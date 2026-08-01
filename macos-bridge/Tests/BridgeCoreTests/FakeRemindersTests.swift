import Foundation
import Testing

@testable import BridgeCore

/// `FakeReminders` is what `BridgeHTTP` (M3) and the .NET client are written against before
/// EventKit exists, so its contract is worth pinning down here.
@Suite("Fake reminders service")
struct FakeRemindersTests {
    private func service() throws -> FakeReminders {
        FakeReminders(lists: [try listName("Groceries"), try listName("Work")])
    }

    private func command(_ list: String, title: String = "Buy milk") throws -> CreateReminderCommand {
        CreateReminderCommand(
            id: BridgeID.generate(),
            list: try listName(list),
            title: title,
            notes: nil,
            dueAt: nil,
            priority: 0
        )
    }

    @Test("create then list round-trips through a named list")
    func createsAndLists() async throws {
        let subject = try service()
        let created = try await subject.create(try command("Groceries"))
        #expect(created.title == "Buy milk")

        let listed = try await subject.list(try ListRemindersQuery())
        #expect(listed.map(\.id) == [created.id])
    }

    @Test("a name that matches no list fails closed on both create and list")
    func unknownListFailsClosed() async throws {
        let subject = try service()
        await #expect(throws: ApiError.noSuchList) {
            try await subject.create(try self.command("Personal"))
        }
        await #expect(throws: ApiError.noSuchList) {
            try await subject.list(try ListRemindersQuery(lists: [try listName("Personal")]))
        }
    }

    @Test("a read-only list refuses a create but still lists")
    func readOnlyListRefusesCreate() async throws {
        let subject = try service()
        let shared = try listName("Work")
        _ = try await subject.create(try command("Work", title: "Existing"))
        await subject.markReadOnly(shared)

        await #expect(throws: ApiError.listReadOnly) {
            try await subject.create(try self.command("Work", title: "New"))
        }
        let listed = try await subject.list(try ListRemindersQuery(lists: [shared]))
        #expect(listed.map(\.title) == ["Existing"])
    }

    /// A list deleted in Reminders.app leaves its reminders mapped but unreachable. The id has to
    /// stop resolving rather than quietly still completing.
    @Test("a reminder whose list is gone becomes a 404, not a silent success")
    func deletedListFailsClosed() async throws {
        let subject = try service()
        let created = try await subject.create(try command("Work"))
        await subject.removeList(try listName("Work"))

        await #expect(throws: ApiError.noSuchList) {
            try await subject.create(try self.command("Work"))
        }
        let listed = try await subject.list(try ListRemindersQuery())
        #expect(listed.isEmpty)
        await #expect(throws: ApiError.notFound) {
            try await subject.complete(id: created.id)
        }
    }

    /// The behaviour change the allowlist's removal *is*: no filter now means every list on the
    /// Mac, and that is the documented contract rather than an accident.
    @Test("listing with no name means every list on the Mac")
    func defaultsToEveryList() async throws {
        let subject = try service()
        _ = try await subject.create(try command("Groceries", title: "A"))
        _ = try await subject.create(try command("Work", title: "B"))

        let listed = try await subject.list(try ListRemindersQuery())
        #expect(listed.count == 2)

        let onlyGroceries = try await subject.list(
            try ListRemindersQuery(lists: [try listName("Groceries")])
        )
        #expect(onlyGroceries.map(\.title) == ["A"])
    }

    @Test("status reports the names a caller may address")
    func reportsAvailableLists() async throws {
        let subject = try service()
        #expect(await subject.availableLists() == [try listName("Groceries"), try listName("Work")])

        await subject.setAvailability(.unauthorized)
        #expect(await subject.availableLists().isEmpty)
    }

    @Test("completed reminders drop out of the list")
    func hidesCompleted() async throws {
        let subject = try service()
        let created = try await subject.create(try command("Groceries"))
        let outcome = try await subject.complete(id: created.id)
        #expect(!outcome.alreadyCompleted)

        let listed = try await subject.list(try ListRemindersQuery())
        #expect(listed.isEmpty)
    }

    @Test("completing twice is a success no-op, not an error")
    func completeIsIdempotent() async throws {
        let subject = try service()
        let created = try await subject.create(try command("Groceries"))
        _ = try await subject.complete(id: created.id)

        let second = try await subject.complete(id: created.id)
        #expect(second.alreadyCompleted)
        #expect(second.id == created.id)
    }

    @Test("an unknown id is a 404")
    func unknownIdIsNotFound() async throws {
        let subject = try service()
        await #expect(throws: ApiError.notFound) {
            try await subject.complete(id: BridgeID.generate())
        }
    }

    @Test("the list limit is honoured")
    func honoursLimit() async throws {
        let subject = try service()
        for index in 0..<5 { _ = try await subject.create(try command("Groceries", title: "T\(index)")) }

        let listed = try await subject.list(try ListRemindersQuery(limit: 3))
        #expect(listed.count == 3)
    }

    @Test("revoked access short-circuits every operation to reminders_unavailable")
    func unavailableWhenUnauthorized() async throws {
        let subject = try service()
        await subject.setAvailability(.unauthorized)

        #expect(await subject.availability() == .unauthorized)
        await #expect(throws: ApiError.remindersUnavailable) {
            try await subject.list(try ListRemindersQuery())
        }
        await #expect(throws: ApiError.remindersUnavailable) {
            try await subject.create(try self.command("Groceries"))
        }
        await #expect(throws: ApiError.remindersUnavailable) {
            try await subject.complete(id: BridgeID.generate())
        }
    }

    @Test("a forced error lets handler error paths be exercised")
    func forcesErrors() async throws {
        let subject = try service()
        await subject.setForcedError(.internal)
        await #expect(throws: ApiError.internal) {
            try await subject.list(try ListRemindersQuery())
        }

        await subject.setForcedError(nil)
        let listed = try await subject.list(try ListRemindersQuery())
        #expect(listed.isEmpty)
    }

    @Test("the fake satisfies the seam, so handlers can hold `any RemindersService`")
    func conformsToTheSeam() async throws {
        let subject: any RemindersService = try service()
        #expect(await subject.availability() == .ok)
    }
}
