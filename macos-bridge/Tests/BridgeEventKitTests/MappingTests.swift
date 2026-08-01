import BridgeCore
import Foundation
import Testing

@testable import BridgeEventKit

@Suite("Reminder mapping")
struct MappingTests {
    private let due = Date(timeIntervalSince1970: 1_785_940_200)

    @Test("a raw reminder becomes a snapshot carrying only bridge-side identifiers")
    func mapsToSnapshot() throws {
        let raw = RawReminder(
            itemId: "x-apple-reminderkit://REMCDReminder/ABCDEF",
            externalId: "ext-1",
            calendarId: "cal-inbox",
            title: "Buy milk",
            notes: "oat, not soy",
            dueAt: due,
            priority: 5
        )
        let id = BridgeID.generate()
        let snapshot = raw.snapshot(id: id, alias: try alias("inbox"))

        #expect(snapshot.id == id)
        #expect(snapshot.alias == (try alias("inbox")))
        #expect(snapshot.title == "Buy milk")
        #expect(snapshot.notes == "oat, not soy")
        #expect(snapshot.dueAt == due)
        #expect(snapshot.priority == 5)
        #expect(snapshot.isCompleted == false)
        #expect(snapshot.completedAt == nil)
    }

    /// The wire format has no field that could carry an EventKit identifier, and this is the
    /// mapping that would have to leak one for it to happen.
    @Test("no EventKit identifier survives the mapping")
    func dropsEventKitIdentifiers() throws {
        let raw = RawReminder(
            itemId: "x-apple-reminderkit://REMCDReminder/SECRET",
            externalId: "external-SECRET",
            calendarId: "cal-SECRET",
            title: "t"
        )
        let encoded = try ResponseJSON.encode(raw.snapshot(id: .generate(), alias: try alias("inbox")))
        let text = String(decoding: encoded, as: UTF8.self)

        #expect(!text.contains("SECRET"))
        #expect(!text.contains("REMCDReminder"))
    }

    /// `EKReminder.h`: *"you may encounter the case where isCompleted is YES, but completionDate
    /// is nil, if the reminder was completed using a different client."*
    @Test("a completed reminder with no completion date maps without complaint")
    func completedWithoutDate() throws {
        let raw = RawReminder(
            itemId: "i",
            calendarId: "cal-inbox",
            title: "done elsewhere",
            isCompleted: true,
            completedAt: nil
        )
        let snapshot = raw.snapshot(id: .generate(), alias: try alias("inbox"))
        #expect(snapshot.isCompleted)
        #expect(snapshot.completedAt == nil)
    }
}

@Suite("Allowlist reverse lookup")
struct AllowlistLookupTests {
    private func table() throws -> Allowlist {
        Allowlist(entries: [
            try allowlistEntry("inbox", calendarId: "CAL-INBOX"),
            try allowlistEntry("work", calendarId: "CAL-WORK"),
            try allowlistEntry("gone", calendarId: "CAL-GONE", state: .broken),
        ])
    }

    @Test("a healthy binding resolves back to its alias")
    func resolvesHealthy() throws {
        #expect(try table().alias(forCalendarId: "CAL-INBOX") == (try alias("inbox")))
        #expect(try table().alias(forCalendarId: "CAL-WORK") == (try alias("work")))
    }

    /// The 404 branch of `complete`. A reminder that has been moved into a list nobody
    /// allowlisted must be indistinguishable from one that never existed.
    @Test("a calendar nobody bound resolves to nothing")
    func unbound() throws {
        #expect(try table().alias(forCalendarId: "CAL-SOMEONE-ELSES") == nil)
        #expect(try table().alias(forCalendarId: "") == nil)
    }

    @Test("a broken binding stops matching rather than staying quietly writable")
    func brokenStopsMatching() throws {
        #expect(try table().alias(forCalendarId: "CAL-GONE") == nil)
    }

    @Test("identifier comparison is exact, never a prefix or a case-fold")
    func exactComparison() throws {
        #expect(try table().alias(forCalendarId: "CAL-INBOX-2") == nil)
        #expect(try table().alias(forCalendarId: "CAL-INBO") == nil)
        #expect(try table().alias(forCalendarId: "cal-inbox") == nil)
    }
}
