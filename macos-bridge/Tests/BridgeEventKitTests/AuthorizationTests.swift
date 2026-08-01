import BridgeCore
import Foundation
import Testing

@testable import BridgeEventKit

@Suite("Authorization")
struct AuthorizationTests {
    /// Write-only access can create an item but cannot read one back, so for reminders it
    /// satisfies neither `list` nor `complete`. For calendars it is worse: resolving a calendar by
    /// name means *enumerating* calendars, which write-only forbids, so it could not even find
    /// where to write. Accepting it would produce a bridge that half works.
    @Test("only full access is usable")
    func onlyFullAccessIsUsable() {
        for status in EventKitAuthorization.allCases {
            #expect(status.isUsable == (status == .fullAccess), "\(status) usability")
        }
    }

    @Test("every status has readable text for the setup window")
    func hasDisplayText() {
        for status in EventKitAuthorization.allCases {
            #expect(!status.displayText.isEmpty)
        }
    }

    /// A macOS release adding a new `EKAuthorizationStatus` must fail closed, not crash and not
    /// be optimistically treated as access.
    @Test("an unknown future status is not usable")
    func unknownFailsClosed() {
        #expect(EventKitAuthorization.unknown.isUsable == false)
    }

    /// Reading either status raises no TCC prompt and needs no grant, so this is safe everywhere —
    /// including in a checkout that has never been given Reminders or Calendar access.
    @Test("reading either status is side-effect free and always answers")
    func statusAlwaysAnswers() {
        #expect(EventKitAuthorization.allCases.contains(RemindersAccess.status()))
        #expect(EventKitAuthorization.allCases.contains(CalendarAccess.status()))
    }

    /// Enumerating lists is a local-only capability with no HTTP route; when access is not usable
    /// it must answer "nothing" rather than reaching for the store.
    @Test("lists and the count agree, and are empty without usable access")
    func listsMatchCount() {
        let lists = RemindersAccess.lists()
        #expect(lists.count == RemindersAccess.reminderListCount())
        if !RemindersAccess.status().isUsable {
            #expect(lists.isEmpty)
        }
    }

    @Test("calendars and the count agree, and are empty without usable access")
    func calendarsMatchCount() {
        let calendars = CalendarAccess.calendars()
        #expect(calendars.count == CalendarAccess.calendarCount())
        if !CalendarAccess.status().isUsable {
            #expect(calendars.isEmpty)
        }
    }

    /// The two grants are separate TCC records, and each readout is gated on **its own**. This is
    /// the assertion that would fail if `CalendarAccess` were ever wired to the reminder status
    /// (or vice versa) as a convenience: on a Mac with one granted and the other not, the denied
    /// side would start returning rows.
    @Test("each inventory is gated on its own grant, never on the other one")
    func grantsAreGatedIndependently() {
        if !RemindersAccess.status().isUsable {
            #expect(RemindersAccess.lists().isEmpty, "reminder lists leaked without a reminder grant")
        }
        if !CalendarAccess.status().isUsable {
            #expect(CalendarAccess.calendars().isEmpty, "calendars leaked without a calendar grant")
        }
        // And a denial on one side never suppresses the other side's ability to answer at all.
        #expect(EventKitAuthorization.allCases.contains(RemindersAccess.status()))
        #expect(EventKitAuthorization.allCases.contains(CalendarAccess.status()))
    }

    /// Reading the status repeatedly must give the same answer — it is read on every request, on a
    /// 5-second UI poll, and from `GET /v1/status`, and those must not disagree with each other.
    ///
    /// It is also the guard on `GrantNote`: the note is consulted inside `status()`, so a bug that
    /// let it flap (recording on a read, say, or expiring mid-call) would show up as two reads
    /// disagreeing on a machine where nothing changed.
    @Test("the status is stable across reads when nothing has changed")
    func statusIsStableAcrossReads() {
        let reminders = RemindersAccess.status()
        let calendar = CalendarAccess.status()

        for _ in 0..<10 {
            #expect(RemindersAccess.status() == reminders)
            #expect(CalendarAccess.status() == calendar)
        }
    }

    /// One source of truth, checked from both ends. The setup window reads `RemindersAccess.status()`
    /// / `CalendarAccess.status()`; `GET /v1/status` reads the actor's `availability()` /
    /// `calendarAvailability()`, which must be derived from exactly those. A window that says
    /// "granted" while the API says `unauthorized` is the bug this pins shut.
    @Test("the actor's availability is the same answer the setup window shows")
    func availabilityMatchesTheAccessSurface() async {
        let service = EventKitStore(identity: MemoryReminderIdentityStore(), observingChanges: false)

        let remindersUsable = await service.availability() == .ok
        let calendarUsable = await service.calendarAvailability() == .ok
        #expect(remindersUsable == RemindersAccess.status().isUsable)
        #expect(calendarUsable == CalendarAccess.status().isUsable)
    }
}
