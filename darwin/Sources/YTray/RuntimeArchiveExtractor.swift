import Foundation

/// Owns one extraction process. State transitions are serialized; stderr is
/// drained while the child runs so a full pipe cannot prevent termination.
final class RuntimeArchiveExtractor: @unchecked Sendable {
    static let maximumErrorBytes = 32 * 1024
    private let queue = DispatchQueue(label: "io.yaklang.ytray.runtime-extraction")
    private var process: Process?
    private var continuation: CheckedContinuation<Void, Error>?
    private var cancelled = false
    private var exitStatus: Int32?
    private var errorOutput: Data?
    private var errorTruncated = false

    func run(executable: URL, arguments: [String]) async throws {
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                queue.async { self.start(executable: executable, arguments: arguments, continuation: continuation) }
            }
        } onCancel: {
            self.queue.async {
                self.cancelled = true
                if let process = self.process, process.isRunning { process.terminate() }
            }
        }
    }

    private func start(executable: URL, arguments: [String], continuation: CheckedContinuation<Void, Error>) {
        guard !cancelled else { continuation.resume(throwing: CancellationError()); return }
        self.continuation = continuation
        let child = Process()
        child.executableURL = executable
        child.arguments = arguments
        child.standardInput = FileHandle.nullDevice
        child.standardOutput = FileHandle.nullDevice
        let pipe = Pipe()
        child.standardError = pipe.fileHandleForWriting
        child.terminationHandler = { [weak self] child in
            guard let self else { return }
            self.queue.async {
                self.exitStatus = child.terminationStatus
                self.finishIfReady()
            }
        }
        process = child
        do {
            try child.run()
            // The child owns its duplicated descriptor. Close our writer so EOF
            // arrives after it exits, even while Process retains standardError.
            try? pipe.fileHandleForWriting.close()
            DispatchQueue.global(qos: .utility).async { [self] in
                var output = Data(), truncated = false
                let reader = pipe.fileHandleForReading
                defer { try? reader.close() }
                do {
                    while let bytes = try reader.read(upToCount: 8192), !bytes.isEmpty {
                        let keep = min(bytes.count, max(0, Self.maximumErrorBytes - output.count))
                        output.append(bytes.prefix(keep))
                        truncated = truncated || keep < bytes.count
                    }
                } catch {
                    // A closed/failed diagnostic stream must not strand the
                    // continuation; the process exit status still decides success.
                }
                let captured = output, wasTruncated = truncated
                queue.async {
                    self.errorOutput = captured
                    self.errorTruncated = wasTruncated
                    self.finishIfReady()
                }
            }
        } catch {
            child.terminationHandler = nil
            process = nil
            self.continuation = nil
            try? pipe.fileHandleForWriting.close()
            try? pipe.fileHandleForReading.close()
            continuation.resume(throwing: error)
        }
    }

    private func finishIfReady() {
        guard let exitStatus, let errorOutput, let continuation else { return }
        self.continuation = nil
        process?.terminationHandler = nil
        process = nil
        if cancelled { continuation.resume(throwing: CancellationError()); return }
        guard exitStatus == 0 else {
            var message = String(decoding: errorOutput, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
            if message.isEmpty { message = "ZIP 解压失败（退出码 \(exitStatus)）" }
            if errorTruncated { message += "\n…错误输出已截断" }
            continuation.resume(throwing: YTrayError.downloadFailed(message))
            return
        }
        continuation.resume()
    }
}
