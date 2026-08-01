import BridgeCore
import Foundation
import NIOHTTP1
import Testing

@testable import BridgeHTTP

@Suite("Router")
struct RouterTests {
    @Test("the six routes match")
    func matchesTheTable() throws {
        #expect(try Router.route(method: .GET, uri: "/v1/status") == .status)
        #expect(try Router.route(method: .POST, uri: "/v1/reminders") == .createReminder)
        #expect(try Router.route(method: .POST, uri: "/v1/calendar-events") == .createCalendarEvent)

        guard case .listReminders = try Router.route(method: .GET, uri: "/v1/reminders") else {
            Issue.record("GET /v1/reminders should list")
            return
        }
        guard case .listCalendarEvents = try Router.route(method: .GET, uri: "/v1/calendar-events") else {
            Issue.record("GET /v1/calendar-events should list")
            return
        }

        let id = BridgeID.generate()
        #expect(try Router.route(method: .POST, uri: "/v1/reminders/\(id.rawValue)/complete")
            == .completeReminder(id))
    }

    @Test("an unknown path is a 404", arguments: [
        "/", "", "/v1", "/v1/", "/v2/status", "/status", "/v1/reminder", "/v1/status/extra",
        "/v1/reminders/complete", "/v1//status", "//v1/status",
        // Shapes the calendar API deliberately does not have: no calendar management, no
        // per-event addressing, and no `/v1/calendar/…` prefix that would imply either.
        "/v1/calendar", "/v1/calendars", "/v1/calendar/events", "/v1/calendar-event",
        "/v1/calendar-events/some-id", "/v1/calendar-events/some-id/delete",
    ])
    func unknownPathIs404(uri: String) {
        #expect(throws: ApiError.notFound) { try Router.route(method: .GET, uri: uri) }
    }

    @Test("a trailing slash is a different path, and it does not exist", arguments: [
        "/v1/status/", "/v1/reminders/", "/v1/calendar-events/",
    ])
    func trailingSlashIs404(uri: String) {
        #expect(throws: ApiError.notFound) { try Router.route(method: .GET, uri: uri) }
    }

    /// The path is never percent-decoded, so an encoded traversal never becomes `..` and simply
    /// fails to match anything.
    @Test("traversal attempts are 404, not normalised", arguments: [
        "/v1/../v1/status",
        "/v1/%2e%2e/v1/status",
        "/%2e%2e/%2e%2e/etc/passwd",
        "/v1/status/../status",
        "/v1/reminders/%2e%2e/complete",
        "/%76%31/status",
        "/v1/status%00",
        "/v1/status#fragment",
    ])
    func traversalIs404(uri: String) {
        #expect(throws: ApiError.notFound) { try Router.route(method: .GET, uri: uri) }
    }

    @Test("a known path with the wrong method is a 405 that says what is allowed")
    func wrongMethodIs405() throws {
        for (method, uri, allow) in [
            (HTTPMethod.POST, "/v1/status", "GET"),
            (HTTPMethod.DELETE, "/v1/status", "GET"),
            (HTTPMethod.PUT, "/v1/reminders", "GET, POST"),
            (HTTPMethod.DELETE, "/v1/reminders", "GET, POST"),
            // No PUT and no DELETE on events either: there is no edit and no delete, and the
            // `Allow` header says so rather than leaving a client to guess.
            (HTTPMethod.PUT, "/v1/calendar-events", "GET, POST"),
            (HTTPMethod.DELETE, "/v1/calendar-events", "GET, POST"),
            (HTTPMethod.PATCH, "/v1/calendar-events", "GET, POST"),
        ] {
            do {
                _ = try Router.route(method: method, uri: uri)
                Issue.record("\(method) \(uri) should not route")
            } catch let failure as HTTPFailure {
                #expect(failure.apiError == .methodNotAllowed)
                #expect(failure.extraHeaders.first?.name == "Allow")
                #expect(failure.extraHeaders.first?.value == allow)
            }
        }
    }

    @Test("the complete route only accepts POST")
    func completeOnlyAcceptsPost() throws {
        let uri = "/v1/reminders/\(BridgeID.generate().rawValue)/complete"
        do {
            _ = try Router.route(method: .GET, uri: uri)
            Issue.record("GET should not route")
        } catch let failure as HTTPFailure {
            #expect(failure.apiError == .methodNotAllowed)
            #expect(failure.extraHeaders.first?.value == "POST")
        }
    }

    /// A malformed id is a 404 rather than a 400: a 400 would confirm that the route exists and
    /// only the argument was wrong.
    @Test("a malformed reminder id is a 404", arguments: [
        "not-an-id", "rem_", "rem_nope", "REM_6f0c1b6e-1f4a-4a9d-9f3e-1b2c3d4e5f60", "..",
    ])
    func malformedIdIs404(segment: String) {
        #expect(throws: ApiError.notFound) {
            try Router.route(method: .POST, uri: "/v1/reminders/\(segment)/complete")
        }
    }

    @Test("an uppercase uuid in the path is accepted and normalised")
    func normalisesIdCase() throws {
        let route = try Router.route(
            method: .POST,
            uri: "/v1/reminders/rem_6F0C1B6E-1F4A-4A9D-9F3E-1B2C3D4E5F60/complete"
        )
        #expect(route == .completeReminder(
            try #require(BridgeID(rawValue: "rem_6f0c1b6e-1f4a-4a9d-9f3e-1b2c3d4e5f60"))
        ))
    }

    // MARK: - Query strings

    @Test("query parameters are parsed")
    func parsesQuery() throws {
        guard case .listReminders(let query) = try Router.route(
            method: .GET,
            uri: "/v1/reminders?list=Groceries&list=Work&limit=50"
        ) else {
            Issue.record("should list")
            return
        }
        #expect(query.lists.map(\.rawValue) == ["Groceries", "Work"])
        #expect(query.limit == 50)
    }

    /// Lists are addressed by their real name now, and real names have spaces, umlauts and emoji
    /// in them — so a `list` value is percent-decoded, unlike the path, which never is.
    @Test("a list name is percent-decoded", arguments: [
        ("/v1/reminders?list=To%20Do", "To Do"),
        ("/v1/reminders?list=Eink%C3%A4ufe", "Einkäufe"),
        ("/v1/reminders?list=Work%20%2F%20Admin", "Work / Admin"),
        ("/v1/reminders?list=%F0%9F%A7%BE", "🧾"),
        ("/v1/reminders?list=Groceries", "Groceries"),
    ])
    func decodesListName(uri: String, expected: String) throws {
        guard case .listReminders(let query) = try Router.route(method: .GET, uri: uri) else {
            Issue.record("should list")
            return
        }
        #expect(query.lists.map(\.rawValue) == [expected])
    }

    /// `+` is a form-encoding convention, not RFC 3986. A list genuinely called "a+b" must not
    /// silently become "a b".
    @Test("a plus is a plus, not a space")
    func plusIsNotASpace() throws {
        guard case .listReminders(let query) = try Router.route(
            method: .GET,
            uri: "/v1/reminders?list=a%2Bb"
        ) else {
            Issue.record("should list")
            return
        }
        #expect(query.lists.map(\.rawValue) == ["a+b"])
    }

    @Test("an empty query is the default query")
    func emptyQuery() throws {
        guard case .listReminders(let query) = try Router.route(method: .GET, uri: "/v1/reminders?") else {
            Issue.record("should list")
            return
        }
        #expect(query.lists.isEmpty)
        #expect(query.limit == Limits.listLimitDefault)
    }

    @Test("a bad query is a 400, matching the JSON decoder's strictness", arguments: [
        "/v1/reminders?unknown=1",
        "/v1/reminders?alias=Groceries",          // the old parameter name is gone, not tolerated
        "/v1/reminders?list=",                    // an empty name resolves nothing
        "/v1/reminders?list=%20%20",              // …and neither does whitespace
        "/v1/reminders?list=Groceries&list=Groceries",
        "/v1/reminders?list=%zz",                 // malformed escape
        "/v1/reminders?list=%2",                  // truncated escape
        "/v1/reminders?list=%",
        "/v1/reminders?list=%C3%28",              // invalid UTF-8
        "/v1/reminders?list=%00",                 // a NUL, decoded and then refused
        "/v1/reminders?list=a%0Ab",               // a newline, likewise
        "/v1/reminders?limit=0",
        "/v1/reminders?limit=201",
        "/v1/reminders?limit=abc",
        "/v1/reminders?limit=+5",
        "/v1/reminders?limit=05",
        "/v1/reminders?list",
        "/v1/reminders?&list=Groceries",
    ])
    func badQueryIs400(uri: String) {
        #expect(throws: ApiError.invalidRequest) { try Router.route(method: .GET, uri: uri) }
    }

    /// Decoding is confined to query values. A traversal in the path still fails to match a route
    /// rather than being normalised into one.
    @Test("percent-decoding a query value does not leak into the path")
    func decodingStaysInTheQuery() {
        #expect(throws: ApiError.notFound) {
            try Router.route(method: .GET, uri: "/v1/%72eminders?list=Groceries")
        }
    }

    @Test("the query only affects the list route")
    func queryOnStatusIsIgnoredByPathMatching() throws {
        #expect(try Router.route(method: .GET, uri: "/v1/status?anything=1") == .status)
    }

    @Test("routes carry the right audit operation and rate-limit class")
    func classifiesRoutes() throws {
        #expect(Route.status.auditOperation == .statusRead)
        #expect(Route.status.rateLimitClass == .read)
        #expect(try Route.listReminders(ListRemindersQuery()).rateLimitClass == .read)
        #expect(try Route.listReminders(ListRemindersQuery()).auditOperation == .remindersList)
        #expect(Route.createReminder.auditOperation == .remindersCreate)
        #expect(Route.createReminder.rateLimitClass == .mutation)
        #expect(Route.completeReminder(BridgeID.generate()).rateLimitClass == .mutation)

        #expect(try Route.listCalendarEvents(ListCalendarEventsQuery()).auditOperation == .calendarList)
        #expect(try Route.listCalendarEvents(ListCalendarEventsQuery()).rateLimitClass == .read)
        #expect(Route.createCalendarEvent.auditOperation == .calendarCreate)
        #expect(Route.createCalendarEvent.rateLimitClass == .mutation)
    }

    // MARK: - The calendar query string

    @Test("calendar query parameters are parsed")
    func parsesCalendarQuery() throws {
        guard case .listCalendarEvents(let query) = try Router.route(
            method: .GET,
            uri: "/v1/calendar-events?calendar=Privat&calendar=Arbeit&days=14&limit=25"
        ) else {
            Issue.record("should list")
            return
        }
        #expect(query.calendars.map(\.rawValue) == ["Privat", "Arbeit"])
        #expect(query.days == 14)
        #expect(query.limit == 25)
    }

    @Test("an empty calendar query is the default query")
    func emptyCalendarQuery() throws {
        for uri in ["/v1/calendar-events", "/v1/calendar-events?"] {
            guard case .listCalendarEvents(let query) = try Router.route(method: .GET, uri: uri) else {
                Issue.record("should list")
                return
            }
            #expect(query.calendars.isEmpty)
            #expect(query.days == Limits.eventWindowDefaultDays)
            #expect(query.limit == Limits.eventLimitDefault)
        }
    }

    @Test("a calendar name is percent-decoded", arguments: [
        ("/v1/calendar-events?calendar=Family%20%2F%20Shared", "Family / Shared"),
        ("/v1/calendar-events?calendar=Geburtstage", "Geburtstage"),
        ("/v1/calendar-events?calendar=Caf%C3%A9%20%E2%98%95%EF%B8%8F", "Café ☕️"),
        ("/v1/calendar-events?calendar=a%2Bb", "a+b"),
    ])
    func decodesCalendarName(uri: String, expected: String) throws {
        guard case .listCalendarEvents(let query) = try Router.route(method: .GET, uri: uri) else {
            Issue.record("should list")
            return
        }
        #expect(query.calendars.map(\.rawValue) == [expected])
    }

    @Test("a bad calendar query is a 400, matching the reminder route's strictness", arguments: [
        "/v1/calendar-events?unknown=1",
        "/v1/calendar-events?list=Privat",          // the reminder parameter is not tolerated here
        "/v1/calendar-events?calendar=",
        "/v1/calendar-events?calendar=%20%20",
        "/v1/calendar-events?calendar=Privat&calendar=Privat",
        "/v1/calendar-events?calendar=%zz",
        "/v1/calendar-events?calendar=%00",
        "/v1/calendar-events?calendar=a%0Ab",
        "/v1/calendar-events?days=0",
        "/v1/calendar-events?days=32",
        "/v1/calendar-events?days=-1",
        "/v1/calendar-events?days=07",
        "/v1/calendar-events?days=abc",
        "/v1/calendar-events?limit=0",
        "/v1/calendar-events?limit=201",
        "/v1/calendar-events?limit=+5",
        "/v1/calendar-events?calendar",
        "/v1/calendar-events?&calendar=Privat",
    ])
    func badCalendarQueryIs400(uri: String) {
        #expect(throws: ApiError.invalidRequest) { try Router.route(method: .GET, uri: uri) }
    }
}
