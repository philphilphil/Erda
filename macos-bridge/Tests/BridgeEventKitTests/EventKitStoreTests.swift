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
    private func service(identity: MemoryReminderIdentityStore = .init()) -> EventKitStore {
        EventKitStore(
            identity: identity,
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

    /// A name no list on this Mac wears must fail, whatever else is here. The string is not one
    /// anybody would call a list, so this holds on a granted Mac too.
    @Test("a name that matches no list is refused, never defaulted")
    func unknownNameIsRefused() async throws {
        let service = service()
        let missing = try listName("erdabridge-no-such-list-\(UUID().uuidString)")

        await #expect(throws: ApiError.self) {
            try await service.list(try ListRemindersQuery(lists: [missing]))
        }
        await #expect(throws: ApiError.self) {
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
    }

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

    /// A name no calendar on this Mac wears must fail, whatever else is here. The string is not
    /// one anybody would call a calendar, so this holds on a granted Mac too.
    @Test("a name that matches no calendar is refused, never defaulted")
    func unknownCalendarIsRefused() async throws {
        let service = service()
        let missing = try calendarName("erdabridge-no-such-calendar-\(UUID().uuidString)")
        let start = Date(timeIntervalSince1970: 1_800_000_000)

        await #expect(throws: ApiError.self) {
            try await service.upcoming(try ListCalendarEventsQuery(calendars: [missing]))
        }
        await #expect(throws: ApiError.self) {
            try await service.create(
                CreateCalendarEventCommand(
                    calendar: missing,
                    title: "[erdabridge-test] must not be created",
                    notes: nil,
                    startAt: start,
                    endAt: start.addingTimeInterval(3600),
                    timeZone: TimeZone(identifier: "Europe/Berlin")!
                )
            )
        }
    }
}
