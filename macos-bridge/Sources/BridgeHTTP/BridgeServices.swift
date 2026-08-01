import BridgeCore
import Foundation

/// Everything the request layer needs, all of it behind a `BridgeCore` protocol or a closure.
///
/// Nothing here names EventKit or SQLite: swapping `FakeReminders` for the real EventKit actor
/// in M4, or the in-memory idempotency store for the SQLite one, is a change at the call site
/// that constructs this value and nowhere else.
public struct BridgeServices: Sendable {
    /// The seam from dossier §6.2.
    public var reminders: any RemindersService
    /// Re-read per request rather than captured once: the setup UI can re-bind an alias, and a
    /// list going `broken` has to take effect without a restart.
    public var allowlist: @Sendable () async -> Allowlist
    /// `nil` when no token has been generated — every request then 401s, which is the correct
    /// fail-closed answer for a bridge with no credential.
    public var tokenVerifier: @Sendable () async -> TokenVerifier?
    public var rateLimiter: RateLimiter
    public var idempotency: any IdempotencyStore
    public var audit: any AuditSink
    /// `BridgeCore` defines the SHA-256 seam but links no crypto; the caller injects CryptoKit.
    public var hasher: any Sha256Hasher
    public var clock: any BridgeClock

    public init(
        reminders: any RemindersService,
        allowlist: @escaping @Sendable () async -> Allowlist,
        tokenVerifier: @escaping @Sendable () async -> TokenVerifier?,
        rateLimiter: RateLimiter,
        idempotency: any IdempotencyStore,
        audit: any AuditSink,
        hasher: any Sha256Hasher,
        clock: any BridgeClock = SystemClock()
    ) {
        self.reminders = reminders
        self.allowlist = allowlist
        self.tokenVerifier = tokenVerifier
        self.rateLimiter = rateLimiter
        self.idempotency = idempotency
        self.audit = audit
        self.hasher = hasher
        self.clock = clock
    }
}

/// Wire-level limits and timeouts. The defaults are the ones from dossier §2.2.
public struct BridgeServerConfiguration: Sendable {
    public var host: String
    public var port: Int

    /// Beyond this, a newly accepted connection is closed without being read.
    public var maxConcurrentConnections = 8

    public var maxHeaderFieldSize = 16 * 1024
    public var maxHeaderListSize = 16 * 1024
    public var maxHeaderFieldCount = 64
    public var maxBodyBytes = 16 * 1024

    public var readTimeoutSeconds: Int64 = 5
    public var writeTimeoutSeconds: Int64 = 5
    public var allTimeoutSeconds: Int64 = 20

    public var backlog: Int32 = 16

    public init(host: String, port: Int) {
        self.host = host
        self.port = port
    }

    public init(bindAddress: BindAddress) {
        self.host = bindAddress.ipAddress
        self.port = bindAddress.port
    }
}
