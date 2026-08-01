import BridgeCore
import Foundation
import NIOHTTP1
import Testing

@testable import BridgeHTTP

@Suite("Router")
struct RouterTests {
    @Test("the four routes match")
    func matchesTheTable() throws {
        #expect(try Router.route(method: .GET, uri: "/v1/status") == .status)
        #expect(try Router.route(method: .POST, uri: "/v1/reminders") == .createReminder)

        guard case .listReminders = try Router.route(method: .GET, uri: "/v1/reminders") else {
            Issue.record("GET /v1/reminders should list")
            return
        }

        let id = BridgeID.generate()
        #expect(try Router.route(method: .POST, uri: "/v1/reminders/\(id.rawValue)/complete")
            == .completeReminder(id))
    }

    @Test("an unknown path is a 404", arguments: [
        "/", "", "/v1", "/v1/", "/v2/status", "/status", "/v1/reminder", "/v1/status/extra",
        "/v1/reminders/complete", "/v1//status", "//v1/status",
    ])
    func unknownPathIs404(uri: String) {
        #expect(throws: ApiError.notFound) { try Router.route(method: .GET, uri: uri) }
    }

    @Test("a trailing slash is a different path, and it does not exist", arguments: [
        "/v1/status/", "/v1/reminders/",
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
            uri: "/v1/reminders?alias=inbox&alias=work&limit=50"
        ) else {
            Issue.record("should list")
            return
        }
        #expect(query.aliases.map(\.rawValue) == ["inbox", "work"])
        #expect(query.limit == 50)
    }

    @Test("an empty query is the default query")
    func emptyQuery() throws {
        guard case .listReminders(let query) = try Router.route(method: .GET, uri: "/v1/reminders?") else {
            Issue.record("should list")
            return
        }
        #expect(query.aliases.isEmpty)
        #expect(query.limit == Limits.listLimitDefault)
    }

    @Test("a bad query is a 400, matching the JSON decoder's strictness", arguments: [
        "/v1/reminders?unknown=1",
        "/v1/reminders?alias=INBOX",
        "/v1/reminders?alias=in%20box",
        "/v1/reminders?alias=inbox&alias=inbox",
        "/v1/reminders?limit=0",
        "/v1/reminders?limit=201",
        "/v1/reminders?limit=abc",
        "/v1/reminders?limit=+5",
        "/v1/reminders?limit=05",
        "/v1/reminders?alias",
        "/v1/reminders?&alias=inbox",
    ])
    func badQueryIs400(uri: String) {
        #expect(throws: ApiError.invalidRequest) { try Router.route(method: .GET, uri: uri) }
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
        #expect(Route.createReminder.auditOperation == .remindersCreate)
        #expect(Route.createReminder.rateLimitClass == .mutation)
        #expect(Route.completeReminder(BridgeID.generate()).rateLimitClass == .mutation)
    }
}
