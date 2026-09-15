// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "ytray-darwin",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "YTray", targets: ["YTray"]),
    ],
    targets: [
        .executableTarget(name: "YTray", dependencies: ["Sparkle"], path: "Sources/YTray",
            linkerSettings: [.unsafeFlags(["-Xlinker", "-rpath", "-Xlinker", "@executable_path/../Frameworks"])]),
        .binaryTarget(name: "Sparkle",
            url: "https://github.com/sparkle-project/Sparkle/releases/download/2.9.6/Sparkle-for-Swift-Package-Manager.zip",
            checksum: "8d5fb41d960b43f4a68aa14126bf62b098544ec8d191cdcc73eb14e63a8e7606"),
        .testTarget(
            name: "YTrayTests",
            dependencies: ["YTray"],
            path: "Tests/YTrayTests"
        ),
    ]
)
