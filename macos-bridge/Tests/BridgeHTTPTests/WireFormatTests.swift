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
@Suite("Wire format — the exact JSON the six routes emit")
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

    @Test("GET /v1/status carries both capabilities, each with its own availability")
    func statusShape() async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(to: harness.request(.GET, "/v1/status"))

        #expect(response.status == 200)
        let payload = try harness.json(response)
        #expect(
            Set(payload.keys) == ["availability", "lists", "calendarAvailability", "calendars"],
            "got \(payload.keys)"
        )
        #expect(payload["availability"] as? String == "ok")
        // Sorted, so a client can present them without re-sorting. `brokenAliases` is gone with
        // the allowlist that produced it.
        #expect(payload["lists"] as? [String] == ["Groceries", "Work"])
        #expect(payload["calendarAvailability"] as? String == "ok")
        #expect(payload["calendars"] as? [String] == ["Arbeit", "Privat"])
    }

    /// The two availabilities are independent on the wire, not one verdict written twice: a Mac
    /// with reminders granted and calendars denied has to be able to say exactly that.
    @Test("one capability being unavailable does not change the other's report")
    func statusReportsCapabilitiesIndependently() async throws {
        let harness = try TestHarness()
        await harness.calendar.setAvailability(.unauthorized)

        let payload = try harness.json(
            await harness.responder.respond(to: harness.request(.GET, "/v1/status"))
        )
        #expect(payload["availability"] as? String == "ok")
        #expect(payload["lists"] as? [String] == ["Groceries", "Work"])
        #expect(payload["calendarAvailability"] as? String == "unauthorized")
        // Empty rather than absent: a client must not have to distinguish "no calendars" from
        // "the key was omitted".
        #expect(try #require(payload["calendars"] as? [Any]).isEmpty)
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

    // MARK: - POST /v1/calendar-events

    /// Every key a `CalendarEventSnapshot` can carry. Note what is **absent**: there is no `id`.
    /// No route takes an event id — no complete, no edit, no delete — so one would be a handle to
    /// nothing, and shipping it would imply an operation the bridge does not have.
    static let eventKeys: Set<String> = [
        "calendar", "title", "notes", "startAt", "endAt", "isAllDay", "timeZone",
    ]

    /// A create carrying every optional the request can express.
    static let fullEventBody = #"""
    {"calendar":"Privat","title":"Dentist","notes":"bring the referral","startAt":"2026-05-29T09:00:00+02:00","endAt":"2026-05-29T10:00:00+02:00","timeZone":"Europe/Berlin"}
    """#

    @Test("POST /v1/calendar-events is a single event object, not wrapped and not an array")
    func createEventShape() async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(
            to: harness.request(.POST, "/v1/calendar-events", body: Self.fullEventBody)
        )

        #expect(response.status == 201)
        let payload = try harness.json(response)
        #expect(Set(payload.keys) == Self.eventKeys, "got \(payload.keys)")
        #expect(payload["calendar"] as? String == "Privat")
        #expect(payload["title"] as? String == "Dentist")
        #expect(payload["notes"] as? String == "bring the referral")
        // Rendered as UTC on the way out, the same as every other timestamp this API emits — the
        // offset the caller sent is preserved as an instant, not as a rendering.
        #expect(payload["startAt"] as? String == "2026-05-29T07:00:00Z")
        #expect(payload["endAt"] as? String == "2026-05-29T08:00:00Z")
        #expect(payload["isAllDay"] as? Bool == false)
        #expect(payload["timeZone"] as? String == "Europe/Berlin")
        #expect(payload["id"] == nil, "an event must not carry an id it cannot be addressed by")
    }

    @Test("a nil optional is omitted from an event, never written as null")
    func eventOmitsNilOptionals() async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(
            to: harness.request(.POST, "/v1/calendar-events", body: createEventBody)
        )

        #expect(response.status == 201)
        let payload = try harness.json(response)
        // `notes` was absent; `timeZone` still appears, because the handler resolves the absent
        // one to the bridge's own zone rather than leaving the event floating.
        #expect(Set(payload.keys) == Self.eventKeys.subtracting(["notes"]), "got \(payload.keys)")
    }

    // MARK: - GET /v1/calendar-events

    @Test("GET /v1/calendar-events is the {\"items\":[…]} wrapper, never a bare array")
    func listEventsShape() async throws {
        let harness = try TestHarness()
        _ = await harness.responder.respond(
            to: harness.request(.POST, "/v1/calendar-events", body: Self.fullEventBody)
        )

        let response = await harness.responder.respond(to: harness.request(.GET, "/v1/calendar-events"))
        #expect(response.status == 200)

        // The regression this file exists for: a top-level array can never gain a field later.
        let top = try JSONSerialization.jsonObject(with: Data(response.body))
        #expect(top as? [Any] == nil, "the body is a bare array")

        let payload = try harness.json(response)
        #expect(Set(payload.keys) == ["items"], "got \(payload.keys)")

        let items = try #require(payload["items"] as? [[String: Any]])
        #expect(items.count == 1)
        let item = try #require(items.first)
        #expect(Set(item.keys) == Self.eventKeys, "got \(item.keys)")
        #expect(item["title"] as? String == "Dentist")
        #expect(item["startAt"] as? String == "2026-05-29T07:00:00Z")
    }

    @Test("an empty calendar is an empty items array, not an omitted key or a null")
    func emptyEventListShape() async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(to: harness.request(.GET, "/v1/calendar-events"))

        #expect(response.status == 200)
        let payload = try harness.json(response)
        #expect(Set(payload.keys) == ["items"], "got \(payload.keys)")
        #expect(try #require(payload["items"] as? [Any]).isEmpty)
    }

    /// An all-day event has to say so. A caller told only "starts 22:00Z" would report a birthday
    /// at the wrong time, in the wrong day, for anyone east of London.
    @Test("an all-day event is flagged rather than reported as a timed one")
    func allDayFlagSurvives() async throws {
        let harness = try TestHarness()
        await harness.calendar.seed(
            CalendarEventSnapshot(
                calendar: try calendarName("Privat"),
                title: "Birthday",
                startAt: Date(timeIntervalSince1970: 1_780_012_800),
                endAt: Date(timeIntervalSince1970: 1_780_099_200),
                isAllDay: true,
                timeZone: nil
            )
        )

        let items = try harness.jsonItems(
            await harness.responder.respond(to: harness.request(.GET, "/v1/calendar-events"))
        )
        let item = try #require(items.first)
        // `timeZone` is genuinely nil for a floating event, so it is omitted rather than invented.
        #expect(Set(item.keys) == Self.eventKeys.subtracting(["notes", "timeZone"]), "got \(item.keys)")
        #expect(item["isAllDay"] as? Bool == true)
    }
}
