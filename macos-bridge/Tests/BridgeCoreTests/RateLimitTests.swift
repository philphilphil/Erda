import Foundation
import Testing

@testable import BridgeCore

@Suite("Token bucket")
struct TokenBucketTests {
    private let start = Date(timeIntervalSince1970: 1_780_000_000)

    @Test("a full bucket admits exactly its capacity, then denies")
    func admitsCapacityThenDenies() {
        var bucket = TokenBucket(capacity: 30, window: 60, now: start)
        for request in 1...30 {
            #expect(bucket.consume(at: start).allowed, "request \(request) should pass")
        }
        let denied = bucket.consume(at: start)
        #expect(!denied.allowed)
        // 30 tokens per 60 s is one token every 2 s, and the bucket is empty.
        #expect(denied.retryAfterSeconds == 2)
    }

    @Test("Retry-After reflects the refill rate")
    func retryAfterMatchesRate() {
        var tight = TokenBucket(capacity: 10, window: 60, now: start)
        for _ in 0..<10 { _ = tight.consume(at: start) }
        #expect(tight.consume(at: start).retryAfterSeconds == 6)

        var slow = TokenBucket(capacity: 1, window: 60, now: start)
        _ = slow.consume(at: start)
        #expect(slow.consume(at: start).retryAfterSeconds == 60)
    }

    @Test("Retry-After is never zero, even a hair before a refill")
    func retryAfterIsAtLeastOneSecond() {
        var bucket = TokenBucket(capacity: 30, window: 60, now: start)
        for _ in 0..<30 { _ = bucket.consume(at: start) }
        // 1.99 s in, the next token is 0.01 s away — which must not round down to `Retry-After: 0`.
        let decision = bucket.consume(at: start.addingTimeInterval(1.99))
        #expect(!decision.allowed)
        #expect(decision.retryAfterSeconds == 1)
    }

    @Test("tokens refill over time and never exceed capacity")
    func refills() {
        var bucket = TokenBucket(capacity: 30, window: 60, now: start)
        for _ in 0..<30 { _ = bucket.consume(at: start) }
        #expect(!bucket.consume(at: start).allowed)

        // 2 s buys exactly one token.
        #expect(bucket.consume(at: start.addingTimeInterval(2)).allowed)
        #expect(!bucket.consume(at: start.addingTimeInterval(2)).allowed)

        // An hour of quiet refills to capacity and no further.
        let later = start.addingTimeInterval(3600)
        #expect(bucket.available(at: later) == 30)
        for _ in 0..<30 { #expect(bucket.consume(at: later).allowed) }
        #expect(!bucket.consume(at: later).allowed)
    }

    @Test("a clock that jumps backwards cannot manufacture tokens")
    func ignoresBackwardsClock() {
        var bucket = TokenBucket(capacity: 5, window: 60, now: start)
        for _ in 0..<5 { _ = bucket.consume(at: start) }
        #expect(!bucket.consume(at: start.addingTimeInterval(-3600)).allowed)
    }
}

@Suite("Rate limiter")
struct RateLimiterTests {
    private func makeLimiter(_ clock: ManualClock) -> RateLimiter {
        RateLimiter(clock: clock)
    }

    private func tokenId(_ raw: String) throws -> TokenId {
        try #require(TokenId(rawValue: raw))
    }

    @Test("30 reads a minute pass; the 31st is limited")
    func globalBudget() async throws {
        let clock = ManualClock()
        let limiter = makeLimiter(clock)
        let token = try tokenId("a1b2c3d4")

        for request in 1...30 {
            let decision = await limiter.admit(tokenId: token, class: .read)
            #expect(decision.allowed, "read \(request) should pass")
        }

        let denied = await limiter.admit(tokenId: token, class: .read)
        #expect(!denied.allowed)
        #expect(denied.retryAfterSeconds == 2)
    }

    @Test("mutations exhaust their own bucket at 10, while reads keep working")
    func mutationBudgetIsIndependent() async throws {
        let clock = ManualClock()
        let limiter = makeLimiter(clock)
        let token = try tokenId("a1b2c3d4")

        for request in 1...10 {
            let decision = await limiter.admit(tokenId: token, class: .mutation)
            #expect(decision.allowed, "mutation \(request) should pass")
        }

        let denied = await limiter.admit(tokenId: token, class: .mutation)
        #expect(!denied.allowed)
        #expect(denied.retryAfterSeconds == 6)

        // 10 of the 30 global tokens went to the mutations; the remaining 20 reads must pass.
        for request in 1...20 {
            let decision = await limiter.admit(tokenId: token, class: .read)
            #expect(decision.allowed, "read \(request) should pass")
        }
        let readDenied = await limiter.admit(tokenId: token, class: .read)
        #expect(!readDenied.allowed)
    }

    @Test("a rejected mutation does not spend a global token")
    func deniedMutationRefundsTheGlobalBucket() async throws {
        let clock = ManualClock()
        let limiter = makeLimiter(clock)
        let token = try tokenId("a1b2c3d4")

        for _ in 0..<10 { _ = await limiter.admit(tokenId: token, class: .mutation) }
        // Five more mutations, all rejected by the mutation bucket.
        for _ in 0..<5 {
            let decision = await limiter.admit(tokenId: token, class: .mutation)
            #expect(!decision.allowed)
        }

        // Had those five burned global tokens, only 15 reads would be left.
        for request in 1...20 {
            let decision = await limiter.admit(tokenId: token, class: .read)
            #expect(decision.allowed, "read \(request) should pass")
        }
    }

    @Test("the budget refills as the injected clock advances")
    func refillsWithTheClock() async throws {
        let clock = ManualClock()
        let limiter = makeLimiter(clock)
        let token = try tokenId("a1b2c3d4")

        for _ in 0..<30 { _ = await limiter.admit(tokenId: token, class: .read) }
        let denied = await limiter.admit(tokenId: token, class: .read)
        #expect(!denied.allowed)

        clock.advance(by: TimeInterval(denied.retryAfterSeconds))
        let afterWait = await limiter.admit(tokenId: token, class: .read)
        #expect(afterWait.allowed)

        clock.advance(by: 60)
        for request in 1...30 {
            let decision = await limiter.admit(tokenId: token, class: .read)
            #expect(decision.allowed, "read \(request) should pass after a full window")
        }
    }

    @Test("buckets are per token id")
    func isolatesTokens() async throws {
        let clock = ManualClock()
        let limiter = makeLimiter(clock)
        let first = try tokenId("a1b2c3d4")
        let second = try tokenId("00ff00ff")

        for _ in 0..<30 { _ = await limiter.admit(tokenId: first, class: .read) }
        let firstDenied = await limiter.admit(tokenId: first, class: .read)
        #expect(!firstDenied.allowed)

        let secondAllowed = await limiter.admit(tokenId: second, class: .read)
        #expect(secondAllowed.allowed)

        let tracked = await limiter.trackedTokenCount
        #expect(tracked == 2)
    }

    @Test("time does not pass unless the test says so")
    func manualClockIsInert() {
        let clock = ManualClock(now: Date(timeIntervalSince1970: 1_000))
        #expect(clock.now == Date(timeIntervalSince1970: 1_000))
        clock.advance(by: 42)
        #expect(clock.now == Date(timeIntervalSince1970: 1_042))
        clock.set(Date(timeIntervalSince1970: 7))
        #expect(clock.now == Date(timeIntervalSince1970: 7))
    }
}
