// swift-tools-version: 6.2
import PackageDescription

// The module split *is* the security architecture: `BridgeCore` links nothing but Foundation,
// and only `BridgeEventKit` (M4) will link EventKit. `Package.swift` is what makes that a
// compiler-enforced fact rather than a convention.
let package = Package(
    name: "ErdaBridge",
    platforms: [.macOS(.v26)],
    products: [
        .executable(name: "ErdaBridge", targets: ["ErdaBridgeApp"])
    ],
    dependencies: [
        // Sole dependency. NOT Hummingbird: it pulls async-http-client (outbound
        // HTTP — the one primitive this process must not have), swift-nio-ssl and
        // swift-nio-http2.
        .package(url: "https://github.com/apple/swift-nio", from: "2.101.3")
    ],
    targets: [
        // Pure logic: DTOs, strict decoding, the error set, token/rate-limit/idempotency
        // primitives and the `RemindersService` seam. Declares NO dependencies — not EventKit,
        // AppKit, SwiftUI, NIO, SQLite or Security — so `swift test` exercises it on any machine
        // with no bundle, no signing and no TCC prompt.
        .target(
            name: "BridgeCore",
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        // Persistence: raw SQLite3 (in the SDK, zero packages), the legacy Keychain, and a
        // rotating JSONL audit file. Links Security + CryptoKit — but still not EventKit.
        .target(
            name: "BridgeStore",
            dependencies: ["BridgeCore"],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        // The request layer. Depends on NIO and on `BridgeCore` — and deliberately NOT on
        // `BridgeEventKit` or `BridgeStore`: every service it needs arrives through a
        // `BridgeCore` protocol, so the whole HTTP surface is exercisable against fakes.
        //
        // The omission is a declared boundary, not a compiler-enforced one. Verified 2026-08-01:
        // SwiftPM shares one module search path across the targets of a *single* package, so
        // adding `import BridgeEventKit` here still compiles despite the missing edge — and
        // `import EventKit` always would, since system frameworks need no declaration at all.
        // What the omission does buy is that the coupling cannot appear without also appearing
        // in this file or in a diff full of new imports. M7's `scripts/lint-forbidden.sh` is
        // what turns it into a build failure; it must reject `import EventKit` and
        // `import BridgeEventKit` anywhere under `Sources/` outside `Sources/BridgeEventKit`.
        .target(
            name: "BridgeHTTP",
            dependencies: [
                "BridgeCore",
                .product(name: "NIOCore", package: "swift-nio"),
                .product(name: "NIOPosix", package: "swift-nio"),
                .product(name: "NIOHTTP1", package: "swift-nio"),
            ],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        // The ONLY target that links EventKit. It implements `BridgeCore.RemindersService` and
        // depends on nothing but `BridgeCore` — not NIO, not SQLite — so the framework and the
        // request layer can never meet except through a `Sendable` DTO.
        .target(
            name: "BridgeEventKit",
            dependencies: ["BridgeCore"],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        // The composition root, and the one place all four modules are visible at once.
        .executableTarget(
            name: "ErdaBridgeApp",
            dependencies: ["BridgeCore", "BridgeStore", "BridgeHTTP", "BridgeEventKit"],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        .testTarget(
            name: "BridgeCoreTests",
            dependencies: ["BridgeCore"],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        .testTarget(
            name: "BridgeStoreTests",
            dependencies: ["BridgeStore"],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        .testTarget(
            name: "BridgeHTTPTests",
            dependencies: [
                "BridgeHTTP",
                .product(name: "NIOCore", package: "swift-nio"),
                .product(name: "NIOPosix", package: "swift-nio"),
            ],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        // Most of these need no EventKit at all. The handful that do are gated on
        // ERDA_BRIDGE_EVENTKIT_TESTS=1 plus ERDA_BRIDGE_TEST_LIST naming a throwaway list, and
        // skip themselves otherwise — a plain `swift test` never touches real Reminders data.
        .testTarget(
            name: "BridgeEventKitTests",
            dependencies: ["BridgeEventKit", "BridgeCore"],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
    ]
)
