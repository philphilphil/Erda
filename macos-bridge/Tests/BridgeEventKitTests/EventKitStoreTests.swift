import BridgeCore
import Foundation
import Testing

@testable import BridgeEventKit

/// The parts of the actor that hold whatever the machine's TCC state is.
///
/// Every assertion here is true both on a Mac that has granted Reminders access and on one that
/// has not — deliberately, because these run in a plain `swift test` under the test host, whose
/// authorization state is not something the suite may change. Anything that needs a real grant
/// lives in `EventKitIntegrationTests` behind `ERDA_BRIDGE_EVENTKIT_TESTS`.
@Suite("EventKitStore — no grant required")
struct EventKitStoreTests {
    private func service(
        identity: MemoryReminderIdentityStore = .init(),
        writeList: MemoryWriteListStore = .init(),
        writeCalendar: MemoryWriteCalendarStore = .init()
    ) -> EventKitStore {
        EventKitStore(
            identity: identity,
            writeList: writeList,
            writeCalendar: writeCalendar,
            clock: ManualClock(),
            timeZone: TimeZone(identifier: "Europe/Berlin")!,
            // No subscription: a unit test must not react to the user's real Reminders database
            // changing underneath it.
            observingChanges: false
        )
    }

    /// Availability now says one thing and one thing only: is the grant usable. There is no
    /// allowlist left to be empty, so nothing else can make the back end unavailable.
    @Test("availability reports the authorization status and nothing else")
    func availabilityFollowsAuthorization() async {
        let expected: ReminderAvailability = RemindersAccess.status().isUsable ? .ok : .unauthorized
        #expect(await service().availability() == expected)
    }

    /// The status readout must answer even with access revoked, because "what can you reach?" has
    /// to be answerable in order to explain that the answer is "nothing".
    @Test("the list readout never throws, whatever the grant is")
    func availableListsNeverThrows() async {
        let lists = await service().availableLists()
        if !RemindersAccess.status().isUsable {
            #expect(lists.isEmpty)
        }
        // Sorted and deduplicated, so a caller can present it directly.
        #expect(lists == lists.sorted())
        #expect(Set(lists).count == lists.count)
    }

    @Test("an unmapped id never succeeds")
    func unmappedIdNeverSucceeds() async throws {
        // 404 when authorized, 503 when not — never a success, and never a 500.
        await #expect(throws: ApiError.self) { try await self.service().complete(id: .generate()) }
    }

    /// A name no list on this Mac wears must fail a *read filter*, whatever else is here. The string
    /// is not one anybody would call a list, so this holds on a granted Mac too. Creates no longer
    /// name a list at all — that is the pinning tests' job below.
    @Test("a filter naming no list is refused, never widened to all of them")
    func unknownNameIsRefused() async throws {
        let service = service()
        let missing = try listName("erdabridge-no-such-list-\(UUID().uuidString)")

        await #expect(throws: ApiError.self) {
            try await service.list(try ListRemindersQuery(lists: [missing]))
        }
    }

    /// The write side, and the important one: with no binding stored, a create must refuse rather
    /// than reach for a default list. This runs on a Mac with **real, granted lists** — that is the
    /// whole point, since a fallback would happily find one.
    @Test("with nothing pinned, a create refuses instead of finding a default list")
    func unpinnedCreateRefuses() async throws {
        let service = service()
        await #expect(throws: Self.expectedReminderCreateFailure) {
            try await service.create(Self.reminderCommand)
        }
    }

    /// A binding whose identifier resolves to nothing — what a deleted list or a signed-out account
    /// leaves behind. Same refusal, and specifically **not** a re-bind onto a list wearing the
    /// stored title.
    @Test("a stored list binding that no longer resolves refuses rather than re-binding by title")
    func danglingListBindingRefuses() async throws {
        // A title every Mac with lists has, paired with an identifier no Mac has: if the resolver
        // ever consulted the title, this would find something.
        let store = MemoryWriteListStore(
            binding: ListBinding(
                listId: "00000000-0000-0000-0000-000000000000",
                titleAtBind: try listName(RemindersAccess.lists().first?.title ?? "Reminders")
            )
        )
        await #expect(throws: Self.expectedReminderCreateFailure) {
            try await self.service(writeList: store).create(Self.reminderCommand)
        }
    }

    /// A store that cannot be read is treated as "nothing pinned", never as licence to pick.
    @Test("an unreadable list binding store fails closed")
    func unreadableListBindingStoreRefuses() async throws {
        let store = MemoryWriteListStore()
        store.setReadFailure(ApiError.internal)
        let service = service(writeList: store)

        await #expect(throws: Self.expectedReminderCreateFailure) {
            try await service.create(Self.reminderCommand)
        }
        #expect(await service.writeList() == .notConfigured)
    }

    /// On a test host without the reminder grant the refusal comes one step earlier, at the
    /// authorization gate. Either way it is a refusal, and either way nothing is written.
    private static var expectedReminderCreateFailure: ApiError {
        RemindersAccess.status().isUsable ? .listNotConfigured : .remindersUnavailable
    }

    /// The readout has to answer even when there is nothing to report — `GET /v1/status` cannot
    /// throw.
    @Test("the write-list readout never throws, whatever the grant is")
    func writeListReadoutNeverThrows() async {
        #expect(await service().writeList() == .notConfigured)
    }

    /// Tagged, so that if a fallback ever *did* fire, the debris it left behind would be obvious
    /// rather than mixed in with real reminders.
    private static let reminderCommand = CreateReminderCommand(
        id: .generate(),
        title: "[erdabridge-test] must not be created",
        notes: nil,
        dueAt: nil,
        priority: 0
    )

    /// The seam's whole point: `EventKitStore` is substitutable for `FakeReminders` in
    /// `BridgeServices` without the request layer knowing which it got.
    @Test("it satisfies the RemindersService seam")
    func conformsToTheSeam() throws {
        let service: any RemindersService = service()
        #expect(service is EventKitStore)
    }

    // MARK: - Calendars

    /// One actor, both seams — which is the whole reason it is one actor. `BridgeServices` holds
    /// two references to the same instance, and neither side can tell.
    @Test("the same instance satisfies both seams")
    func conformsToBothSeams() throws {
        let store = service()
        let reminders: any RemindersService = store
        let calendar: any CalendarService = store
        #expect(reminders is EventKitStore)
        #expect(calendar is EventKitStore)
        #expect(ObjectIdentifier(reminders as AnyObject) == ObjectIdentifier(calendar as AnyObject))
    }

    /// Calendar availability follows the **event** grant, which is a separate TCC record. This
    /// would fail if it were ever wired to the reminder status as a convenience.
    @Test("calendar availability reports the event authorization, not the reminder one")
    func calendarAvailabilityFollowsItsOwnGrant() async {
        let expected: CalendarAvailability = CalendarAccess.status().isUsable ? .ok : .unauthorized
        #expect(await service().calendarAvailability() == expected)
    }

    @Test("the calendar readout never throws, whatever the grant is")
    func availableCalendarsNeverThrows() async {
        let calendars = await service().availableCalendars()
        if !CalendarAccess.status().isUsable {
            #expect(calendars.isEmpty)
        }
        // Sorted and deduplicated, so a caller can present it directly.
        #expect(calendars == calendars.sorted())
        #expect(Set(calendars).count == calendars.count)
    }

    /// A name no calendar on this Mac wears must fail a *read filter*, whatever else is here. The
    /// string is not one anybody would call a calendar, so this holds on a granted Mac too.
    @Test("a filter naming no calendar is refused, never widened to all of them")
    func unknownCalendarIsRefused() async throws {
        await #expect(throws: ApiError.self) {
            try await self.service().upcoming(
                try ListCalendarEventsQuery(
                    calendars: [try calendarName("erdabridge-no-such-calendar-\(UUID().uuidString)")]
                )
            )
        }
    }

    /// The write side, and the important one: with no binding stored, a create must refuse rather
    /// than reach for `defaultCalendarForNewEvents`. This runs on a Mac with **real, granted
    /// calendars** — that is the whole point, since a fallback would happily find one.
    @Test("with nothing pinned, a create refuses instead of finding a default calendar")
    func unpinnedCreateRefuses() async throws {
        let service = service()
        await #expect(throws: Self.expectedCreateFailure) {
            try await service.create(Self.command)
        }
    }

    /// A binding whose identifier resolves to nothing — what a deleted calendar or a signed-out
    /// account leaves behind. Same refusal, and specifically **not** a re-bind onto a calendar
    /// wearing the stored title.
    @Test("a stored binding that no longer resolves refuses rather than re-binding by title")
    func danglingBindingRefuses() async throws {
        // A title every Mac with calendars has, paired with an identifier no Mac has: if the
        // resolver ever consulted the title, this would find something.
        let store = MemoryWriteCalendarStore(
            binding: CalendarBinding(
                calendarId: "00000000-0000-0000-0000-000000000000",
                titleAtBind: try calendarName(CalendarAccess.calendars().first?.title ?? "Calendar")
            )
        )
        await #expect(throws: Self.expectedCreateFailure) {
            try await self.service(writeCalendar: store).create(Self.command)
        }
    }

    /// A store that cannot be read is treated as "nothing pinned", never as licence to pick.
    @Test("an unreadable binding store fails closed")
    func unreadableBindingStoreRefuses() async throws {
        let store = MemoryWriteCalendarStore()
        store.setReadFailure(ApiError.internal)
        let service = service(writeCalendar: store)

        await #expect(throws: Self.expectedCreateFailure) {
            try await service.create(Self.command)
        }
        #expect(await service.writeCalendar() == .notConfigured)
    }

    /// On a test host without the calendar grant the refusal comes one step earlier, at the
    /// authorization gate. Either way it is a refusal, and either way nothing is written — which is
    /// the property these tests are actually about.
    private static var expectedCreateFailure: ApiError {
        CalendarAccess.status().isUsable ? .calendarNotConfigured : .calendarUnavailable
    }

    /// The readout has to answer even when there is nothing to report — `GET /v1/status` cannot
    /// throw.
    @Test("the write-calendar readout never throws, whatever the grant is")
    func writeCalendarReadoutNeverThrows() async {
        #expect(await service().writeCalendar() == .notConfigured)
    }

    /// Well into the future and tagged, so that if a fallback ever *did* fire, the debris it left
    /// behind would be obvious rather than mixed in with real appointments.
    private static let command = CreateCalendarEventCommand(
        title: "[erdabridge-test] must not be created",
        notes: nil,
        startAt: Date(timeIntervalSince1970: 1_800_000_000),
        endAt: Date(timeIntervalSince1970: 1_800_003_600),
        timeZone: TimeZone(identifier: "Europe/Berlin")!
    )
}
