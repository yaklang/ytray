import Darwin
import Foundation

enum BrowserEngineNativeHost {
    static let hostName = "com.yaklang.ytray_engine"
    static let extensionID = "mcnaombmlombekhbonfndagbcfhmoail"
    private static let origin = "chrome-extension://" + extensionID
    private static let prefix = "yak-browser-bridge-v1-"
    private static var directory: String { "/tmp/yak-be-\(geteuid())" }

    static func register(in profile: URL, executable: URL) throws {
        let directory = profile.appendingPathComponent("NativeMessagingHosts", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let manifest: [String: Any] = [
            "name": hostName, "description": "YTray Yak engine bridge",
            "path": executable.standardizedFileURL.path, "type": "stdio",
            "allowed_origins": [origin + "/"],
        ]
        try JSONSerialization.data(withJSONObject: manifest)
            .write(to: directory.appendingPathComponent(hostName + ".json"), options: .atomic)
    }

    static func runIfRequested(arguments: [String]) -> Bool {
        guard let caller = arguments.dropFirst().first, caller.hasPrefix("chrome-extension://") else { return false }
        guard caller == origin || caller == origin + "/" else { exit(2) }
        do {
            let input = FileHandle.standardInput
            let output = FileHandle.standardOutput
            let request = try readFrame(input)
            if request["type"] as? String == "yak_engine_discover" {
                try writeFrame(["targets": discover()], to: output)
            } else if request["type"] as? String == "yak_engine_open" {
                try relay(request, input: input, output: output)
            } else {
                throw CocoaError(.fileReadCorruptFile)
            }
        } catch {
            fputs("YTray Native Messaging: \(error.localizedDescription)\n", stderr)
            exit(2)
        }
        return true
    }

    private struct Target: Decodable {
        let socketName: String
        let engineIdentityId: String
        let engineInstanceId: String
        let frontendId: String
        let frontendName: String
        let pid: Int32
        let protocolVersion: Int
        let type: String

        var valid: Bool {
            type == "engine" && protocolVersion == 3 && pid > 0
                && !engineIdentityId.isEmpty && engineIdentityId.utf8.count <= 160
                && !frontendId.isEmpty && frontendId.utf8.count <= 160
                && !frontendName.isEmpty && frontendName.utf8.count <= 160
                && validSocket(socketName, engineInstanceId: engineInstanceId)
        }

        var wire: [String: Any] {
            ["mode": "engine", "exactInstance": true, "socketName": socketName,
             "engineIdentityId": engineIdentityId, "engineInstanceId": engineInstanceId,
             "frontendId": frontendId, "frontendName": frontendName]
        }
    }

    private static func validSocket(_ name: String, engineInstanceId: String) -> Bool {
        engineInstanceId.utf8.count == 39
            && engineInstanceId.range(of: "^engine-[a-f0-9]{32}$", options: .regularExpression) != nil
            && name == prefix + engineInstanceId + ".sock"
    }

    private static func discover() -> [[String: Any]] {
        guard privatePath(directory, kind: S_IFDIR, permissions: 0o700),
              let names = try? FileManager.default.contentsOfDirectory(atPath: directory) else { return [] }
        return names.sorted().compactMap { name -> [String: Any]? in
            guard name.hasPrefix(prefix), name.hasSuffix(".json") else { return nil }
            // O_NOFOLLOW and O_NONBLOCK rule out descriptors that are symlinks or named pipes.
            let fd = open(directory + "/" + name, O_RDONLY | O_NOFOLLOW | O_NONBLOCK)
            guard fd >= 0 else { return nil }
            let file = FileHandle(fileDescriptor: fd, closeOnDealloc: true)
            var info = stat()
            guard fstat(fd, &info) == 0, info.st_mode & S_IFMT == S_IFREG,
                  info.st_uid == geteuid(), info.st_size > 0, info.st_size <= 4096,
                  let data = try? file.read(upToCount: 4096),
                  let hint = try? JSONDecoder().decode(Target.self, from: data), hint.valid,
                  name == prefix + hint.engineInstanceId + ".json",
                  let (socket, pid) = try? connect(hint.socketName, timeout: 400_000), pid == hint.pid else { return nil }
            do {
                try writeFrame(["type": "discover", "engineInstanceId": hint.engineInstanceId,
                                "engineIdentityId": hint.engineIdentityId], to: socket)
                let data = try JSONSerialization.data(withJSONObject: readFrame(socket))
                let target = try JSONDecoder().decode(Target.self, from: data)
                guard target.valid, target.socketName == hint.socketName, target.pid == pid,
                      target.engineIdentityId == hint.engineIdentityId,
                      target.engineInstanceId == hint.engineInstanceId else { return nil }
                return target.wire
            } catch {
                return nil
            }
        }
    }

    private static func relay(_ request: [String: Any], input: FileHandle, output: FileHandle) throws {
        guard let name = request["socketName"] as? String,
              let instance = request["engineInstanceId"] as? String, validSocket(name, engineInstanceId: instance),
              let identity = request["engineIdentityId"] as? String, !identity.isEmpty, identity.utf8.count <= 160,
              let purpose = request["purpose"] as? String, purpose == "pair" || purpose == "connect" else {
            throw CocoaError(.fileReadCorruptFile)
        }
        let (engine, _) = try connect(name, timeout: 0)
        try writeFrame(["type": "open", "origin": origin, "purpose": purpose,
                        "engineInstanceId": instance, "engineIdentityId": identity], to: engine)
        // This Chrome child owns both streams. End the relay when either peer
        // closes, including when the browser closes stdin while the engine is idle.
        let ended = DispatchSemaphore(value: 0)
        DispatchQueue.global().async {
            defer { ended.signal() }
            try? copy(from: input, to: engine)
        }
        DispatchQueue.global().async {
            defer { ended.signal() }
            try? copy(from: engine, to: output)
        }
        ended.wait()
    }

    private static func privatePath(_ path: String, kind: mode_t, permissions: mode_t) -> Bool {
        var info = stat()
        return lstat(path, &info) == 0 && info.st_uid == geteuid()
            && info.st_mode & S_IFMT == kind && info.st_mode & 0o777 == permissions
    }

    private static func connect(_ name: String, timeout: Int32) throws -> (FileHandle, Int32) {
        let path = directory + "/" + name
        guard privatePath(directory, kind: S_IFDIR, permissions: 0o700),
              privatePath(path, kind: S_IFSOCK, permissions: 0o600) else { throw CocoaError(.fileReadNoPermission) }
        let pathBytes = Array(path.utf8) + [UInt8(0)]
        var address = sockaddr_un()
        guard pathBytes.count <= MemoryLayout.size(ofValue: address.sun_path) else {
            throw CocoaError(.fileReadInvalidFileName)
        }
        address.sun_len = UInt8(MemoryLayout<sockaddr_un>.size)
        address.sun_family = sa_family_t(AF_UNIX)
        withUnsafeMutableBytes(of: &address.sun_path) { $0.copyBytes(from: pathBytes) }

        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { throw CocoaError(.fileReadUnknown) }
        let handle = FileHandle(fileDescriptor: fd, closeOnDealloc: true)
        var noPipe: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &noPipe, socklen_t(MemoryLayout.size(ofValue: noPipe)))
        var sendTimeout = timeval(tv_sec: 0, tv_usec: timeout == 0 ? 400_000 : timeout)
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &sendTimeout, socklen_t(MemoryLayout.size(ofValue: sendTimeout)))
        if timeout > 0 {
            var receiveTimeout = timeval(tv_sec: 0, tv_usec: timeout)
            setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &receiveTimeout, socklen_t(MemoryLayout.size(ofValue: receiveTimeout)))
        }
        let result = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        var uid: uid_t = 0
        var gid: gid_t = 0
        var pid: Int32 = 0
        var size = socklen_t(MemoryLayout.size(ofValue: pid))
        guard result == 0, getpeereid(fd, &uid, &gid) == 0, uid == geteuid(),
              getsockopt(fd, SOL_LOCAL, LOCAL_PEERPID, &pid, &size) == 0, pid > 0 else {
            throw CocoaError(.fileReadNoPermission)
        }
        if timeout == 0 {
            var unlimited = timeval()
            setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &unlimited, socklen_t(MemoryLayout.size(ofValue: unlimited)))
        }
        return (handle, pid)
    }

    private static func readFrame(_ source: FileHandle) throws -> [String: Any] {
        func readExactly(_ count: Int) throws -> Data {
            var bytes = Data()
            while bytes.count < count {
                guard let part = try source.read(upToCount: count - bytes.count), !part.isEmpty else {
                    throw CocoaError(.fileReadCorruptFile)
                }
                bytes.append(part)
            }
            return bytes
        }
        let header = try readExactly(4)
        let count = header.enumerated().reduce(0) { $0 | Int($1.element) << ($1.offset * 8) }
        guard count > 0, count <= 4096,
              let frame = try JSONSerialization.jsonObject(with: readExactly(count)) as? [String: Any] else {
            throw CocoaError(.fileReadCorruptFile)
        }
        return frame
    }

    private static func writeFrame(_ frame: [String: Any], to target: FileHandle) throws {
        let data = try JSONSerialization.data(withJSONObject: frame)
        var size = UInt32(data.count).littleEndian
        try target.write(contentsOf: Data(bytes: &size, count: MemoryLayout.size(ofValue: size)))
        try target.write(contentsOf: data)
    }

    private static func copy(from source: FileHandle, to target: FileHandle) throws {
        while true {
            let data = source.availableData
            if data.isEmpty { return }
            try target.write(contentsOf: data)
        }
    }
}
