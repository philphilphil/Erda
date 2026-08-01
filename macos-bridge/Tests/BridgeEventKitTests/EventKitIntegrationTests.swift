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
///   swift test --filter EventKitIntegrationTests
/// ```
///
/// Without `ERDA_BRIDGE_EVENTKIT_TESTS=1` every test here is skipped, so a plain `swift test`
/// never opens the user's Reminders database. With it set but `ERDA_BRIDGE_TEST_LIST` missing,
/// the tests **fail loudly** rather than falling back to a default list — picking a list on the
/// user's behalf is the one mistake that would write into real data.
///
/// That matters more than it used to. The bridge can now reach every list on the Mac, so the only
/// thing keeping these tests off real data is the name in that variable: every write below names
/// it explicitly, and nothing here ever calls a list-wide write.
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
}

@Suite(
    "EventKit integration (throwaway list)",
    .enabled(if: EventKitEnvironment.isEnabled, "set ERDA_BRIDGE_EVENTKIT_TESTS=1 to run"),
    .serialized
)
struct EventKitIntegrationTests {
    private let identity = MemoryReminderIdentityStore()
    private let scratch: ListName
    private let service: EventKitReminders

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
        self.service = EventKitReminders(
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
        let fresh = EventKitReminders(identity: forgetful)

        let listed = try await fresh.list(try ListRemindersQuery(lists: [scratch], limit: 200))
        let match = try #require(listed.first { $0.title == created.title })
        // A different id than the original — the mapping was minted, not recovered.
        #expect(try forgetful.itemId(for: match.id) != nil)

        let outcome = try await fresh.complete(id: match.id)
        #expect(outcome.alreadyCompleted == false)
    }
}
