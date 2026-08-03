import Foundation
import Testing

@testable import BridgeCore

/// `FakeReminders` is what `BridgeHTTP` (M3) and the .NET client are written against before
/// EventKit exists, so its *contract* has to match the real one — a fake that fails differently
/// would make those tests worthless. These pin the behaviours that matter: writes go to the pinned
/// list and nowhere else, an unpinned or vanished target fails closed rather than defaulting, a read
/// filter still fails closed on an unknown name, a read-only list cannot take a reminder, and
/// complete stays a no-op the second time.
@Suite("Fake reminders service")
struct FakeRemindersTests {
    private func fake(
        lists: [String] = ["Groceries", "Work"],
        writeList: String? = "Groceries",
        readOnly: [String] = []
    ) throws -> FakeReminders {
        FakeReminders(
            lists: Set(try lists.map { try listName($0) }),
            writeList: try writeList.map { try listName($0) },
            readOnly: Set(try readOnly.map { try listName($0) })
        )
    }

    private func command(title: String = "Buy milk") -> CreateReminderCommand {
        CreateReminderCommand(
            id: BridgeID.generate(),
            title: title,
            notes: nil,
            dueAt: nil,
            priority: 0
        )
    }

    @Test("a created reminder comes back from a listing")
    func createThenList() async throws {
        let subject = try fake()
        let created = try await subject.create(command())
        #expect(created.title == "Buy milk")
        #expect(created.list.rawValue == "Groceries")

        let listed = try await subject.list(try ListRemindersQuery())
        #expect(listed.map(\.id) == [created.id])
    }

    /// The write target is not something a create can steer, so this is the assertion that it lands
    /// where it was pinned — including when that is *not* the alphabetically first list, which is
    /// what a lazy implementation would pick.
    @Test("a create lands in the pinned list, whatever else this Mac has")
    func createUsesThePinnedList() async throws {
        let subject = try fake(writeList: "Work")
        let created = try await subject.create(command())

        #expect(created.list.rawValue == "Work")
        #expect(await subject.all.map(\.list.rawValue) == ["Work"])
    }

    /// Nothing pinned. There are two perfectly good writable lists sitting right there, and the
    /// answer is still no — a default here is exactly the guess this design exists to remove.
    @Test("with no list pinned, a create fails closed rather than picking one")
    func unpinnedFailsClosed() async throws {
        let subject = try fake(writeList: nil)
        await #expect(throws: ApiError.listNotConfigured) {
            try await subject.create(self.command())
        }
        #expect(await subject.all.isEmpty, "a refused create still wrote something")
        // Reads are untouched: the narrowing is on writes only.
        #expect(try await subject.list(try ListRemindersQuery()).isEmpty)
        #expect(await subject.availableLists().map(\.rawValue) == ["Groceries", "Work"])
    }

    /// Pinned, then deleted in Reminders.app. The same refusal as never having pinned one — and
    /// emphatically not a re-bind onto whatever else is around.
    @Test("a pinned list that has gone fails closed rather than re-binding")
    func vanishedTargetFailsClosed() async throws {
        let subject = try fake()
        await subject.removeList(try listName("Groceries"))

        await #expect(throws: ApiError.listNotConfigured) {
            try await subject.create(self.command())
        }
        #expect(await subject.all.isEmpty)
    }

    /// The status readout has to tell the two apart even though the create does not.
    @Test("the write-list readout distinguishes never-chosen from gone")
    func writeListReport() async throws {
        #expect(await (try fake(writeList: nil)).writeList() == .notConfigured)
        #expect(await (try fake()).writeList() == .configured(try listName("Groceries")))

        let vanished = try fake()
        await vanished.removeList(try listName("Groceries"))
        #expect(await vanished.writeList() == .unresolvable(try listName("Groceries")))
    }

    @Test("a read filter naming no list fails closed, never widening to all of them")
    func unknownNameFailsClosed() async throws {
        let subject = try fake()
        await #expect(throws: ApiError.noSuchList) {
            try await subject.list(try ListRemindersQuery(lists: [try listName("Personal")]))
        }
    }

    @Test("a read-only list exists but cannot take a reminder")
    func readOnlyList() async throws {
        let subject = try fake(writeList: "Work", readOnly: ["Work"])
        await #expect(throws: ApiError.listReadOnly) {
            try await subject.create(self.command())
        }
        // It is still readable — read-only means read-only, not invisible.
        #expect(await subject.availableLists().map(\.rawValue) == ["Groceries", "Work"])
    }

    /// A list deleted in Reminders.app leaves its reminders mapped but unreachable. The id has to
    /// stop resolving rather than quietly still completing.
    @Test("a reminder whose list is gone becomes a 404 on complete, not a silent success")
    func deletedListFailsClosed() async throws {
        let subject = try fake()
        let created = try await subject.create(command())
        await subject.removeList(try listName("Groceries"))

        let listed = try await subject.list(try ListRemindersQuery())
        #expect(listed.isEmpty)
        await #expect(throws: ApiError.notFound) {
            try await subject.complete(id: created.id)
        }
    }

    /// Reads span everything, and re-pinning between two creates is now the *only* way a reminder
    /// reaches a second list — which is exactly the asymmetry being tested.
    @Test("listing with no name means every list on the Mac, and naming one narrows to it")
    func filtering() async throws {
        let subject = try fake()
        _ = try await subject.create(command(title: "A"))
        await subject.setWriteList(try listName("Work"))
        _ = try await subject.create(command(title: "B"))

        let listed = try await subject.list(try ListRemindersQuery())
        #expect(listed.count == 2)

        let onlyGroceries = try await subject.list(
            try ListRemindersQuery(lists: [try listName("Groceries")])
        )
        #expect(onlyGroceries.map(\.title) == ["A"])
    }

    @Test("status reports the names a read may filter by")
    func reportsAvailableLists() async throws {
        let subject = try fake()
        #expect(await subject.availableLists() == [try listName("Groceries"), try listName("Work")])

        await subject.setAvailability(.unauthorized)
        #expect(await subject.availableLists().isEmpty)
    }

    @Test("completed reminders drop out of the list")
    func hidesCompleted() async throws {
        let subject = try fake()
        let created = try await subject.create(command())
        let outcome = try await subject.complete(id: created.id)
        #expect(!outcome.alreadyCompleted)

        let listed = try await subject.list(try ListRemindersQuery())
        #expect(listed.isEmpty)
    }

    @Test("completing twice is a success no-op, not an error")
    func completeIsIdempotent() async throws {
        let subject = try fake()
        let created = try await subject.create(command())
        _ = try await subject.complete(id: created.id)

        let second = try await subject.complete(id: created.id)
        #expect(second.alreadyCompleted)
        #expect(second.id == created.id)
    }

    @Test("an unknown id is a 404")
    func unknownIdIsNotFound() async throws {
        let subject = try fake()
        await #expect(throws: ApiError.notFound) {
            try await subject.complete(id: BridgeID.generate())
        }
    }

    @Test("the list limit is honoured")
    func honoursLimit() async throws {
        let subject = try fake()
        for index in 0..<5 { _ = try await subject.create(command(title: "T\(index)")) }

        let listed = try await subject.list(try ListRemindersQuery(limit: 3))
        #expect(listed.count == 3)
    }

    @Test("revoked access short-circuits every operation to reminders_unavailable")
    func unavailableWhenUnauthorized() async throws {
        let subject = try fake()
        await subject.setAvailability(.unauthorized)

        #expect(await subject.availability() == .unauthorized)
        await #expect(throws: ApiError.remindersUnavailable) {
            try await subject.list(try ListRemindersQuery())
        }
        await #expect(throws: ApiError.remindersUnavailable) {
            try await subject.create(self.command())
        }
        await #expect(throws: ApiError.remindersUnavailable) {
            try await subject.complete(id: BridgeID.generate())
        }
    }

    @Test("a forced error lets handler error paths be exercised")
    func forcesErrors() async throws {
        let subject = try fake()
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
        let subject: any RemindersService = try fake()
        #expect(await subject.availability() == .ok)
    }
}
