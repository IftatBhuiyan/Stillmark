// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "Stillmark",
    platforms: [.macOS(.v15)],
    products: [.executable(name: "Stillmark", targets: ["Stillmark"])],
    targets: [
        .executableTarget(name: "Stillmark", swiftSettings: [.swiftLanguageMode(.v5)]),
        .testTarget(name: "StillmarkTests", dependencies: ["Stillmark"])
    ]
)
