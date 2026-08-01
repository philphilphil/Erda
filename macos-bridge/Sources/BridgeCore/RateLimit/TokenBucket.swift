import Foundation

public struct RateLimitDecision: Sendable, Equatable {
    public let allowed: Bool
    /// Whole seconds until one token is available again, ≥ 1. Only meaningful when denied;
    /// this is the value that goes into the `Retry-After` response header.
    public let retryAfterSeconds: Int

    public static let allow = RateLimitDecision(allowed: true, retryAfterSeconds: 0)

    public static func deny(retryAfterSeconds: Int) -> RateLimitDecision {
        RateLimitDecision(allowed: false, retryAfterSeconds: max(1, retryAfterSeconds))
    }
}

/// A continuously refilling token bucket, driven entirely by an injected clock so its behaviour
/// is exactly reproducible in tests.
///
/// `capacity` tokens are handed out at once and refill at `capacity / window` per second, so a
/// burst of `capacity` requests passes and the next one waits `window / capacity` seconds — that
/// is the `Retry-After` value.
public struct TokenBucket: Sendable, Equatable {
    public let capacity: Int
    /// The window over which a full bucket refills, in seconds.
    public let window: TimeInterval

    private var tokens: Double
    private var lastRefill: Date

    public init(capacity: Int, window: TimeInterval, now: Date) {
        precondition(capacity > 0, "a bucket that can never admit a request is a configuration bug")
        precondition(window > 0, "refill window must be positive")
        self.capacity = capacity
        self.window = window
        self.tokens = Double(capacity)
        self.lastRefill = now
    }

    /// Tokens added per second.
    public var refillRate: Double { Double(capacity) / window }

    /// Tokens available at `now`, without consuming anything. Exposed for tests and for the
    /// two-bucket check in `RateLimiter`.
    public func available(at now: Date) -> Double {
        let elapsed = max(0, now.timeIntervalSince(lastRefill))
        return min(Double(capacity), tokens + elapsed * refillRate)
    }

    /// Advances the refill clock without taking a token.
    public mutating func refill(at now: Date) {
        // A clock that went backwards must not manufacture tokens, hence the `max(0, …)` above.
        tokens = available(at: now)
        lastRefill = max(lastRefill, now)
    }

    /// Takes one token if there is one.
    public mutating func consume(at now: Date) -> RateLimitDecision {
        refill(at: now)
        guard tokens >= 1 else {
            let deficit = 1 - tokens
            return .deny(retryAfterSeconds: Int((deficit / refillRate).rounded(.up)))
        }
        tokens -= 1
        return .allow
    }
}
