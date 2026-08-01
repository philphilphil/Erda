import Foundation
import Testing

@testable import BridgeCore

@Suite("Retry backoff")
struct RetryBackoffTests {
    private let backoff = RetryBackoff(initial: .seconds(2), maximum: .seconds(60), multiplier: 2)

    @Test("the first failure waits the initial delay")
    func firstAttempt() {
        #expect(backoff.delay(forAttempt: 1) == .seconds(2))
    }

    @Test("an attempt number below 1 is treated as the first, not as a negative exponent")
    func nonPositiveAttempts() {
        #expect(backoff.delay(forAttempt: 0) == .seconds(2))
        #expect(backoff.delay(forAttempt: -5) == .seconds(2))
    }

    @Test("the delay doubles per attempt", arguments: [
        (2, Duration.seconds(4)),
        (3, .seconds(8)),
        (4, .seconds(16)),
        (5, .seconds(32)),
    ])
    func doubles(attempt: Int, expected: Duration) {
        #expect(backoff.delay(forAttempt: attempt) == expected)
    }

    @Test("the cap holds, and holds for an absurd attempt count without overflowing")
    func caps() {
        #expect(backoff.delay(forAttempt: 6) == .seconds(60))
        #expect(backoff.delay(forAttempt: 40) == .seconds(60))
        #expect(backoff.delay(forAttempt: 10_000) == .seconds(60))
        #expect(backoff.delay(forAttempt: .max) == .seconds(60))
    }

    @Test("a multiplier of 1 is a constant schedule rather than a runaway loop")
    func constantSchedule() {
        let constant = RetryBackoff(initial: .milliseconds(250), maximum: .seconds(5), multiplier: 1)
        #expect(constant.delay(forAttempt: 1) == .milliseconds(250))
        #expect(constant.delay(forAttempt: 99) == .milliseconds(250))
    }

    @Test("a cap below the doubled value truncates rather than overshooting")
    func truncatesToCap() {
        let tight = RetryBackoff(initial: .seconds(2), maximum: .seconds(3), multiplier: 2)
        #expect(tight.delay(forAttempt: 1) == .seconds(2))
        #expect(tight.delay(forAttempt: 2) == .seconds(3))
    }

    @Test("the shipped default is 2s → 60s")
    func defaultSchedule() {
        #expect(RetryBackoff.default.initial == .seconds(2))
        #expect(RetryBackoff.default.maximum == .seconds(60))
    }
}
