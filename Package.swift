// swift-tools-version:6.0
import PackageDescription

let package = Package(
    name: "Rewind",
    platforms: [.macOS(.v15)],
    targets: [
        .executableTarget(
            name: "Rewind",
            path: "Sources/Rewind",
            swiftSettings: [.swiftLanguageMode(.v5)]
        )
    ]
)
