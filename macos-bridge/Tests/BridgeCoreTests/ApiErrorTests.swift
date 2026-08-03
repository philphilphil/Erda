import Foundation
import Testing

@testable import BridgeCore

/// The whole table, spelled out. If a code is added or its status changed, this is the thing that
/// has to be edited deliberately.
let apiErrorStatusTable: [(ApiError, Int)] = [
    (.invalidRequest, 400),
    (.unauthorized, 401),
    (.notFound, 404),
    (.noSuchList, 404),
    (.noSuchCalendar, 404),
    (.methodNotAllowed, 405),
    (.idempotencyKeyReuse, 409),
    (.requestInProgress, 409),
    (.listReadOnly, 409),
    (.ambiguousCalendar, 409),
    (.calendarReadOnly, 409),
    (.payloadTooLarge, 413),
    (.unsupportedMediaType, 415),
    (.rateLimited, 429),
    (.internal, 500),
    (.remindersUnavailable, 503),
    (.listNotConfigured, 503),
    (.calendarUnavailable, 503),
    (.calendarNotConfigured, 503),
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
        // Authorization is the only thing left that can make the back end unusable — the
        // allowlist that used to supply a second reason is gone.
        #expect(ReminderAvailability.allCases.count == 2)
    }

    /// The calendar counterpart, and a **different** 503. macOS authorizes events and reminders
    /// separately, so telling Phil to check the wrong one is a wasted trip to System Settings.
    @Test("calendar availability maps to its own 503, not the reminders one")
    func calendarAvailabilityMapsToItsOwnError() {
        #expect(CalendarAvailability.ok.apiError == nil)
        #expect(CalendarAvailability.unauthorized.apiError == .calendarUnavailable)
        #expect(CalendarAvailability.unauthorized.apiError != ReminderAvailability.unauthorized.apiError)
        #expect(CalendarAvailability.allCases.count == 2)
    }

    /// A name matching nothing and a name matching two are the *same* answer for a list and
    /// **different** answers for a calendar. That divergence is deliberate — Erda relays the
    /// reason verbatim and the two fixes differ — so it is pinned here rather than left to be
    /// "tidied up" later.
    @Test("calendars distinguish missing from ambiguous; lists deliberately do not")
    func calendarSplitsWhatListFolds() {
        #expect(ApiError.noSuchCalendar != ApiError.ambiguousCalendar)
        #expect(ApiError.noSuchCalendar.httpStatus == 404)
        #expect(ApiError.ambiguousCalendar.httpStatus == 409)
        // The list side has no ambiguity code at all: both outcomes are `no_such_list`.
        #expect(!ApiError.allCases.contains { $0.code == "ambiguous_list" })
    }

    /// "Grant Calendar access in System Settings" and "pick a calendar in the ErdaBridge window"
    /// are different errands. They share a status — both are "the Mac cannot serve this right now"
    /// — but they must never share a code, because the code is all Erda has to tell Phil which
    /// errand to run.
    @Test("an unconfigured write calendar is its own 503, distinct from a missing grant")
    func notConfiguredIsDistinctFromUnavailable() {
        #expect(ApiError.calendarNotConfigured != ApiError.calendarUnavailable)
        #expect(ApiError.calendarNotConfigured.httpStatus == 503)
        #expect(ApiError.calendarNotConfigured.code == "calendar_not_configured")
    }

    /// The reminder mirror: "grant Reminders access in System Settings" and "pick a list in the
    /// ErdaBridge window" are different errands. They share a 503, but never a code — and the write
    /// list's "not configured" is its own thing, distinct from the calendar's.
    @Test("an unconfigured write list is its own 503, distinct from a missing grant and from the calendar's")
    func listNotConfiguredIsDistinct() {
        #expect(ApiError.listNotConfigured != ApiError.remindersUnavailable)
        #expect(ApiError.listNotConfigured != ApiError.calendarNotConfigured)
        #expect(ApiError.listNotConfigured.httpStatus == 503)
        #expect(ApiError.listNotConfigured.code == "list_not_configured")
    }
}
