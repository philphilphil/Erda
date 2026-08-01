import BridgeCore
import Foundation
import Testing

@testable import BridgeEventKit

@Suite("EventKit error mapping")
struct ErrorMappingTests {
    /// The single most important mapping in the module: a revoked grant must answer 503, and a
    /// 500 here would look like a bug in the bridge instead of a permission the user pulled.
    @Test("a revoked grant is 503, not 500")
    func revocationIs503() {
        let mapped = EventKitErrorMapping.apiError(
            for: EKErrorFixture.error(EKErrorFixture.eventStoreNotAuthorized)
        )
        #expect(mapped == .remindersUnavailable)
        #expect(mapped.httpStatus == 503)
    }

    @Test("a list that cannot take the reminder is a 409 on the alias", arguments: [
        EKErrorFixture.calendarReadOnly,
        EKErrorFixture.calendarIsImmutable,
        EKErrorFixture.calendarDoesNotAllowReminders,
        EKErrorFixture.sourceDoesNotAllowReminders,
        EKErrorFixture.noCalendar,
    ])
    func unusableCalendarIsAliasBroken(code: Int) {
        let mapped = EventKitErrorMapping.apiError(for: EKErrorFixture.error(code))
        #expect(mapped == .aliasBroken)
        #expect(mapped.httpStatus == 409)
    }

    @Test("EventKit rejecting the request's own content is a 400", arguments: [
        EKErrorFixture.priorityIsInvalid,
        EKErrorFixture.noStartDate,
        EKErrorFixture.datesInverted,
        EKErrorFixture.recurringReminderRequiresDueDate,
        EKErrorFixture.startDateTooFarInFuture,
    ])
    func contentRejectionIsInvalidRequest(code: Int) {
        #expect(EventKitErrorMapping.apiError(for: EKErrorFixture.error(code)) == .invalidRequest)
    }

    @Test("anything else collapses to a bare 500")
    func unknownCollapses() {
        #expect(EventKitErrorMapping.apiError(for: EKErrorFixture.error(EKErrorFixture.internalFailure)) == .internal)
        // A code this SDK does not define at all.
        #expect(EventKitErrorMapping.apiError(for: EKErrorFixture.error(9_999)) == .internal)
    }

    @Test("a foreign error domain is never interpreted as an EventKit code")
    func foreignDomain() {
        // `NSPOSIXErrorDomain` code 29 is not `EKErrorEventStoreNotAuthorized`, and reading it as
        // one would turn an unrelated failure into a 503 the client would retry forever.
        let posix = NSError(domain: NSPOSIXErrorDomain, code: EKErrorFixture.eventStoreNotAuthorized)
        #expect(EventKitErrorMapping.apiError(for: posix) == .internal)
        #expect(EventKitErrorMapping.apiError(for: NSError(domain: "com.example", code: 6)) == .internal)
    }

    @Test("errors this module raised itself keep their meaning")
    func passesThroughApiErrors() {
        for error in ApiError.allCases {
            #expect(EventKitErrorMapping.apiError(for: error) == error)
        }
    }

    @Test("a cancelled task is not reported as a permission problem")
    func cancellationIsNotUnavailable() {
        #expect(EventKitErrorMapping.apiError(for: CancellationError()) == .internal)
    }

    /// The error body has no `message` field at all, so this is belt and braces — but the mapper
    /// is the last place an `NSError` exists, and it must not start smuggling one out.
    @Test("nothing from the NSError's userInfo survives")
    func dropsNSErrorDetail() throws {
        let mapped = EventKitErrorMapping.apiError(for: EKErrorFixture.error(EKErrorFixture.calendarReadOnly))
        let encoded = try ResponseJSON.encode(ApiErrorResponse(error: mapped, requestId: "r"))
        let text = String(decoding: encoded, as: UTF8.self)

        #expect(!text.contains("/Users/"))
        #expect(!text.contains("couldn"))
        // Key order is not asserted — the response body is JSON, not a byte string — but the set
        // of keys is: two, neither of which can carry an `NSError` description.
        let fields = try #require(
            try JSONSerialization.jsonObject(with: encoded) as? [String: String]
        )
        #expect(fields == ["error": "alias_broken", "requestId": "r"])
    }
}
