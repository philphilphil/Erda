import Foundation
import Testing

@testable import BridgeCore

/// `FakeReminders` is what `BridgeHTTP` (M3) and the M6 .NET client are written against before
/// EventKit exists, so its contract is worth pinning down here.
@Suite("Fake reminders service")
struct FakeRemindersTests {
    private func service() throws -> FakeReminders {
        FakeReminders(aliases: [try alias("inbox"), try alias("work")])
    }

    private func command(_ aliasName: String, title: String = "Buy milk") throws -> CreateReminderCommand {
        CreateReminderCommand(
            id: BridgeID.generate(),
            alias: try alias(aliasName),
            title: title,
            notes: nil,
            dueAt: nil,
            priority: 0
        )
    }

    @Test("create then list round-trips through an allowlisted alias")
    func createsAndLists() async throws {
        let subject = try service()
        let created = try await subject.create(try command("inbox"))
        #expect(created.title == "Buy milk")

        let listed = try await subject.list(try ListRemindersQuery())
        #expect(listed.map(\.id) == [created.id])
    }

    @Test("an unknown alias fails closed on both create and list")
    func unknownAliasFailsClosed() async throws {
        let subject = try service()
        await #expect(throws: ApiError.aliasUnknown) {
            try await subject.create(try self.command("personal"))
        }
        await #expect(throws: ApiError.aliasUnknown) {
            try await subject.list(try ListRemindersQuery(aliases: [try alias("personal")]))
        }
    }

    @Test("a broken alias is refused, and its reminders stop being visible")
    func brokenAliasFailsClosed() async throws {
        let subject = try service()
        let created = try await subject.create(try command("work"))
        await subject.markBroken(try alias("work"))

        await #expect(throws: ApiError.aliasBroken) {
            try await subject.create(try self.command("work"))
        }
        let listed = try await subject.list(try ListRemindersQuery())
        #expect(listed.isEmpty)
        // The id still exists, but the caller has no business learning that.
        await #expect(throws: ApiError.notFound) {
            try await subject.complete(id: created.id)
        }
    }

    @Test("listing without aliases means every healthy list, never every list on the Mac")
    func defaultsToHealthyAliases() async throws {
        let subject = try service()
        _ = try await subject.create(try command("inbox", title: "A"))
        _ = try await subject.create(try command("work", title: "B"))

        let listed = try await subject.list(try ListRemindersQuery())
        #expect(listed.count == 2)

        let onlyInbox = try await subject.list(try ListRemindersQuery(aliases: [try alias("inbox")]))
        #expect(onlyInbox.map(\.title) == ["A"])
    }

    @Test("completed reminders drop out of the list")
    func hidesCompleted() async throws {
        let subject = try service()
        let created = try await subject.create(try command("inbox"))
        let outcome = try await subject.complete(id: created.id)
        #expect(!outcome.alreadyCompleted)

        let listed = try await subject.list(try ListRemindersQuery())
        #expect(listed.isEmpty)
    }

    @Test("completing twice is a success no-op, not an error")
    func completeIsIdempotent() async throws {
        let subject = try service()
        let created = try await subject.create(try command("inbox"))
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
        for index in 0..<5 { _ = try await subject.create(try command("inbox", title: "T\(index)")) }

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
            try await subject.create(try self.command("inbox"))
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
