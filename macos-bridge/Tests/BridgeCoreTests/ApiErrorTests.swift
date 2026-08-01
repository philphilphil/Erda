import Foundation
import Testing

@testable import BridgeCore

/// The whole table, spelled out. If a code is added or its status changed, this is the thing that
/// has to be edited deliberately.
let apiErrorStatusTable: [(ApiError, Int)] = [
    (.invalidRequest, 400),
    (.aliasUnknown, 400),
    (.unauthorized, 401),
    (.notFound, 404),
    (.methodNotAllowed, 405),
    (.idempotencyKeyReuse, 409),
    (.requestInProgress, 409),
    (.aliasBroken, 409),
    (.payloadTooLarge, 413),
    (.unsupportedMediaType, 415),
    (.rateLimited, 429),
    (.internal, 500),
    (.remindersUnavailable, 503),
    (.unsupportedHttpVersion, 505),
]

@Suite("Error mapping")
struct ApiErrorTests {
    @Test("every code maps to its intended status", arguments: apiErrorStatusTable)
    func mapsToStatus(error: ApiError, status: Int) {
        #expect(error.httpStatus == status)
    }

    @Test("the table covers the entire closed set")
    func tableIsExhaustive() {
        #expect(Set(apiErrorStatusTable.map(\.0)) == Set(ApiError.allCases))
        #expect(apiErrorStatusTable.count == ApiError.allCases.count)
    }

    @Test("codes are stable snake_case and unique")
    func codesAreStable() {
        let codes = ApiError.allCases.map(\.code)
        #expect(Set(codes).count == codes.count)
        for code in codes {
            #expect(code.allSatisfy { $0.isLowercase || $0 == "_" }, "\(code) is not snake_case")
        }
    }

    @Test("the wire body carries only a code and a request id — never a message")
    func responseHasNoMessageField() throws {
        let response = ApiErrorResponse(error: .remindersUnavailable, requestId: "6f0c1b6e")
        let data = try ResponseJSON.encode(response)

        let parsed = try #require(
            try JSONSerialization.jsonObject(with: data) as? [String: Any]
        )
        #expect(Set(parsed.keys) == ["error", "requestId"])
        #expect(parsed["error"] as? String == "reminders_unavailable")

        // The type itself has no third field to fill, so nothing at the call site can add one.
        #expect(Mirror(reflecting: response).children.compactMap(\.label) == ["error", "requestId"])
    }

    @Test("no status is ambiguous: a code always means one thing")
    func codeToStatusIsAFunction() {
        var seen: [ApiError: Int] = [:]
        for error in ApiError.allCases {
            let status = error.httpStatus
            #expect(seen[error] == nil || seen[error] == status)
            seen[error] = status
            #expect((400...599).contains(status), "\(error.code) mapped outside the error range")
        }
    }

    @Test("availability collapses to a single client-visible outcome")
    func availabilityMapsToOneError() {
        #expect(ReminderAvailability.ok.apiError == nil)
        #expect(ReminderAvailability.unauthorized.apiError == .remindersUnavailable)
        #expect(ReminderAvailability.noAllowlist.apiError == .remindersUnavailable)
    }
}
