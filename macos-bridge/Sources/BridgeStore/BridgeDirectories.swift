import Foundation

/// POSIX modes used throughout the store.
///
/// `FileManager` defaults to 0755 for directories and 0644 for files, which on a shared Mac
/// would leave the id map and the audit log readable by every other user account. Every
/// directory and file this module creates is set explicitly.
public enum FilePermissions {
    public static let directory: Int16 = 0o700
    public static let file: Int16 = 0o600

    /// Creates `url` as a 0700 directory, and repairs the mode if it already exists with a
    /// looser one — `createDirectory` silently does nothing for an existing path, so without
    /// this a directory created by an earlier, laxer build would stay world-readable forever.
    public static func createDirectory(at url: URL) throws {
        try FileManager.default.createDirectory(
            at: url,
            withIntermediateDirectories: true,
            attributes: [.posixPermissions: NSNumber(value: directory)]
        )
        try FileManager.default.setAttributes(
            [.posixPermissions: NSNumber(value: directory)],
            ofItemAtPath: url.path
        )
    }

    /// Sets 0600 on an existing file. Missing files are ignored: SQLite creates `-wal` and
    /// `-shm` lazily, so hardening runs over paths that may not exist yet.
    public static func hardenFile(at url: URL) throws {
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        try FileManager.default.setAttributes(
            [.posixPermissions: NSNumber(value: file)],
            ofItemAtPath: url.path
        )
    }

    /// The current mode of a path, for tests and the self-test.
    public static func mode(of url: URL) throws -> Int16 {
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        return (attributes[.posixPermissions] as? NSNumber)?.int16Value ?? -1
    }
}

/// Where the bridge keeps its state.
///
/// There is no App Sandbox, so there is no container: these are the real user paths from
/// dossier §4.4. `~/Library/Application Support/<own subdirectory>` is not TCC-protected on
/// macOS, unlike `~/Documents` or `~/Desktop`, so no prompt is involved.
public struct BridgeDirectories: Sendable, Equatable {
    /// The permanent TCC identity. Chosen once, never changed.
    public static let bundleIdentifier = "de.philippbaum.erdabridge"
    public static let logDirectoryName = "ErdaBridge"

    public let applicationSupport: URL
    public let logs: URL

    public init(applicationSupport: URL, logs: URL) {
        self.applicationSupport = applicationSupport
        self.logs = logs
    }

    /// The production locations.
    public static func standard() throws -> BridgeDirectories {
        let support = try FileManager.default.url(
            for: .applicationSupportDirectory,
            in: .userDomainMask,
            appropriateFor: nil,
            create: false
        ).appendingPathComponent(bundleIdentifier, isDirectory: true)

        let library = try FileManager.default.url(
            for: .libraryDirectory,
            in: .userDomainMask,
            appropriateFor: nil,
            create: false
        )

        return BridgeDirectories(
            applicationSupport: support,
            logs: library.appendingPathComponent("Logs", isDirectory: true)
                .appendingPathComponent(logDirectoryName, isDirectory: true)
        )
    }

    public var databaseURL: URL { applicationSupport.appendingPathComponent("bridge.sqlite") }
    public var walURL: URL { applicationSupport.appendingPathComponent("bridge.sqlite-wal") }
    public var sharedMemoryURL: URL { applicationSupport.appendingPathComponent("bridge.sqlite-shm") }
    public var tokenFileURL: URL { applicationSupport.appendingPathComponent("token.json") }
    public var auditLogURL: URL { logs.appendingPathComponent("audit.jsonl") }

    /// Creates both directories 0700.
    public func create() throws {
        try FilePermissions.createDirectory(at: applicationSupport)
        try FilePermissions.createDirectory(at: logs)
    }

    /// Re-applies 0600 to the database and its sidecar files. Called after opening, because
    /// `-wal` and `-shm` do not exist until the first write in WAL mode.
    public func hardenDatabaseFiles() throws {
        for url in [databaseURL, walURL, sharedMemoryURL] {
            try FilePermissions.hardenFile(at: url)
        }
    }
}
