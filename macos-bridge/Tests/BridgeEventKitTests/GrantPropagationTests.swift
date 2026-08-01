import BridgeCore
import Foundation
import Testing

@testable import BridgeEventKit

/// The fix for "a fresh grant is invisible until the app restarts".
///
/// None of this can be exercised against real TCC — a test may not raise a permission prompt, and
/// the machine's grants are not the suite's to change — which is exactly why the decision was
/// extracted into a pure function and a clock-driven note. Everything that could get this wrong is
/// testable here; what is left in `EventKitAuthorization` is two calls and a log line.
@Suite("Grant propagation")
struct GrantPropagationTests {
    // MARK: - The decision

    /// The whole point: `granted == true` arrived, the class method has not caught up, and the
    /// bridge must not report a successful grant as a refusal for the life of the process.
    @Test("a just-granted permission reads as full access while tccd is still catching up")
    func notDeterminedIsCoveredByAFreshGrant() {
        #expect(
            GrantPropagation.resolve(reported: .notDetermined, grantedRecently: true) == .fullAccess
        )
    }

    /// The safety property, and the more important half. `.notDetermined` is the *only* value that
    /// means "no answer yet"; everything else is an answer. Masking a `.denied` would leave the
    /// bridge claiming it can write after access was pulled in System Settings, which would turn a
    /// cosmetic bug into a real one.
    @Test("every other reported status is an answer and passes through untouched", arguments: [
        EventKitAuthorization.fullAccess,
        .denied,
        .restricted,
        .writeOnly,
        .unknown,
    ])
    func realAnswersAreNeverOverridden(reported: EventKitAuthorization) {
        #expect(GrantPropagation.resolve(reported: reported, grantedRecently: true) == reported)
        #expect(GrantPropagation.resolve(reported: reported, grantedRecently: false) == reported)
    }

    @Test("with no recent grant, not-determined stays not-determined")
    func notDeterminedWithoutAGrantIsUntouched() {
        #expect(
            GrantPropagation.resolve(reported: .notDetermined, grantedRecently: false) == .notDetermined
        )
    }

    /// A property rather than a case list: the override can only ever *add* usability to
    /// `.notDetermined`, so no reported status can be downgraded by it.
    @Test("the override never turns a usable status into an unusable one")
    func overrideNeverDowngrades() {
        for reported in EventKitAuthorization.allCases {
            for granted in [true, false] {
                let resolved = GrantPropagation.resolve(reported: reported, grantedRecently: granted)
                #expect(!(reported.isUsable && !resolved.isUsable), "\(reported) was downgraded")
            }
        }
    }

    // MARK: - The note

    @Test("a note only stands once something has been recorded")
    func noteStartsInactive() {
        #expect(GrantNote(clock: ManualClock()).isActive() == false)
    }

    /// Bounded on purpose: a grant that genuinely did not take has to show through in the same
    /// sitting rather than being asserted until the next launch.
    @Test("a note stands for its window and then expires")
    func noteExpires() {
        let clock = ManualClock()
        let note = GrantNote(clock: clock)
        note.record()

        #expect(note.isActive())
        clock.advance(by: GrantNote.window - 1)
        #expect(note.isActive())
        clock.advance(by: 2)
        #expect(note.isActive() == false)
    }

    @Test("clearing a note drops it immediately, whatever the clock says")
    func noteClears() {
        let note = GrantNote(clock: ManualClock())
        note.record()
        note.clear()

        #expect(note.isActive() == false)
    }

    /// `status()` clears the note the moment the class method agrees, so the override window is as
    /// short as it can be rather than as long as it is allowed to be. This is that sequence.
    @Test("a note recorded, then cleared on agreement, does not resurrect")
    func clearedNoteStaysCleared() {
        let clock = ManualClock()
        let note = GrantNote(clock: clock)
        note.record()
        note.clear()

        clock.advance(by: 1)
        #expect(
            GrantPropagation.resolve(reported: .notDetermined, grantedRecently: note.isActive())
                == .notDetermined
        )
    }

    // MARK: - Settling

    /// The mechanism that should do the work in practice: wait a beat for the truth instead of
    /// reading once and losing the race.
    @Test("settling returns as soon as tccd answers")
    func settleStopsOnAnAnswer() async {
        let reads = Counter()
        let settled = await GrantSettling.settle(budget: 1, interval: 0.01) {
            // Not determined on the first read, granted on the second — the exact shape of the bug.
            reads.increment() > 1 ? .fullAccess : .notDetermined
        }

        #expect(settled == .fullAccess)
        #expect(reads.value == 2, "it kept polling after getting an answer")
    }

    @Test("settling gives up rather than hanging on a tccd that never answers")
    func settleIsBounded() async {
        let started = Date()
        let settled = await GrantSettling.settle(budget: 0.3, interval: 0.05) { .notDetermined }

        #expect(settled == .notDetermined)
        // Generous: this asserts the loop terminates, not how fast the scheduler is.
        #expect(Date().timeIntervalSince(started) < 5)
    }

    /// An answer that is already there costs no wait at all — the common case, once the first grant
    /// has been given.
    @Test("an already-settled status is returned without sleeping")
    func settleReturnsImmediatelyWhenAlreadyAnswered() async {
        let reads = Counter()
        let settled = await GrantSettling.settle(budget: 5, interval: 1) {
            _ = reads.increment()
            return .denied
        }

        #expect(settled == .denied)
        #expect(reads.value == 1)
    }
}

/// A counter the `@Sendable` reader closures can share.
private final class Counter: @unchecked Sendable {
    private let lock = NSLock()
    private var count = 0

    /// Returns the value *after* incrementing.
    @discardableResult
    func increment() -> Int {
        lock.lock()
        defer { lock.unlock() }
        count += 1
        return count
    }

    var value: Int {
        lock.lock()
        defer { lock.unlock() }
        return count
    }
}
