import BridgeCore
import Foundation

/// The audit log: append-only JSONL on disk, rotated by size.
///
/// It is deliberately not a SQLite table (dossier §4.2). Its whole job is to survive and stay
/// readable when something has gone wrong, which means it must not be lockable by a transaction,
/// must be `tail -f`-able and `jq`-able while the bridge runs, and must not be corruptible by a
/// bug in the database layer.
public final class RotatingJSONLAuditSink: AuditSink, @unchecked Sendable {
    public static let defaultMaxBytes = 5 * 1024 * 1024
    /// `audit.jsonl` plus four archives.
    public static let defaultKeptFiles = 5

    private let lock = NSLock()
    private let directory: URL
    private let baseName: String
    private let maxBytes: Int
    private let keptFiles: Int

    private var handle: FileHandle?
    private var currentBytes: Int = 0
    private var failures = 0
    private var lastFailure: String?

    /// - Parameters:
    ///   - keptFiles: total files retained, including the live one. Must be at least 1.
    public init(
        directory: URL,
        baseName: String = "audit",
        maxBytes: Int = RotatingJSONLAuditSink.defaultMaxBytes,
        keptFiles: Int = RotatingJSONLAuditSink.defaultKeptFiles
    ) throws {
        precondition(maxBytes > 0, "a zero-byte cap would rotate on every line")
        precondition(keptFiles >= 1, "at least the live file must be kept")
        self.directory = directory
        self.baseName = baseName
        self.maxBytes = maxBytes
        self.keptFiles = keptFiles

        try FilePermissions.createDirectory(at: directory)
        try openCurrent()
    }

    deinit {
        try? handle?.close()
    }

    public var currentURL: URL { directory.appendingPathComponent("\(baseName).jsonl") }

    func archiveURL(_ index: Int) -> URL {
        directory.appendingPathComponent("\(baseName).\(index).jsonl")
    }

    /// Never throws and never blocks a request on a logging problem. A failure is counted and
    /// surfaced through `lastError` for the status UI; the request itself still completes.
    public func record(_ event: AuditEvent) {
        lock.lock()
        defer { lock.unlock() }

        do {
            let payload = Data((try event.jsonLine() + "\n").utf8)
            if currentBytes > 0, currentBytes + payload.count > maxBytes {
                try rotate()
            }
            guard let handle else { throw AuditSinkError.notOpen }
            try handle.write(contentsOf: payload)
            currentBytes += payload.count
        } catch {
            failures += 1
            lastFailure = String(describing: error)
        }
    }

    public func flush() {
        lock.lock()
        defer { lock.unlock() }
        try? handle?.synchronize()
    }

    public func close() {
        lock.lock()
        defer { lock.unlock() }
        try? handle?.close()
        handle = nil
    }

    public var failureCount: Int {
        lock.lock()
        defer { lock.unlock() }
        return failures
    }

    public var lastError: String? {
        lock.lock()
        defer { lock.unlock() }
        return lastFailure
    }

    public var currentByteCount: Int {
        lock.lock()
        defer { lock.unlock() }
        return currentBytes
    }

    // MARK: - Internals

    private func openCurrent() throws {
        let url = currentURL
        if !FileManager.default.fileExists(atPath: url.path) {
            FileManager.default.createFile(
                atPath: url.path,
                contents: nil,
                attributes: [.posixPermissions: NSNumber(value: FilePermissions.file)]
            )
        }
        // Repairs the mode of a file created by an earlier build, and of one created above on a
        // filesystem that ignored the attribute.
        try FilePermissions.hardenFile(at: url)

        let opened = try FileHandle(forWritingTo: url)
        currentBytes = Int(try opened.seekToEnd())
        handle = opened
    }

    /// `audit.3 → audit.4`, …, `audit.jsonl → audit.1`, then a fresh `audit.jsonl`.
    private func rotate() throws {
        try handle?.close()
        handle = nil

        let manager = FileManager.default
        let archives = keptFiles - 1

        if archives == 0 {
            // Only the live file is kept: truncate rather than archive.
            try? manager.removeItem(at: currentURL)
            try openCurrent()
            return
        }

        // The oldest archive falls off the end.
        try? manager.removeItem(at: archiveURL(archives))
        for index in stride(from: archives - 1, through: 1, by: -1) {
            let source = archiveURL(index)
            guard manager.fileExists(atPath: source.path) else { continue }
            try? manager.removeItem(at: archiveURL(index + 1))
            try manager.moveItem(at: source, to: archiveURL(index + 1))
        }

        if manager.fileExists(atPath: currentURL.path) {
            try? manager.removeItem(at: archiveURL(1))
            try manager.moveItem(at: currentURL, to: archiveURL(1))
        }

        currentBytes = 0
        try openCurrent()
    }
}

public enum AuditSinkError: Error, Equatable {
    case notOpen
}
