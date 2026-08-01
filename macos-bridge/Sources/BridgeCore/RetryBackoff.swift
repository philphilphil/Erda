import Foundation

/// The wait between listener start attempts, as a pure schedule.
///
/// It lives here rather than inside the supervisor so the arithmetic — which is the part with the
/// off-by-one and the overflow in it — is testable without a socket, a clock or a running app.
/// The supervisor only decides *when* to ask; this decides *how long*.
///
/// Attempts are numbered from 1: `delay(forAttempt: 1)` is the pause after the first failure.
public struct RetryBackoff: Sendable, Equatable {
    public let initial: Duration
    public let maximum: Duration
    public let multiplier: Int

    /// Two seconds doubling to a minute. A DHCP lease usually settles inside the first few
    /// attempts; capping at a minute keeps a Mac that is simply off the network from spinning.
    public static let `default` = RetryBackoff()

    public init(
        initial: Duration = .seconds(2),
        maximum: Duration = .seconds(60),
        multiplier: Int = 2
    ) {
        precondition(initial > .zero, "a zero initial delay would busy-loop the supervisor")
        precondition(maximum >= initial, "the cap must not be below the first delay")
        precondition(multiplier >= 1, "a multiplier below 1 would shorten the wait on every failure")
        self.initial = initial
        self.maximum = maximum
        self.multiplier = multiplier
    }

    public func delay(forAttempt attempt: Int) -> Duration {
        guard attempt > 1 else { return initial }

        var delay = initial
        // Bounded so a long-running failure cannot turn this into a long loop, and so `delay`
        // can never be multiplied enough times to overflow: with a multiplier of 2 the cap is
        // reached in well under 64 steps, and with a multiplier of 1 nothing grows at all.
        for _ in 1..<min(attempt, 64) {
            delay = delay * multiplier
            if delay >= maximum { return maximum }
        }
        return delay
    }
}
