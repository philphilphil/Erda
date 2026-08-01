import BridgeCore
import Foundation
import Testing

@testable import BridgeEventKit

/// Real EventKit, against a **dedicated throwaway Reminders list**.
///
/// ## How to run
///
/// ```
/// ERDA_BRIDGE_EVENTKIT_TESTS=1 ERDA_BRIDGE_TEST_LIST="ErdaBridge Scratch" \
///   ERDA_BRIDGE_TEST_CALENDAR="ErdaBridge Scratch" \
///   swift test --filter EventKit
/// ```
///
/// Without `ERDA_BRIDGE_EVENTKIT_TESTS=1` every test here is skipped, so a plain `swift test`
/// never opens the user's Reminders or Calendar database. With it set but `ERDA_BRIDGE_TEST_LIST`
/// (or, for the calendar suite, `ERDA_BRIDGE_TEST_CALENDAR`) missing, the tests **fail loudly**
/// rather than falling back to a default — picking a list or calendar on the user's behalf is the
/// one mistake that would write into real data.
///
/// That matters more than it used to. The bridge can now reach every list and every calendar on
/// the Mac, so the only thing keeping these tests off real data is the name in those variables:
/// every write below names one explicitly, and nothing here ever writes without one.
///
/// ## What these are and are not
///
/// They are a convenience, not the acceptance criteria. `swift test` runs under the test host
/// binary, not under the signed `ErdaBridge.app` bundle, so TCC attributes the access to a
/// different code identity: they can be skipped, denied or flaky for reasons that say nothing
/// about the shipped app. **The authoritative check is the manual checklist in
/// `macos-bridge/README.md`**, performed against the installed bundle.
///
/// They also leave their reminders behind, completed, in the throwaway list — the bridge has no
/// delete API by design, and adding one just to tidy up after tests would be a worse trade.
struct EventKitEnvironment {
    static var isEnabled: Bool {
        ProcessInfo.processInfo.environment["ERDA_BRIDGE_EVENTKIT_TESTS"] == "1"
    }

    /// The list title these tests are allowed to touch. Absent ⇒ the suite fails.
    static var listTitle: String? {
        ProcessInfo.processInfo.environment["ERDA_BRIDGE_TEST_LIST"]
            .flatMap { $0.isEmpty ? nil : $0 }
    }

    /// The calendar title the calendar suite is allowed to touch. Deliberately a **separate**
    /// variable from `ERDA_BRIDGE_TEST_LIST`: reusing the reminder one would mean a run configured
    /// for reminders silently started writing events into whatever calendar happened to share that
    /// name — and events, unlike the reminders here, cannot be deleted by this bridge at all.
    /// Absent ⇒ the calendar suite fails.
    static var calendarTitle: String? {
        ProcessInfo.processInfo.environment["ERDA_BRIDGE_TEST_CALENDAR"]
            .flatMap { $0.isEmpty ? nil : $0 }
    }
}

@Suite(
    "EventKit integration (throwaway list)",
    .enabled(if: EventKitEnvironment.isEnabled, "set ERDA_BRIDGE_EVENTKIT_TESTS=1 to run"),
    .serialized
)
struct EventKitIntegrationTests {
    private let identity = MemoryReminderIdentityStore()
    private let scratch: ListName
    private let service: EventKitStore

    init() throws {
        // Fail loudly, and before touching anything: no default, no "first writable list".
        let title = try #require(
            EventKitEnvironment.listTitle,
            "ERDA_BRIDGE_TEST_LIST must name a dedicated throwaway Reminders list"
        )
        try #require(
            RemindersAccess.status().isUsable,
            "Reminders access is not granted to the test host — grant it in System Settings or run the manual checklist instead"
        )

        let candidates = RemindersAccess.lists().filter { $0.title == title }
        try #require(!candidates.isEmpty, "no Reminders list titled \(title)")
        // Two lists with the same title would make "the throwaway one" ambiguous — and the bridge
        // itself refuses an ambiguous name for the same reason, so it could not resolve it either.
        try #require(candidates.count == 1, "\(candidates.count) Reminders lists are titled \(title)")
        let list = try #require(candidates.first)
        try #require(list.isWritable, "\(title) cannot hold reminders")

        self.scratch = try #require(ListName(rawValue: title), "\(title) is not a usable list name")
        self.service = EventKitStore(
            identity: identity,
            timeZone: TimeZone(identifier: "Europe/Berlin")!
        )
    }

    private func command(_ title: String, dueAt: Date? = nil, priority: Int = 0) -> CreateReminderCommand {
        CreateReminderCommand(
            id: .generate(),
            list: scratch,
            // Tagged so anything left behind is obviously test debris.
            title: "[erdabridge-test] \(title) \(UUID().uuidString.prefix(8))",
            notes: "written by EventKitIntegrationTests",
            dueAt: dueAt,
            priority: priority
        )
    }

    @Test("the bridge reports itself available against a real granted list")
    func availabilityIsOk() async {
        #expect(await service.availability() == .ok)
    }

    @Test("the throwaway list is among the names status reports")
    func availableListsIncludesTheScratchList() async {
        #expect(await service.availableLists().contains(scratch))
    }

    @Test("a created reminder comes back from list, with its mapping persisted")
    func createThenList() async throws {
        let due = Date().addingTimeInterval(3600)
        let created = try await service.create(command("create-then-list", dueAt: due, priority: 5))

        #expect(created.list == scratch)
        #expect(created.isCompleted == false)
        #expect(try identity.itemId(for: created.id) != nil)

        let listed = try await service.list(try ListRemindersQuery(lists: [scratch], limit: 200))
        let match = try #require(listed.first { $0.id == created.id })
        #expect(match.title == created.title)
        #expect(match.priority == 5)
        // Whole seconds survive the DateComponents round trip; sub-second precision does not.
        let dueAt = try #require(match.dueAt)
        #expect(abs(dueAt.timeIntervalSince(due)) < 1)
    }

    /// The `allDay` trap: a reminder created with a due time must come back with that time, not
    /// as an all-day item at midnight.
    @Test("a timed due date does not become an all-day reminder")
    func timedDueDateStaysTimed() async throws {
        var berlin = Calendar(identifier: .gregorian)
        berlin.timeZone = TimeZone(identifier: "Europe/Berlin")!
        let due = try #require(
            berlin.date(from: DateComponents(year: 2027, month: 3, day: 14, hour: 16, minute: 45, second: 0))
        )

        let created = try await service.create(command("timed-due", dueAt: due))
        let listed = try await service.list(try ListRemindersQuery(lists: [scratch], limit: 200))
        let match = try #require(listed.first { $0.id == created.id })

        let readBack = try #require(match.dueAt)
        let components = berlin.dateComponents([.hour, .minute], from: readBack)
        #expect(components.hour == 16)
        #expect(components.minute == 45)
    }

    @Test("a reminder with no due date is listed rather than silently dropped")
    func undatedRemindersAreListed() async throws {
        let created = try await service.create(command("undated"))
        let listed = try await service.list(try ListRemindersQuery(lists: [scratch], limit: 200))

        let match = try #require(
            listed.first { $0.id == created.id },
            "an undated reminder disappeared from list — the predicate is dropping it"
        )
        #expect(match.dueAt == nil)
    }

    @Test("completing removes it from list, and completing again is a success no-op")
    func completeIsIdempotent() async throws {
        let created = try await service.create(command("complete-twice"))

        let first = try await service.complete(id: created.id)
        #expect(first.id == created.id)
        #expect(first.alreadyCompleted == false)

        let second = try await service.complete(id: created.id)
        #expect(second.alreadyCompleted == true)

        let listed = try await service.list(try ListRemindersQuery(lists: [scratch], limit: 200))
        #expect(!listed.contains { $0.id == created.id })
    }

    @Test("an id the bridge never issued is a 404")
    func unknownIdIs404() async {
        await #expect(throws: ApiError.notFound) { try await service.complete(id: .generate()) }
    }

    /// A mapping row that points at an EventKit id which no longer resolves — what an iCloud full
    /// sync leaves behind. It must be a flat 404, never an attempt to find the reminder some
    /// other way.
    @Test("a dangling mapping is a 404, not a re-resolution attempt")
    func danglingMappingIs404() async throws {
        let ghost = BridgeID.generate()
        try identity.recordMapping(
            bridgeId: ghost,
            itemId: "x-apple-reminderkit://REMCDReminder/00000000-0000-0000-0000-000000000000",
            externalId: nil,
            list: scratch,
            at: Date()
        )
        await #expect(throws: ApiError.notFound) { try await service.complete(id: ghost) }
    }

    /// A name nobody's list wears fails, on a Mac full of real lists — no default, no nearest
    /// match, no "the first writable one".
    @Test("a name that matches no list fails closed against the real database")
    func unknownNameFailsClosed() async throws {
        let missing = try #require(ListName(rawValue: "erdabridge-no-such-list-\(UUID().uuidString)"))
        await #expect(throws: ApiError.noSuchList) {
            try await service.create(
                CreateReminderCommand(
                    id: .generate(),
                    list: missing,
                    title: "[erdabridge-test] must not be created",
                    notes: nil,
                    dueAt: nil,
                    priority: 0
                )
            )
        }
        await #expect(throws: ApiError.noSuchList) {
            try await service.list(try ListRemindersQuery(lists: [missing], limit: 200))
        }
        #expect(identity.mappingCount == 0)
    }

    /// A filtered list stays inside the list it named. (The unfiltered case deliberately spans
    /// every list now — that is the point of the change — so it is not asserted here.)
    @Test("a filtered list returns nothing from any other list")
    func filteredListStaysInsideItsList() async throws {
        _ = try await service.create(command("containment"))
        let listed = try await service.list(try ListRemindersQuery(lists: [scratch], limit: 200))
        #expect(listed.allSatisfy { $0.list == scratch })
    }

    /// A reminder the bridge did not create has no mapping. Minting one on first sight is what
    /// makes it completable at all — without it, `list` would only ever show the bridge's own
    /// reminders.
    @Test("a reminder seen for the first time gets a mapping so it can be completed")
    func mintsMappingsOnFirstSight() async throws {
        let created = try await service.create(command("mint-on-sight"))
        let forgetful = MemoryReminderIdentityStore()
        let fresh = EventKitStore(identity: forgetful)

        let listed = try await fresh.list(try ListRemindersQuery(lists: [scratch], limit: 200))
        let match = try #require(listed.first { $0.title == created.title })
        // A different id than the original — the mapping was minted, not recovered.
        #expect(try forgetful.itemId(for: match.id) != nil)

        let outcome = try await fresh.complete(id: match.id)
        #expect(outcome.alreadyCompleted == false)
    }
}

/// Real EventKit **calendars**, against a dedicated throwaway calendar named by
/// `ERDA_BRIDGE_TEST_CALENDAR`.
///
/// The same caveats as the reminder suite apply, and one more that is sharper: **this bridge has
/// no delete for events**, by design. Everything created here stays in that calendar until a human
/// removes it in Calendar.app, which is exactly why the variable is mandatory, separate from the
/// reminder one, and must name a calendar nobody looks at.
@Suite(
    "EventKit calendar integration (throwaway calendar)",
    .enabled(if: EventKitEnvironment.isEnabled, "set ERDA_BRIDGE_EVENTKIT_TESTS=1 to run"),
    .serialized
)
struct EventKitCalendarIntegrationTests {
    private let scratch: CalendarName
    private let berlin = TimeZone(identifier: "Europe/Berlin")!
    /// Pinned to the throwaway calendar's own identifier. Everything this suite creates lands there
    /// because that is the only place a create *can* land — there is no per-request calendar left
    /// to pass.
    private let service: EventKitStore

    init() throws {
        // Fail loudly, and before touching anything: no default, no "first writable calendar".
        let title = try #require(
            EventKitEnvironment.calendarTitle,
            "ERDA_BRIDGE_TEST_CALENDAR must name a dedicated throwaway calendar — nothing here can be deleted afterwards"
        )
        try #require(
            CalendarAccess.status().isUsable,
            "Calendar access is not granted to the test host — grant it in System Settings or run the manual checklist instead"
        )

        let candidates = CalendarAccess.calendars().filter { $0.title == title }
        try #require(!candidates.isEmpty, "no calendar titled \(title)")
        // Two calendars with the same title would make "the throwaway one" ambiguous — and the
        // bridge refuses an ambiguous name for the same reason, so it could not resolve it either.
        try #require(candidates.count == 1, "\(candidates.count) calendars are titled \(title)")
        let calendar = try #require(candidates.first)
        try #require(calendar.isWritable, "\(title) cannot hold events")

        let scratch = try #require(CalendarName(rawValue: title), "\(title) is not a usable calendar name")
        self.scratch = scratch
        self.service = EventKitStore(
            identity: MemoryReminderIdentityStore(),
            writeCalendar: MemoryWriteCalendarStore(
                binding: CalendarBinding(calendarId: calendar.calendarId, titleAtBind: scratch)
            ),
            timeZone: berlin
        )
    }

    /// Well into the future, so nothing here collides with a real appointment in a window someone
    /// might actually look at, and tagged so anything left behind is obviously test debris.
    private func command(
        _ title: String,
        startingAt start: Date? = nil,
        lasting seconds: TimeInterval = 3600
    ) -> CreateCalendarEventCommand {
        let startAt = start ?? Date().addingTimeInterval(24 * 3600)
        return CreateCalendarEventCommand(
            title: "[erdabridge-test] \(title) \(UUID().uuidString.prefix(8))",
            notes: "written by EventKitCalendarIntegrationTests",
            startAt: startAt,
            endAt: startAt.addingTimeInterval(seconds),
            timeZone: berlin
        )
    }

    @Test("the bridge reports itself available against a real granted calendar")
    func availabilityIsOk() async {
        #expect(await service.calendarAvailability() == .ok)
    }

    @Test("the throwaway calendar is among the names status reports")
    func availableCalendarsIncludesTheScratchCalendar() async {
        #expect(await service.availableCalendars().contains(scratch))
    }

    @Test("a created event comes back from a listing, with its times intact")
    func createThenList() async throws {
        let start = Date().addingTimeInterval(3600)
        let created = try await service.create(command("create-then-list", startingAt: start))

        #expect(created.calendar == scratch)
        #expect(created.isAllDay == false)
        #expect(created.timeZone == "Europe/Berlin")

        let listed = try await service.upcoming(
            try ListCalendarEventsQuery(calendars: [scratch], days: 2, limit: 200)
        )
        let match = try #require(listed.first { $0.title == created.title })
        // Whole seconds survive the EventKit round trip; sub-second precision does not.
        #expect(abs(match.startAt.timeIntervalSince(start)) < 1)
        #expect(abs(match.endAt.timeIntervalSince(start.addingTimeInterval(3600))) < 1)
        #expect(match.isAllDay == false)
    }

    /// The event-side counterpart of the reminder suite's all-day trap: an event created with
    /// explicit times must not come back as an all-day band.
    @Test("a timed event does not become an all-day one")
    func timedEventStaysTimed() async throws {
        var gregorian = Calendar(identifier: .gregorian)
        gregorian.timeZone = berlin
        let start = try #require(
            gregorian.date(from: DateComponents(year: 2027, month: 3, day: 14, hour: 16, minute: 45))
        )

        let created = try await service.create(command("timed", startingAt: start))
        // A year out, so the default window would not reach it — the window really is a filter.
        let inDefaultWindow = try await service.upcoming(
            try ListCalendarEventsQuery(calendars: [scratch], limit: 200)
        )
        #expect(!inDefaultWindow.contains { $0.title == created.title })

        #expect(created.isAllDay == false)
        let components = gregorian.dateComponents([.hour, .minute], from: created.startAt)
        #expect(components.hour == 16)
        #expect(components.minute == 45)
    }

    /// The window is the route's only bound on how much of somebody's calendar comes back, so it
    /// has to actually bound it against real data.
    @Test("the window really filters, and a wider one finds what a narrower one missed")
    func windowFilters() async throws {
        let start = Date().addingTimeInterval(20 * 24 * 3600)
        let created = try await service.create(command("far-out", startingAt: start))

        let narrow = try await service.upcoming(
            try ListCalendarEventsQuery(calendars: [scratch], days: 1, limit: 200)
        )
        #expect(!narrow.contains { $0.title == created.title })

        let wide = try await service.upcoming(
            try ListCalendarEventsQuery(calendars: [scratch], days: 31, limit: 200)
        )
        #expect(wide.contains { $0.title == created.title })
    }

    @Test("a listing is capped by its limit and sorted soonest-first")
    func limitAndOrder() async throws {
        let base = Date().addingTimeInterval(2 * 3600)
        for offset in [3.0, 1.0, 2.0] {
            _ = try await service.create(command("ordering", startingAt: base.addingTimeInterval(offset * 3600)))
        }

        let listed = try await service.upcoming(
            try ListCalendarEventsQuery(calendars: [scratch], days: 2, limit: 200)
        )
        #expect(listed == listed.sorted { $0.startAt < $1.startAt })

        let capped = try await service.upcoming(
            try ListCalendarEventsQuery(calendars: [scratch], days: 2, limit: 1)
        )
        #expect(capped.count <= 1)
    }

    /// A name nobody's calendar wears fails a **read filter**, on a Mac full of real calendars — no
    /// default, no nearest match, no "the first writable one".
    @Test("a name that matches no calendar fails closed against the real database")
    func unknownNameFailsClosed() async throws {
        let missing = try #require(CalendarName(rawValue: "erdabridge-no-such-calendar-\(UUID().uuidString)"))

        await #expect(throws: ApiError.noSuchCalendar) {
            try await service.upcoming(try ListCalendarEventsQuery(calendars: [missing]))
        }
    }

    /// The write side, against real calendars: with nothing pinned there is a whole Mac full of
    /// writable calendars to fall into, and the bridge must fall into none of them.
    @Test("with nothing pinned, a create refuses on a Mac full of real calendars")
    func unpinnedCreateRefusesAgainstRealCalendars() async throws {
        let unpinned = EventKitStore(identity: MemoryReminderIdentityStore(), timeZone: berlin)

        await #expect(throws: ApiError.calendarNotConfigured) {
            try await unpinned.create(self.command("must not be created"))
        }
        #expect(await unpinned.writeCalendar() == .notConfigured)
    }

    /// A binding that points at nothing, paired with the throwaway calendar's real **title**. If
    /// resolution ever consulted the title, this would find the scratch calendar and write into it
    /// — so the assertion is both "it refused" and "it did not quietly re-bind".
    @Test("a dangling binding refuses rather than re-binding by title")
    func danglingBindingRefusesAgainstRealCalendars() async throws {
        let dangling = EventKitStore(
            identity: MemoryReminderIdentityStore(),
            writeCalendar: MemoryWriteCalendarStore(
                binding: CalendarBinding(
                    calendarId: "00000000-0000-0000-0000-000000000000",
                    titleAtBind: scratch
                )
            ),
            timeZone: berlin
        )

        await #expect(throws: ApiError.calendarNotConfigured) {
            try await dangling.create(self.command("must not be created"))
        }
        #expect(await dangling.writeCalendar() == .unresolvable(scratch))
    }

    /// The status readout, against the real calendar this suite writes to.
    @Test("the write-calendar readout names the pinned calendar")
    func writeCalendarReportsTheScratchCalendar() async {
        #expect(await service.writeCalendar() == .configured(scratch))
    }

    /// A filtered listing stays inside the calendar it named. (The unfiltered case spans every
    /// calendar by design, so it is not asserted here — that would mean reading real appointments
    /// into a test's assertions.)
    @Test("a filtered listing returns nothing from any other calendar")
    func filteredListingStaysInsideItsCalendar() async throws {
        _ = try await service.create(command("containment"))
        let listed = try await service.upcoming(
            try ListCalendarEventsQuery(calendars: [scratch], days: 2, limit: 200)
        )
        #expect(listed.allSatisfy { $0.calendar == scratch })
    }

    /// Both grants run through one `EKEventStore`. If the shared store or the shared change flag
    /// were mishandled, interleaving the two would be where it showed up — a reminder fetch
    /// invalidating the calendar's objects, or vice versa.
    @Test(
        "reminder and calendar operations interleave on one store without disturbing each other",
        .enabled(if: EventKitEnvironment.listTitle != nil, "needs ERDA_BRIDGE_TEST_LIST too")
    )
    func interleavesWithReminders() async throws {
        let listTitle = try #require(EventKitEnvironment.listTitle)
        let list = try #require(ListName(rawValue: listTitle))
        try #require(RemindersAccess.status().isUsable, "needs Reminders access as well")

        let created = try await service.create(command("interleaved"))
        _ = try await service.create(
            CreateReminderCommand(
                id: .generate(),
                list: list,
                title: "[erdabridge-test] interleaved \(UUID().uuidString.prefix(8))",
                notes: nil,
                dueAt: nil,
                priority: 0
            )
        )

        let events = try await service.upcoming(
            try ListCalendarEventsQuery(calendars: [scratch], days: 2, limit: 200)
        )
        #expect(events.contains { $0.title == created.title })
        // And the reminder side still answers after all of that.
        #expect(await service.availableLists().contains(list))
    }
}
