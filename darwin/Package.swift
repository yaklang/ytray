// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "instance-dock-darwin",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "InstanceDock", targets: ["InstanceDock"]),
    ],
    targets: [
        .executableTarget(name: "InstanceDock", path: "Sources/InstanceDock"),
        .testTarget(
            name: "InstanceDockTests",
            dependencies: ["InstanceDock"],
            path: "Tests/InstanceDockTests"
        ),
    ]
)
