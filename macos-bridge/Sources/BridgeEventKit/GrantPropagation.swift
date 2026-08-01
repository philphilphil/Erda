import BridgeCore
import Foundation

/// TCC's grant propagation is asynchronous, and this is the one place that copes with it.
///
/// `requestFullAccessToEvents` can return `granted == true` while
/// `EKEventStore.authorizationStatus(for: .event)` still answers `.notDetermined`: tccd's reply
/// reaches this process on its own schedule, and a read on the *next line* races it. Observed on
/// macOS 26, from `~/Library/Logs/ErdaBridge/authorization.log`, on the first-ever calendar grant:
///
/// ```
/// 17:34:08 calendar: requesting full access (status before: notDetermined)
/// 17:34:10 calendar: request returned granted=true, status now notDetermined
/// ```
///
/// It then *stayed* `notDetermined`. The setup window said access had not been granted and
/// `GET /v1/status` kept answering `calendarAvailability: unauthorized` with zero calendars, until
/// the app was killed and relaunched — at which point it read `full access` and all eight
/// calendars. A successful grant was indistinguishable from a refusal for as long as the process
/// lived, which cost an evening of debugging the wrong thing.
///
/// Two mechanisms answer it, and they are deliberately different in kind. `GrantSettling` waits a
/// beat for the truth; `GrantNote` covers the case where the truth is slower than anyone will wait.
enum GrantPropagation {
    /// What to report, given what the class method says and whether a grant was accepted moments
    /// ago.
    ///
    /// **Only `.notDetermined` is overridable.** It is the single value that means "tccd has not
    /// answered yet"; `.denied`, `.restricted` and `.writeOnly` are answers — including the
    /// unwelcome ones — and masking a `.denied` would leave the bridge claiming it can still write
    /// after access was pulled in System Settings. Revocation therefore stays immediate, which is
    /// the invariant the whole authorization gate rests on, and this override cannot weaken it.
    static func resolve(
        reported: EventKitAuthorization,
        grantedRecently: Bool
    ) -> EventKitAuthorization {
        guard grantedRecently, reported == .notDetermined else { return reported }
        return .fullAccess
    }
}

/// A note that the user granted access moments ago, valid for a short while.
///
/// It exists only to bridge the gap described on `GrantPropagation`, and is scoped as narrowly as
/// that job allows: it expires, it is dropped the instant the class method agrees, and it can only
/// ever turn `.notDetermined` into `.fullAccess`.
///
/// `@unchecked Sendable` because it is a lock-protected mutable box — the same shape as `FetchGate`.
final class GrantNote: @unchecked Sendable {
    /// How long a `granted == true` may stand in for a status read that has not caught up.
    ///
    /// Long enough to cover a tccd that is slow or busy, short enough that a grant which genuinely
    /// did not take shows through in the same sitting rather than being asserted until the next
    /// launch. Nothing depends on the exact number; what matters is that it is finite.
    static let window: TimeInterval = 30

    private let lock = NSLock()
    private let clock: any BridgeClock
    private var grantedAt: Date?

    init(clock: any BridgeClock = SystemClock()) {
        self.clock = clock
    }

    func record() {
        lock.lock()
        defer { lock.unlock() }
        grantedAt = clock.now
    }

    /// Whether the note still stands. Expiry is checked on read rather than on a timer: there is no
    /// thread here to run one, and a note nobody reads costs nothing by lingering.
    func isActive() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard let grantedAt else { return false }
        return clock.now.timeIntervalSince(grantedAt) < Self.window
    }

    func clear() {
        lock.lock()
        defer { lock.unlock() }
        grantedAt = nil
    }
}

/// Waits, briefly, for `authorizationStatus(for:)` to catch up with a grant the user has already
/// given.
///
/// This is the mechanism that should do the work in practice — the note above is the fallback for
/// when it does not. Re-reading is right where trusting the returned `granted` flag would be wrong:
/// the two can disagree when the user answers the prompt and then changes their mind in System
/// Settings before the continuation resumes, and the class method is the one that knows.
enum GrantSettling {
    /// Bounded and short on purpose: this runs on a user gesture, so a long wait reads as a hang,
    /// and `GrantNote` already covers a tccd slower than this.
    static let budget: TimeInterval = 2
    static let interval: TimeInterval = 0.1

    /// Re-reads until the answer stops being `.notDetermined`, or the budget runs out.
    ///
    /// The reader is injected rather than hardcoded so the reminder and calendar paths — two
    /// separate TCC records — share one implementation, and so the loop itself is exercisable
    /// without a permission prompt, which a test may not raise.
    static func settle(
        budget: TimeInterval = GrantSettling.budget,
        interval: TimeInterval = GrantSettling.interval,
        reading read: @Sendable () -> EventKitAuthorization
    ) async -> EventKitAuthorization {
        var reported = read()
        var waited: TimeInterval = 0

        while reported == .notDetermined, waited < budget {
            try? await Task.sleep(for: .seconds(interval))
            waited += interval
            reported = read()
        }
        return reported
    }
}
