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
@Suite("EventKitReminders — no grant required")
struct EventKitRemindersTests {
    private func service(_ allowlist: Allowlist, identity: MemoryReminderIdentityStore = .init()) -> EventKitReminders {
        EventKitReminders(
            allowlist: { allowlist },
            identity: identity,
            clock: ManualClock(),
            timeZone: TimeZone(identifier: "Europe/Berlin")!,
            // No subscription: a unit test must not react to the user's real Reminders database
            // changing underneath it.
            observingChanges: false
        )
    }

    /// An allowlist nobody has filled in is the state the bridge ships in. It must answer "not
    /// usable" rather than "everything", whatever the authorization status happens to be.
    @Test("an empty allowlist is never available")
    func emptyAllowlistIsNeverOk() async {
        let availability = await service(Allowlist(entries: [])).availability()
        #expect(availability != .ok)
    }

    @Test("an allowlist with nothing healthy is never available")
    func allBrokenIsNeverOk() async throws {
        let allowlist = Allowlist(entries: [try allowlistEntry("gone", state: .broken)])
        #expect(await service(allowlist).availability() != .ok)
    }

    /// `predicateForReminders(in:)` with an empty array is documented as *all* calendars. If the
    /// resolved calendar set were ever passed through empty, one `GET` would return every
    /// reminder on the Mac — so the empty case has to be refused before the predicate is built.
    @Test("listing with nothing to list is refused, never widened to every calendar")
    func neverFetchesAcrossEveryCalendar() async throws {
        let service = service(Allowlist(entries: []))
        await #expect(throws: ApiError.remindersUnavailable) {
            try await service.list(try ListRemindersQuery())
        }
        // Same answer when only broken bindings exist.
        let broken = self.service(Allowlist(entries: [try allowlistEntry("gone", state: .broken)]))
        await #expect(throws: ApiError.remindersUnavailable) {
            try await broken.list(try ListRemindersQuery())
        }
    }

    @Test("an unmapped id never succeeds")
    func unmappedIdNeverSucceeds() async throws {
        let service = service(Allowlist(entries: [try allowlistEntry("inbox")]))
        // 404 when authorized, 503 when not — never a success, and never a 500.
        await #expect(throws: ApiError.self) { try await service.complete(id: .generate()) }
    }

    @Test("an alias outside the table is refused before anything is fetched")
    func unknownAliasIsRefused() async throws {
        let service = service(Allowlist(entries: [try allowlistEntry("inbox")]))
        let query = try ListRemindersQuery(aliases: [try alias("personal")])
        await #expect(throws: ApiError.self) { try await service.list(query) }
    }

    /// The seam's whole point: `EventKitReminders` is substitutable for `FakeReminders` in
    /// `BridgeServices` without the request layer knowing which it got.
    @Test("it satisfies the RemindersService seam")
    func conformsToTheSeam() throws {
        let service: any RemindersService = service(Allowlist(entries: [try allowlistEntry("inbox")]))
        #expect(service is EventKitReminders)
    }
}
