import BridgeCore
import Foundation
import Testing

@testable import BridgeEventKit

@Suite("EventKit error mapping")
struct ErrorMappingTests {
    /// The single most important mapping in the module: a revoked grant must answer 503, and a
    /// 500 here would look like a bug in the bridge instead of a permission the user pulled.
    ///
    /// Which 503 depends on the entity, and it matters: `EKErrorEventStoreNotAuthorized` is one
    /// code for both, but "grant Reminders access" and "grant Calendar access" are two different
    /// rows in System Settings.
    @Test("a revoked grant is 503, and names the permission that was actually revoked")
    func revocationIs503() {
        let error = EKErrorFixture.error(EKErrorFixture.eventStoreNotAuthorized)

        let reminders = EventKitErrorMapping.apiError(for: error, entity: .reminder)
        #expect(reminders == .remindersUnavailable)
        #expect(reminders.httpStatus == 503)

        let calendar = EventKitErrorMapping.apiError(for: error, entity: .event)
        #expect(calendar == .calendarUnavailable)
        #expect(calendar.httpStatus == 503)
    }

    @Test("a list that exists but cannot take the reminder is a 409", arguments: [
        EKErrorFixture.calendarReadOnly,
        EKErrorFixture.calendarIsImmutable,
        EKErrorFixture.calendarDoesNotAllowReminders,
        EKErrorFixture.sourceDoesNotAllowReminders,
    ])
    func unusableCalendarIsReadOnly(code: Int) {
        let mapped = EventKitErrorMapping.apiError(for: EKErrorFixture.error(code), entity: .reminder)
        #expect(mapped == .listReadOnly)
        #expect(mapped.httpStatus == 409)
    }

    /// The event-side counterpart: a subscribed or holiday calendar, or an account that holds no
    /// events. The two shared codes (`calendarReadOnly`, `calendarIsImmutable`) must come out as
    /// the *calendar* 409 here, not the list one.
    @Test("a calendar that exists but cannot take the event is a 409", arguments: [
        EKErrorFixture.calendarReadOnly,
        EKErrorFixture.calendarIsImmutable,
        EKErrorFixture.calendarDoesNotAllowEvents,
        EKErrorFixture.sourceDoesNotAllowEvents,
    ])
    func unusableCalendarIsCalendarReadOnly(code: Int) {
        let mapped = EventKitErrorMapping.apiError(for: EKErrorFixture.error(code), entity: .event)
        #expect(mapped == .calendarReadOnly)
        #expect(mapped.httpStatus == 409)
    }

    /// A reminders-only code seen on the event path is not silently reinterpreted as its calendar
    /// sibling — EventKit does not emit it there, and inventing a mapping would hide a real bug.
    @Test("a reminders-only code stays a reminders answer whatever the entity")
    func remindersOnlyCodesAreNotRemapped() {
        for code in [EKErrorFixture.calendarDoesNotAllowReminders, EKErrorFixture.sourceDoesNotAllowReminders] {
            #expect(EventKitErrorMapping.apiError(for: EKErrorFixture.error(code), entity: .event) == .listReadOnly)
        }
    }

    /// The resolution the handler did has come apart underneath it — the same thing, from the
    /// caller's side, as a name that matched nothing.
    @Test("an item with no calendar at all is the entity's own not-found")
    func noCalendarIsNotFound() {
        let error = EKErrorFixture.error(EKErrorFixture.noCalendar)
        #expect(EventKitErrorMapping.apiError(for: error, entity: .reminder) == .noSuchList)
        #expect(EventKitErrorMapping.apiError(for: error, entity: .event) == .noSuchCalendar)
        #expect(ApiError.noSuchCalendar.httpStatus == 404)
    }

    @Test("EventKit rejecting the request's own content is a 400", arguments: [
        EKErrorFixture.priorityIsInvalid,
        EKErrorFixture.noStartDate,
        EKErrorFixture.noEndDate,
        EKErrorFixture.datesInverted,
        EKErrorFixture.recurringReminderRequiresDueDate,
        EKErrorFixture.startDateTooFarInFuture,
    ])
    func contentRejectionIsInvalidRequest(code: Int) {
        #expect(EventKitErrorMapping.apiError(for: EKErrorFixture.error(code), entity: .reminder) == .invalidRequest)
        #expect(EventKitErrorMapping.apiError(for: EKErrorFixture.error(code), entity: .event) == .invalidRequest)
    }

    @Test("anything else collapses to a bare 500")
    func unknownCollapses() {
        #expect(
            EventKitErrorMapping.apiError(for: EKErrorFixture.error(EKErrorFixture.internalFailure), entity: .reminder)
                == .internal
        )
        // A code this SDK does not define at all.
        #expect(EventKitErrorMapping.apiError(for: EKErrorFixture.error(9_999), entity: .event) == .internal)
        // `EKErrorInvalidSpan` is real but is not something this bridge can provoke — it saves
        // with `.thisEvent` and nothing else — so it must not have a bespoke mapping.
        #expect(
            EventKitErrorMapping.apiError(for: EKErrorFixture.error(EKErrorFixture.invalidSpan), entity: .event)
                == .internal
        )
    }

    @Test("a foreign error domain is never interpreted as an EventKit code")
    func foreignDomain() {
        // `NSPOSIXErrorDomain` code 29 is not `EKErrorEventStoreNotAuthorized`, and reading it as
        // one would turn an unrelated failure into a 503 the client would retry forever.
        let posix = NSError(domain: NSPOSIXErrorDomain, code: EKErrorFixture.eventStoreNotAuthorized)
        #expect(EventKitErrorMapping.apiError(for: posix, entity: .reminder) == .internal)
        #expect(EventKitErrorMapping.apiError(for: posix, entity: .event) == .internal)
        #expect(EventKitErrorMapping.apiError(for: NSError(domain: "com.example", code: 6), entity: .reminder) == .internal)
    }

    @Test("errors this module raised itself keep their meaning")
    func passesThroughApiErrors() {
        for error in ApiError.allCases {
            #expect(EventKitErrorMapping.apiError(for: error, entity: .reminder) == error)
            #expect(EventKitErrorMapping.apiError(for: error, entity: .event) == error)
        }
    }

    @Test("a cancelled task is not reported as a permission problem")
    func cancellationIsNotUnavailable() {
        #expect(EventKitErrorMapping.apiError(for: CancellationError(), entity: .reminder) == .internal)
        #expect(EventKitErrorMapping.apiError(for: CancellationError(), entity: .event) == .internal)
    }

    /// The error body has no `message` field at all, so this is belt and braces — but the mapper
    /// is the last place an `NSError` exists, and it must not start smuggling one out.
    @Test("nothing from the NSError's userInfo survives")
    func dropsNSErrorDetail() throws {
        let mapped = EventKitErrorMapping.apiError(
            for: EKErrorFixture.error(EKErrorFixture.calendarReadOnly),
            entity: .reminder
        )
        let encoded = try ResponseJSON.encode(ApiErrorResponse(error: mapped, requestId: "r"))
        let text = String(decoding: encoded, as: UTF8.self)

        #expect(!text.contains("/Users/"))
        #expect(!text.contains("couldn"))
        // Key order is not asserted — the response body is JSON, not a byte string — but the set
        // of keys is: two, neither of which can carry an `NSError` description.
        let fields = try #require(
            try JSONSerialization.jsonObject(with: encoded) as? [String: String]
        )
        #expect(fields == ["error": "list_read_only", "requestId": "r"])
    }
}
