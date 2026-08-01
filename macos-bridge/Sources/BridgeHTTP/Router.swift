import BridgeCore
import Foundation
import NIOHTTP1

/// The four things this API can do. Nothing else exists — in particular there is no route that
/// touches the token, permissions or config, because those must not be reachable from the network
/// at all.
public enum Route: Sendable, Equatable {
    case status
    case listReminders(ListRemindersQuery)
    case createReminder
    case completeReminder(BridgeID)

    var auditOperation: AuditOperation {
        switch self {
        case .status: .statusRead
        case .listReminders: .remindersList
        case .createReminder: .remindersCreate
        case .completeReminder: .remindersComplete
        }
    }

    /// Mutations draw on the tighter rate-limit bucket.
    var rateLimitClass: RateLimitClass {
        switch self {
        case .status, .listReminders: .read
        case .createReminder, .completeReminder: .mutation
        }
    }
}

/// A four-entry table matched by a hand-written path splitter.
///
/// No regex anywhere: no ReDoS surface, and no arguments about what a pattern does with an
/// unusual encoding. And **the path is never percent-decoded** — decoding is what turns
/// `%2e%2e` into `..`, so a traversal attempt simply fails to match any entry and is a 404
/// like any other unknown path.
public enum Router {
    /// Raised instead of a bare `ApiError` so the 405 can carry its `Allow` header.
    static func route(method: HTTPMethod, uri: String) throws -> Route {
        let (path, query) = Self.split(uri: uri)
        let components = path.split(separator: "/", omittingEmptySubsequences: false)

        // `/v1/status`
        if components.count == 3, components[0].isEmpty, components[1] == "v1", components[2] == "status" {
            guard method == .GET else { throw HTTPFailure.methodNotAllowed(allow: ["GET"]) }
            return .status
        }

        // `/v1/reminders`
        if components.count == 3, components[0].isEmpty, components[1] == "v1", components[2] == "reminders" {
            switch method {
            case .GET: return .listReminders(try Self.listQuery(from: query))
            case .POST: return .createReminder
            default: throw HTTPFailure.methodNotAllowed(allow: ["GET", "POST"])
            }
        }

        // `/v1/reminders/{id}/complete`
        if components.count == 5, components[0].isEmpty, components[1] == "v1",
           components[2] == "reminders", components[4] == "complete" {
            // An id that does not parse is a path that does not exist. Reporting 400 here would
            // tell a caller that it had found a real route with a bad argument; 404 tells it
            // nothing it did not already know.
            guard let id = BridgeID(rawValue: String(components[3])) else { throw ApiError.notFound }
            guard method == .POST else { throw HTTPFailure.methodNotAllowed(allow: ["POST"]) }
            return .completeReminder(id)
        }

        throw ApiError.notFound
    }

    /// Splits the request target into path and raw query. A trailing slash is left in the path,
    /// where it produces an extra empty component and therefore a 404 — this API is strict about
    /// having exactly one spelling per resource.
    static func split(uri: String) -> (path: String, query: Substring) {
        guard let separator = uri.firstIndex(of: "?") else { return (uri, "") }
        return (String(uri[uri.startIndex..<separator]), uri[uri.index(after: separator)...])
    }

    /// `?list=Groceries&list=Einkaufsliste&limit=50`, parsed strictly: an unknown parameter is a
    /// 400, the same posture the JSON decoder takes with an unknown key. Repeating `list` narrows
    /// to those lists; omitting it means every reminder list on the Mac.
    ///
    /// A `list` value **is** percent-decoded — real list names hold spaces, umlauts and emoji, so
    /// there is no way around it — and only after decoding does `ListName` get to reject it. A
    /// malformed escape is a 400, not a best guess. `limit` is left raw: it is digits, and
    /// anything with a `%` in it fails to parse as an integer, which is the right answer anyway.
    static func listQuery(from query: Substring) throws -> ListRemindersQuery {
        guard !query.isEmpty else { return try ListRemindersQuery() }

        var lists: [ListName] = []
        var limit = Limits.listLimitDefault

        for pair in query.split(separator: "&", omittingEmptySubsequences: false) {
            guard !pair.isEmpty else { throw ApiError.invalidRequest }
            guard let separator = pair.firstIndex(of: "=") else { throw ApiError.invalidRequest }
            let name = pair[pair.startIndex..<separator]
            let value = pair[pair.index(after: separator)...]

            switch name {
            case "list":
                guard let decoded = PercentDecoding.decode(value),
                      let list = ListName(rawValue: decoded)
                else { throw ApiError.invalidRequest }
                guard !lists.contains(list) else { throw ApiError.invalidRequest }
                lists.append(list)
            case "limit":
                guard let parsed = Int(value), String(value) == String(parsed) else {
                    throw ApiError.invalidRequest
                }
                limit = parsed
            default:
                throw ApiError.invalidRequest
            }
        }

        return try ListRemindersQuery(lists: lists, limit: limit)
    }
}
