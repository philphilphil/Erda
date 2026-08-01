import BridgeCore
import Foundation
import NIOHTTP1
import Testing

@testable import BridgeHTTP

/// The two calendar routes driven through the whole chain — protocol gate, auth, rate limit,
/// content negotiation, strict decode, idempotency, domain, audit — against `FakeCalendar`.
@Suite("Calendar routes")
struct CalendarResponderTests {
    // MARK: - Creating

    @Test("a well-formed create is a 201 carrying the event")
    func createSucceeds() async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(
            to: harness.request(.POST, "/v1/calendar-events", body: createEventBody)
        )

        #expect(response.status == 201)
        let payload = try harness.json(response)
        #expect(payload["title"] as? String == "Dentist")
        #expect(payload["calendar"] as? String == "Privat")
        #expect(await harness.calendar.all.count == 1)
    }

    /// The event lands in the pinned calendar, and the response says which — the caller never named
    /// one, so this is the only way it can tell Phil where the appointment went.
    @Test("a create lands in the pinned calendar and reports it back")
    func createUsesThePinnedCalendar() async throws {
        let harness = try TestHarness(calendars: ["Privat", "Arbeit"], writeCalendar: "Arbeit")
        let response = await harness.responder.respond(
            to: harness.request(.POST, "/v1/calendar-events", body: createEventBody)
        )

        #expect(response.status == 201)
        #expect(try harness.json(response)["calendar"] as? String == "Arbeit")
        #expect(await harness.calendar.all.map(\.calendar.rawValue) == ["Arbeit"])
    }

    /// Both ways of having no usable write target, and both fail closed with the same 503 rather
    /// than landing in whatever calendar happens to be around. It is deliberately *not* the
    /// `calendar_unavailable` 503: that one sends Phil to System Settings, this one to the
    /// ErdaBridge window.
    @Test("a create with no usable write calendar is a 503 that writes nothing", arguments: [
        "never configured", "configured but gone",
    ])
    func createFailsClosedWithoutAWriteCalendar(scenario: String) async throws {
        let harness = try TestHarness(writeCalendar: scenario == "never configured" ? nil : "Privat")
        if scenario != "never configured" {
            await harness.calendar.forget(try calendarName("Privat"))
        }

        let response = await harness.responder.respond(
            to: harness.request(.POST, "/v1/calendar-events", body: createEventBody)
        )
        #expect(response.status == 503)
        #expect(try harness.errorCode(response) == "calendar_not_configured")
        #expect(await harness.calendar.all.isEmpty)

        // Reads are untouched by either: the narrowing is on writes only.
        #expect(await harness.responder.respond(to: harness.request(.GET, "/v1/calendar-events")).status == 200)
    }

    /// The picker only offers writable calendars, so this is the calendar that became read-only
    /// *after* it was pinned — a 409 rather than a silent hop to a different calendar.
    @Test("a pinned calendar that cannot take events is a conflict, not a redirect")
    func createRefusesAReadOnlyPinnedCalendar() async throws {
        let harness = try TestHarness(
            calendars: ["Privat", "Feiertage"],
            writeCalendar: "Feiertage",
            readOnlyCalendars: ["Feiertage"]
        )

        let response = await harness.responder.respond(
            to: harness.request(.POST, "/v1/calendar-events", body: createEventBody)
        )
        #expect(response.status == 409)
        #expect(try harness.errorCode(response) == "calendar_read_only")
        #expect(await harness.calendar.all.isEmpty)
    }

    /// Every one of these fails at decode, so no calendar is even resolved.
    @Test("a malformed create is a 400 and touches nothing", arguments: [
        #"{"title":"x","startAt":"2026-05-29T10:00:00Z","endAt":"2026-05-29T09:00:00Z"}"#,
        #"{"title":"x","startAt":"2026-05-29T09:00:00","endAt":"2026-05-29T10:00:00Z"}"#,
        #"{"title":"x","startAt":"2026-05-29T09:00:00Z","endAt":"2026-07-29T09:00:00Z"}"#,
        #"{"title":"","startAt":"2026-05-29T09:00:00Z","endAt":"2026-05-29T10:00:00Z"}"#,
        #"{"title":"x","startAt":"2026-05-29T09:00:00Z","endAt":"2026-05-29T10:00:00Z","timeZone":"CEST"}"#,
        #"{"title":"x","startAt":"2026-05-29T09:00:00Z","endAt":"2026-05-29T10:00:00Z","recurrence":"FREQ=DAILY"}"#,
        #"not json"#,
    ])
    func malformedCreateIs400(body: String) async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(
            to: harness.request(.POST, "/v1/calendar-events", body: body)
        )

        #expect(response.status == 400)
        #expect(try harness.errorCode(response) == "invalid_request")
        #expect(await harness.calendar.all.isEmpty)
    }

    @Test("a create without an Idempotency-Key is refused, like every other mutation")
    func createRequiresIdempotencyKey() async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(
            to: harness.request(.POST, "/v1/calendar-events", body: createEventBody, idempotencyKey: nil)
        )

        #expect(response.status == 400)
        #expect(await harness.calendar.all.isEmpty)
    }

    /// A retry after a timeout must not put a second appointment on the calendar.
    @Test("the same key and body replays instead of creating a second event")
    func createIsIdempotent() async throws {
        let harness = try TestHarness()
        let key = "calendar-replay"

        let first = await harness.responder.respond(
            to: harness.request(.POST, "/v1/calendar-events", body: createEventBody, idempotencyKey: key)
        )
        let second = await harness.responder.respond(
            to: harness.request(.POST, "/v1/calendar-events", body: createEventBody, idempotencyKey: key)
        )

        #expect(first.status == 201)
        #expect(second.status == 201)
        #expect(second.body == first.body)
        #expect(harness.header(second, "Idempotency-Replayed") == "true")
        #expect(await harness.calendar.all.count == 1)
    }

    @Test("the same key with a different body is a conflict, not a second event")
    func createKeyReuseConflicts() async throws {
        let harness = try TestHarness()
        let key = "calendar-reuse"
        _ = await harness.responder.respond(
            to: harness.request(.POST, "/v1/calendar-events", body: createEventBody, idempotencyKey: key)
        )

        let other = #"""
        {"title":"Standup","startAt":"2026-05-29T09:00:00Z","endAt":"2026-05-29T09:15:00Z"}
        """#
        let response = await harness.responder.respond(
            to: harness.request(.POST, "/v1/calendar-events", body: other, idempotencyKey: key)
        )

        #expect(response.status == 409)
        #expect(try harness.errorCode(response) == "idempotency_key_reuse")
        #expect(await harness.calendar.all.count == 1)
    }

    @Test("a create must be JSON with a body")
    func createContentNegotiation() async throws {
        let harness = try TestHarness()

        let wrongType = await harness.responder.respond(
            to: harness.request(.POST, "/v1/calendar-events", body: createEventBody, contentType: "text/plain")
        )
        #expect(wrongType.status == 415)

        // No `Content-Type` at all is refused before the body is even looked at.
        let noType = await harness.responder.respond(
            to: harness.request(.POST, "/v1/calendar-events", body: "")
        )
        #expect(noType.status == 415)

        // JSON, but nothing in it. The header is added by hand because the harness only attaches
        // one to a non-empty body.
        let empty = await harness.responder.respond(
            to: harness.request(
                .POST,
                "/v1/calendar-events",
                body: "",
                extraHeaders: [("Content-Type", "application/json")]
            )
        )
        #expect(empty.status == 400)
    }

    // MARK: - Listing

    @Test("listing returns what was created, in the wrapper")
    func listReturnsEvents() async throws {
        let harness = try TestHarness()
        _ = await harness.responder.respond(
            to: harness.request(.POST, "/v1/calendar-events", body: createEventBody)
        )

        let items = try harness.jsonItems(
            await harness.responder.respond(to: harness.request(.GET, "/v1/calendar-events"))
        )
        #expect(items.count == 1)
        #expect(items[0]["title"] as? String == "Dentist")
    }

    @Test("naming a calendar narrows the listing; omitting it spans every calendar")
    func listFilters() async throws {
        let harness = try TestHarness()
        _ = await harness.responder.respond(
            to: harness.request(.POST, "/v1/calendar-events", body: createEventBody)
        )
        // Re-pinned locally between the two creates — an event only ever reaches a second calendar
        // that way now, which is what makes "reads span both, writes do not" testable at all.
        await harness.calendar.setWriteCalendar(try calendarName("Arbeit"))
        _ = await harness.responder.respond(
            to: harness.request(
                .POST,
                "/v1/calendar-events",
                body: #"{"title":"Standup","startAt":"2026-05-29T07:00:00Z","endAt":"2026-05-29T07:15:00Z"}"#
            )
        )

        #expect(try harness.jsonItems(
            await harness.responder.respond(to: harness.request(.GET, "/v1/calendar-events"))
        ).count == 2)

        let narrowed = try harness.jsonItems(
            await harness.responder.respond(to: harness.request(.GET, "/v1/calendar-events?calendar=Arbeit"))
        )
        #expect(narrowed.map { $0["calendar"] as? String } == ["Arbeit"])
    }

    /// A name with a space has to survive the query string, which means percent-decoding — the
    /// same thing the reminder route does, and the same reason.
    @Test("a percent-encoded calendar name reaches the service intact")
    func listDecodesCalendarName() async throws {
        let harness = try TestHarness(calendars: ["Family / Shared"])
        let response = await harness.responder.respond(
            to: harness.request(.GET, "/v1/calendar-events?calendar=Family%20%2F%20Shared")
        )
        // It resolved: an undecoded name would have been a 404 from the fake.
        #expect(response.status == 200)
    }

    @Test("a listing that names an unknown calendar fails closed rather than spanning all of them")
    func listUnknownCalendar() async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(
            to: harness.request(.GET, "/v1/calendar-events?calendar=Nope")
        )
        #expect(response.status == 404)
        #expect(try harness.errorCode(response) == "no_such_calendar")
    }

    @Test("a GET with a body is refused rather than silently ignored")
    func listRejectsBody() async throws {
        let harness = try TestHarness()
        let response = await harness.responder.respond(
            to: harness.request(.GET, "/v1/calendar-events", body: #"{"days":1}"#)
        )
        #expect(response.status == 400)
    }

    // MARK: - Availability

    /// Denying calendar access must not take the reminder routes down, and the 503 must name the
    /// permission that is actually missing.
    @Test("revoked calendar access is a distinct 503 that leaves reminders working")
    func calendarUnavailableIsIndependent() async throws {
        let harness = try TestHarness()
        await harness.calendar.setAvailability(.unauthorized)

        for request in [
            harness.request(.GET, "/v1/calendar-events"),
            harness.request(.POST, "/v1/calendar-events", body: createEventBody),
        ] {
            let response = await harness.responder.respond(to: request)
            #expect(response.status == 503)
            #expect(try harness.errorCode(response) == "calendar_unavailable")
            #expect(harness.header(response, "Retry-After") == "60")
        }

        // The reminder side is untouched.
        let reminders = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: createBody)
        )
        #expect(reminders.status == 201)
    }

    @Test("revoked reminders access leaves the calendar routes working")
    func remindersUnavailableIsIndependent() async throws {
        let harness = try TestHarness()
        await harness.reminders.setAvailability(.unauthorized)

        let reminders = await harness.responder.respond(
            to: harness.request(.POST, "/v1/reminders", body: createBody)
        )
        #expect(reminders.status == 503)
        #expect(try harness.errorCode(reminders) == "reminders_unavailable")

        let calendar = await harness.responder.respond(
            to: harness.request(.POST, "/v1/calendar-events", body: createEventBody)
        )
        #expect(calendar.status == 201)
    }

    // MARK: - Auth and rate limiting

    @Test("neither calendar route has an unauthenticated surface")
    func requiresAuth() async throws {
        let harness = try TestHarness()
        for request in [
            harness.request(.GET, "/v1/calendar-events", authorized: false),
            harness.request(.POST, "/v1/calendar-events", body: createEventBody, authorized: false),
        ] {
            let response = await harness.responder.respond(to: request)
            #expect(response.status == 401)
        }
        #expect(await harness.calendar.all.isEmpty)
    }

    /// A create draws on the mutation bucket, so a flood of them cannot also burn the read budget
    /// — and cannot exceed the mutation cap.
    @Test("a create is charged as a mutation")
    func createIsRateLimitedAsAMutation() async throws {
        let harness = try TestHarness(rateLimiterCapacities: (global: 30, mutation: 2))

        var statuses: [Int] = []
        for _ in 0..<3 {
            statuses.append(
                await harness.responder.respond(
                    to: harness.request(.POST, "/v1/calendar-events", body: createEventBody)
                ).status
            )
        }
        #expect(statuses == [201, 201, 429])

        // Reads still work: the mutation bucket is empty, the global one is not.
        #expect(await harness.responder.respond(to: harness.request(.GET, "/v1/calendar-events")).status == 200)
    }

    // MARK: - Auditing

    @Test("a calendar request is audited under its own operation, with the calendar name")
    func auditsCalendarOperations() async throws {
        let harness = try TestHarness()

        _ = await harness.responder.respond(
            to: harness.request(.POST, "/v1/calendar-events", body: createEventBody)
        )
        var event = try #require(harness.audit.events.first)
        #expect(event.operation == .calendarCreate)
        // Taken from the event that was written, not from the request — which names no calendar.
        #expect(event.calendar == (try calendarName("Privat")))
        #expect(event.list == nil)
        #expect(event.status == 201)

        // And a create that never wrote anything names no calendar at all: there was no choice to
        // record, on either side.
        harness.audit.reset()
        let unpinned = try TestHarness(writeCalendar: nil)
        _ = await unpinned.responder.respond(
            to: unpinned.request(.POST, "/v1/calendar-events", body: createEventBody)
        )
        let refused = try #require(unpinned.audit.events.first)
        #expect(refused.operation == .calendarCreate)
        #expect(refused.calendar == nil)
        #expect(refused.status == 503)

        harness.audit.reset()
        _ = await harness.responder.respond(to: harness.request(.GET, "/v1/calendar-events"))
        event = try #require(harness.audit.events.first)
        #expect(event.operation == .calendarList)
        // A listing names calendars in the query string, which the trace does not read — the
        // audit line records the operation, not the filter.
        #expect(event.calendar == nil)
    }

    /// The threat model's rule, at the handler level: a calendar operation logs the calendar and
    /// **nothing about the event** — not the title, not the notes, not the times.
    @Test("no event title, note or time reaches the audit log")
    func auditCarriesNoEventContent() async throws {
        let harness = try TestHarness()
        let secrets = [
            "Therapy with Dr. Meyer",
            "/Users/phil/Notes/1 Inbox/secret.md",
            harness.token,
            "\u{202E}gnirts ydeerg",
            "'; DROP TABLE reminder_map; --",
        ]

        for secret in secrets {
            harness.audit.reset()
            harness.clock.advance(by: 60)

            let body = try String(
                decoding: JSONSerialization.data(withJSONObject: [
                    "title": secret,
                    "notes": secret,
                    "startAt": "2026-05-29T09:00:00+02:00",
                    "endAt": "2026-05-29T10:00:00+02:00",
                    "timeZone": "Europe/Berlin",
                ]),
                as: UTF8.self
            )
            let response = await harness.responder.respond(
                to: harness.request(.POST, "/v1/calendar-events", body: body)
            )
            #expect(response.status == 201 || response.status == 400)

            for line in try harness.audit.lines() {
                #expect(!line.contains(secret), "audit line leaked event content")
                #expect(!line.contains("/Users/"), "audit line leaked a path")
                #expect(!line.contains("erdab_"), "audit line leaked a token")
                #expect(!line.contains("2026-05-29"), "audit line leaked an event time")
                #expect(!line.contains("Europe/Berlin"), "audit line leaked an event's time zone")
                // The calendar name is the one thing that is allowed through.
                #expect(line.contains("\"calendar\":\"Privat\""))
            }
        }
    }
}
