import Foundation

/// A stored idempotency row (design dossier §4.3). Persistence is `BridgeStore`'s job; this is
/// the shape the decision function reads.
public struct IdempotencyRecord: Sendable, Equatable {
    public let key: String
    /// `SHA-256(RequestHash.preimage(...))` of the request that claimed the key.
    public let requestHash: [UInt8]
    /// `nil` while the request is still in flight.
    public let status: Int?
    public let responseBody: [UInt8]?
    public let createdAt: Date

    public init(key: String, requestHash: [UInt8], status: Int?, responseBody: [UInt8]?, createdAt: Date) {
        self.key = key
        self.requestHash = requestHash
        self.status = status
        self.responseBody = responseBody
        self.createdAt = createdAt
    }

    public var isInFlight: Bool { status == nil }
}

/// What the request layer should do with a mutation carrying an `Idempotency-Key`.
public enum IdempotencyOutcome: Sendable, Equatable {
    /// The key is ours; run the handler, then record the result.
    case proceed
    /// A completed request with the same key and the same body: return the stored response
    /// verbatim, with `Idempotency-Replayed: true`.
    case replay(status: Int, body: [UInt8])
    /// The same key with a *different* body — the client reused a key for a different request.
    case conflictKeyReuse
    /// The same key and body, still running. Retrying is legitimate; the answer is not ready.
    case conflictInProgress

    public var apiError: ApiError? {
        switch self {
        case .proceed, .replay: nil
        case .conflictKeyReuse: .idempotencyKeyReuse
        case .conflictInProgress: .requestInProgress
        }
    }
}

/// The idempotency decision, isolated from storage so all four states are testable directly.
public enum Idempotency {
    /// TTL after which a row is swept and its key becomes reusable.
    public static let ttl: TimeInterval = 24 * 60 * 60

    /// - Parameters:
    ///   - stored: the row already holding this key, or `nil` if the insert succeeded.
    ///   - requestHash: the digest of the incoming request.
    public static func decide(stored: IdempotencyRecord?, requestHash: [UInt8]) -> IdempotencyOutcome {
        guard let stored else { return .proceed }

        // Hash mismatch is checked first: a different body under the same key is a client bug,
        // and it must be reported as such whether or not the original has finished.
        guard ConstantTime.equal(stored.requestHash, requestHash) else { return .conflictKeyReuse }

        guard let status = stored.status else { return .conflictInProgress }
        // A completed row with no body replays as an empty body — 204-shaped responses are
        // legitimate and must not be mistaken for "still running".
        return .replay(status: status, body: stored.responseBody ?? [])
    }

    /// Whether a row has aged out. Rows older than the TTL are swept at startup and hourly.
    public static func isExpired(_ record: IdempotencyRecord, now: Date) -> Bool {
        now.timeIntervalSince(record.createdAt) >= ttl
    }
}

/// The storage seam for idempotency, so the request layer can enforce it without depending on
/// SQLite — the same shape as `AuditSink` and `RemindersService`. `BridgeStore`'s
/// `IdempotencyRepository` is the real implementation.
public protocol IdempotencyStore: Sendable {
    /// Claims `key`, or reports what it already means. Must be atomic against a concurrent
    /// claim of the same key: exactly one caller may receive `.proceed`.
    func claim(key: String, requestHash: [UInt8]) throws -> IdempotencyOutcome
    /// Records the finished response so a later retry replays it.
    func complete(key: String, status: Int, body: [UInt8]) throws
    /// Drops the in-flight row after a failure so the retry can proceed.
    @discardableResult
    func abandon(key: String) throws -> Bool
}

/// An in-memory `IdempotencyStore`. Lives in the library for the same reason as `FakeReminders`:
/// `BridgeHTTP` does not depend on `BridgeStore`, so its socket tests need a store they can
/// reach — and the claim has to be genuinely atomic for those tests to mean anything.
public final class MemoryIdempotencyStore: IdempotencyStore, @unchecked Sendable {
    private let lock = NSLock()
    private var records: [String: IdempotencyRecord] = [:]
    private let clock: any BridgeClock

    public init(clock: any BridgeClock = SystemClock()) {
        self.clock = clock
    }

    public func claim(key: String, requestHash: [UInt8]) throws -> IdempotencyOutcome {
        lock.lock()
        defer { lock.unlock() }

        let now = clock.now
        var stored = records[key]
        if let existing = stored, Idempotency.isExpired(existing, now: now) {
            records[key] = nil
            stored = nil
        }

        let outcome = Idempotency.decide(stored: stored, requestHash: requestHash)
        if outcome == .proceed {
            records[key] = IdempotencyRecord(
                key: key,
                requestHash: requestHash,
                status: nil,
                responseBody: nil,
                createdAt: now
            )
        }
        return outcome
    }

    public func complete(key: String, status: Int, body: [UInt8]) throws {
        lock.lock()
        defer { lock.unlock() }
        guard let existing = records[key], existing.isInFlight else { return }
        records[key] = IdempotencyRecord(
            key: key,
            requestHash: existing.requestHash,
            status: status,
            responseBody: body,
            createdAt: existing.createdAt
        )
    }

    @discardableResult
    public func abandon(key: String) throws -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard let existing = records[key], existing.isInFlight else { return false }
        records[key] = nil
        return true
    }

    public func record(for key: String) -> IdempotencyRecord? {
        lock.lock()
        defer { lock.unlock() }
        return records[key]
    }
}
