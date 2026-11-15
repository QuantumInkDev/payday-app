// swift-tools-version: 6.0
// PayDayKit: the shared Swift core of FutureCheck for macOS and iOS (models, core rules, sync).
// The contract it implements is the repository's spec/ folder; the tests run every fixture case in
// spec/conformance/ straight from the source tree.

import PackageDescription

let package = Package(
    name: "PayDayKit",
    platforms: [
        .macOS(.v15),
        .iOS(.v18),
    ],
    products: [
        .library(name: "PayDayKit", targets: ["PayDayKit"]),
    ],
    targets: [
        .target(
            name: "PayDayKit",
            resources: [
                // spec/bip39-english.txt, embedded unchanged (SHA-256 checked at load and in a test).
                .copy("Resources/bip39-english.txt"),
            ]
        ),
        .testTarget(
            name: "PayDayKitTests",
            dependencies: ["PayDayKit"]
        ),
    ],
    swiftLanguageModes: [.v6]
)
