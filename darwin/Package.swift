// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "ytray-darwin",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "YTray", targets: ["YTray"]),
    ],
    targets: [
        .executableTarget(name: "YTray", path: "Sources/YTray"),
        .testTarget(
            name: "YTrayTests",
            dependencies: ["YTray"],
            path: "Tests/YTrayTests"
        ),
    ]
)
