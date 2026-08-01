import BridgeCore
import Foundation
import NIOHTTP1
import Testing

@testable import BridgeHTTP

/// What actually goes on the wire, asserted against the response **bytes** — never by decoding
/// back into the same `Codable` type that produced them.
///
/// A round-trip through `ReminderSnapshot` cannot see the difference between `[…]` and
/// `{"items":[…]}`, which is exactly how the list route shipped a bare array while every existing
/// test stayed green. These tests parse with `JSONSerialization` and pin the key sets, so a field
/// that appears, disappears or gets renamed fails here before a client discovers it.
@Suite("Wire format — the exact JSON the four routes emit")
struct WireFormatTests {
    /// Every key a `ReminderSnapshot` can carry.
    static let snapshotKeys: Set<String> = [
        "id", "list", "title", "notes", "dueAt", "priority", "isCompleted", "completedAt",
    ]

    /// The subset always present: `notes`, `dueAt` and `completedAt` are optional, and the encoder
    /// omits an optional that is nil rather than writing `null`.
    static let requiredSnapshotKeys: Set<String> = ["id", "list", "title", "priority", "isCompleted"]

    /// A create carrying every optional the request can express, so the response body shows the
    /// spelling of `notes`, `dueAt` and `priority` as well as the mandatory fields.
    static let fullCreateBody = #"""
    {"list":"Groceries","title":"Buy milk","notes":"semi-skimmed","dueAt":"2026-08-01T09:00:00Z","priority":5}
    """#

    // MARK: - GET /v1/status

    @Test("GET /v1/status is an object with exactly availability and lists")
    func statusShape() async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(to: harness.request(.GET, "/v1/status"))

        #expect(response.status == 200)
        let payload = try harness.json(response)
        #expect(Set(payload.keys) == ["availability", "lists"], "got \(payload.keys)")
        #expect(payload["availability"] as? String == "ok")
        // Sorted, so a client can present them without re-sorting. `brokenAliases` is gone with
        // the allowlist that produced it.
        #expect(payload["lists"] as? [String] == ["Groceries", "Work"])
    }

    // MARK: - POST /v1/reminders

    @Test("POST /v1/reminders is a single snapshot object, not wrapped and not an array")
    func createShape() async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: Self.fullCreateBody)
        )

        #expect(response.status == 201)
        let payload = try harness.json(response)
        // `completedAt` is nil on a fresh create and therefore absent — see `omitsNilOptionals`.
        #expect(Set(payload.keys) == Self.snapshotKeys.subtracting(["completedAt"]), "got \(payload.keys)")

        #expect((payload["id"] as? String)?.hasPrefix("rem_") == true)
        #expect(payload["list"] as? String == "Groceries")
        #expect(payload["title"] as? String == "Buy milk")
        #expect(payload["notes"] as? String == "semi-skimmed")
        #expect(payload["dueAt"] as? String == "2026-08-01T09:00:00Z")
        #expect(payload["priority"] as? Int == 5)
        #expect(payload["isCompleted"] as? Bool == false)
    }

    @Test("a nil optional is omitted from a snapshot, never written as null")
    func omitsNilOptionals() async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: createBody)
        )

        #expect(response.status == 201)
        let payload = try harness.json(response)
        #expect(Set(payload.keys) == Self.requiredSnapshotKeys, "got \(payload.keys)")
    }

    // MARK: - GET /v1/reminders

    @Test("GET /v1/reminders is the {\"items\":[…]} wrapper, never a bare array")
    func listShape() async throws {
        let harness = try TestHarness()
        _ = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: Self.fullCreateBody)
        )

        let response = await harness.responder.respond(to: harness.request(.GET, "/v1/reminders"))
        #expect(response.status == 200)

        // The regression itself: a top-level array can never gain a field later.
        let top = try JSONSerialization.jsonObject(with: Data(response.body))
        #expect(top as? [Any] == nil, "the body is a bare array")

        let payload = try harness.json(response)
        #expect(Set(payload.keys) == ["items"], "got \(payload.keys)")

        let items = try #require(payload["items"] as? [[String: Any]])
        #expect(items.count == 1)
        let item = try #require(items.first)
        #expect(Set(item.keys) == Self.snapshotKeys.subtracting(["completedAt"]), "got \(item.keys)")
        #expect(item["title"] as? String == "Buy milk")
        #expect(item["dueAt"] as? String == "2026-08-01T09:00:00Z")
    }

    @Test("an empty list is an empty items array, not an omitted key or a null")
    func emptyListShape() async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(to: harness.request(.GET, "/v1/reminders"))

        #expect(response.status == 200)
        let payload = try harness.json(response)
        #expect(Set(payload.keys) == ["items"], "got \(payload.keys)")
        #expect(try #require(payload["items"] as? [Any]).isEmpty)
    }

    @Test("a snapshot carrying a completion timestamp spells it completedAt")
    func completedAtSpelling() async throws {
        let harness = try TestHarness()
        // Seeded rather than created-then-completed: `FakeReminders.list` filters completed
        // reminders out, so no route can otherwise put `completedAt` on the wire. The pairing of
        // `isCompleted: false` with a `completedAt` is not a state EventKit produces — it exists
        // purely to force the last optional through the encoder.
        await harness.reminders.seed(
            ReminderSnapshot(
                id: BridgeID.generate(),
                list: try listName("Groceries"),
                title: "Seeded",
                notes: "n",
                dueAt: Date(timeIntervalSince1970: 1_780_000_000),
                priority: 1,
                isCompleted: false,
                completedAt: Date(timeIntervalSince1970: 1_780_000_500)
            )
        )

        let response = await harness.responder.respond(to: harness.request(.GET, "/v1/reminders"))
        #expect(response.status == 200)

        let items = try harness.jsonItems(response)
        let item = try #require(items.first)
        #expect(Set(item.keys) == Self.snapshotKeys, "got \(item.keys)")
        #expect(item["completedAt"] as? String == "2026-05-28T20:35:00Z")
        #expect(item["dueAt"] as? String == "2026-05-28T20:26:40Z")
    }

    // MARK: - POST /v1/reminders/{id}/complete

    @Test("POST /v1/reminders/{id}/complete is exactly id and alreadyCompleted")
    func completeShape() async throws {
        let harness = try TestHarness()
        let created = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: createBody)
        )
        let id = try #require(try harness.json(created)["id"] as? String)

        let response = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders/\(id)/complete", contentType: nil)
        )

        #expect(response.status == 200)
        let payload = try harness.json(response)
        #expect(Set(payload.keys) == ["id", "alreadyCompleted"], "got \(payload.keys)")
        #expect(payload["id"] as? String == id)
        #expect(payload["alreadyCompleted"] as? Bool == false)
    }
}
