import Foundation

/// Which buckets a request draws from.
public enum RateLimitClass: Sendable, Hashable {
    /// Reads: the global bucket only.
    case read
    /// Mutations: the global bucket **and** the tighter mutation bucket.
    case mutation
}

/// Per-token rate limiting: 30 requests/minute overall, of which at most 10/minute may be
/// mutations (design dossier §2.3).
///
/// An actor rather than a lock, because it is only ever touched from the connection tasks.
public actor RateLimiter {
    public static let defaultGlobalCapacity = 30
    public static let defaultMutationCapacity = 10
    public static let defaultWindow: TimeInterval = 60

    private struct Buckets {
        var global: TokenBucket
        var mutation: TokenBucket
    }

    private let clock: any BridgeClock
    private let globalCapacity: Int
    private let mutationCapacity: Int
    private let window: TimeInterval
    private var buckets: [TokenId: Buckets] = [:]

    public init(
        clock: any BridgeClock,
        globalCapacity: Int = RateLimiter.defaultGlobalCapacity,
        mutationCapacity: Int = RateLimiter.defaultMutationCapacity,
        window: TimeInterval = RateLimiter.defaultWindow
    ) {
        self.clock = clock
        self.globalCapacity = globalCapacity
        self.mutationCapacity = mutationCapacity
        self.window = window
    }

    /// Charges one request against `tokenId`.
    ///
    /// A mutation must clear both buckets before either is charged: if the mutation bucket is
    /// empty, the global token is put back, so a client that hammers mutations does not also burn
    /// through its read budget.
    public func admit(tokenId: TokenId, class kind: RateLimitClass) -> RateLimitDecision {
        let now = clock.now
        var current = buckets[tokenId] ?? Buckets(
            global: TokenBucket(capacity: globalCapacity, window: window, now: now),
            mutation: TokenBucket(capacity: mutationCapacity, window: window, now: now)
        )

        let globalDecision = current.global.consume(at: now)
        guard globalDecision.allowed else {
            // Persist the refill bookkeeping; nothing was consumed.
            buckets[tokenId] = current
            return globalDecision
        }

        if kind == .mutation {
            let mutationDecision = current.mutation.consume(at: now)
            guard mutationDecision.allowed else {
                // Roll the global charge back by discarding the mutated copy: the stored bucket
                // still refills from its own timestamp, so nothing is lost by not persisting.
                return mutationDecision
            }
        }

        buckets[tokenId] = current
        return .allow
    }

    /// Number of tokens currently tracked — used by tests to assert per-token isolation.
    public var trackedTokenCount: Int { buckets.count }
}
