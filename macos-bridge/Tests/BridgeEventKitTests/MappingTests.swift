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
            calendarId: "CAL-GROCERIES",
            title: "Buy milk",
            notes: "oat, not soy",
            dueAt: due,
            priority: 5
        )
        let id = BridgeID.generate()
        let snapshot = raw.snapshot(id: id, list: try listName("Groceries"))

        #expect(snapshot.id == id)
        #expect(snapshot.list == (try listName("Groceries")))
        #expect(snapshot.title == "Buy milk")
        #expect(snapshot.notes == "oat, not soy")
        #expect(snapshot.dueAt == due)
        #expect(snapshot.priority == 5)
        #expect(snapshot.isCompleted == false)
        #expect(snapshot.completedAt == nil)
    }

    /// The wire format has no field that could carry an EventKit identifier, and this is the
    /// mapping that would have to leak one for it to happen. A list's *name* does go out — it has
    /// to, since that is how a caller addresses one — but its `calendarIdentifier` never does.
    @Test("no EventKit identifier survives the mapping")
    func dropsEventKitIdentifiers() throws {
        let raw = RawReminder(
            itemId: "x-apple-reminderkit://REMCDReminder/SECRET",
            externalId: "external-SECRET",
            calendarId: "cal-SECRET",
            title: "t"
        )
        let encoded = try ResponseJSON.encode(raw.snapshot(id: .generate(), list: try listName("Groceries")))
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
            calendarId: "CAL-GROCERIES",
            title: "done elsewhere",
            isCompleted: true,
            completedAt: nil
        )
        let snapshot = raw.snapshot(id: .generate(), list: try listName("Groceries"))
        #expect(snapshot.isCompleted)
        #expect(snapshot.completedAt == nil)
    }
}

/// The branch that decides which list a name means. With the allowlist gone this is no longer an
/// access check — every list is reachable — but it is still the branch that decides *where* a
/// write lands, so it has to refuse to guess.
@Suite("List name resolution")
struct ListLookupTests {
    private let groceries = ListLookup.Candidate(calendarId: "CAL-GROCERIES", title: "Groceries")
    private let work = ListLookup.Candidate(calendarId: "CAL-WORK", title: "Work")
    private let umlaut = ListLookup.Candidate(calendarId: "CAL-EINKAUF", title: "Einkäufe")

    private var table: [ListLookup.Candidate] { [groceries, work, umlaut] }

    @Test("an exact name resolves to its list")
    func resolvesExact() throws {
        #expect(try ListLookup.resolve(try listName("Groceries"), in: table) == groceries)
        #expect(try ListLookup.resolve(try listName("Work"), in: table) == work)
        #expect(try ListLookup.resolve(try listName("Einkäufe"), in: table) == umlaut)
    }

    /// A name people type by hand, or a model repeats from memory, is not going to be
    /// case-perfect. Folding is safe exactly as long as it stays unambiguous.
    @Test("a unique case-insensitive match is accepted", arguments: [
        "groceries", "GROCERIES", "gRoCeRiEs", "einkäufe", "EINKÄUFE",
    ])
    func resolvesCaseInsensitively(spelling: String) throws {
        let resolved = try ListLookup.resolve(try listName(spelling), in: table)
        #expect(resolved == groceries || resolved == umlaut)
    }

    /// The whole fail-closed posture: a name nobody's list wears resolves to nothing, and never to
    /// "the first list" or "the default one".
    @Test("a name that matches nothing fails closed — never a default list", arguments: [
        "Personal", "Inbox", "Grocerie", "Groceries 2", "Gro", "Reminders",
    ])
    func unknownNameFailsClosed(spelling: String) throws {
        #expect(throws: ApiError.noSuchList) {
            try ListLookup.resolve(try listName(spelling), in: table)
        }
    }

    /// Two accounts can both hold a list called "Reminders". The wire format carries no account,
    /// so there is no honest way to pick — and picking is how the bridge would write into somebody
    /// else's shared list.
    @Test("an ambiguous name is refused rather than resolved to one of the pair")
    func ambiguousNameFailsClosed() throws {
        let duplicates = [
            ListLookup.Candidate(calendarId: "CAL-ICLOUD", title: "Reminders"),
            ListLookup.Candidate(calendarId: "CAL-LOCAL", title: "Reminders"),
        ]
        #expect(throws: ApiError.noSuchList) {
            try ListLookup.resolve(try listName("Reminders"), in: duplicates)
        }
        // Case-folded ambiguity is refused for the same reason.
        let folded = [
            ListLookup.Candidate(calendarId: "CAL-A", title: "Work"),
            ListLookup.Candidate(calendarId: "CAL-B", title: "WORK"),
        ]
        #expect(throws: ApiError.noSuchList) {
            try ListLookup.resolve(try listName("work"), in: folded)
        }
    }

    /// …but an exact match wins over a case-folded one, so a list called exactly what you asked
    /// for is never withheld because a differently-cased sibling exists.
    @Test("an exact match beats a case-folded sibling")
    func exactWinsOverFolded() throws {
        let both = [
            ListLookup.Candidate(calendarId: "CAL-A", title: "Work"),
            ListLookup.Candidate(calendarId: "CAL-B", title: "WORK"),
        ]
        #expect(try ListLookup.resolve(try listName("Work"), in: both).calendarId == "CAL-A")
        #expect(try ListLookup.resolve(try listName("WORK"), in: both).calendarId == "CAL-B")
    }

    @Test("an empty Mac resolves nothing")
    func emptyTable() throws {
        #expect(throws: ApiError.noSuchList) {
            try ListLookup.resolve(try listName("Groceries"), in: [])
        }
    }

    // MARK: - The reverse lookup, which is `complete`'s 404 branch

    @Test("a live calendar resolves back to its name")
    func reverseResolves() throws {
        #expect(ListLookup.name(forCalendarId: "CAL-GROCERIES", in: table) == (try listName("Groceries")))
        #expect(ListLookup.name(forCalendarId: "CAL-WORK", in: table) == (try listName("Work")))
    }

    /// A reminder whose list has been deleted, or which was never in a reminder list at all, must
    /// be a flat 404 — the same answer as an id that was never issued.
    @Test("a calendar this Mac no longer reports resolves to nothing")
    func reverseFailsClosed() throws {
        #expect(ListLookup.name(forCalendarId: "CAL-SOMEONE-ELSES", in: table) == nil)
        #expect(ListLookup.name(forCalendarId: "", in: table) == nil)
        #expect(ListLookup.name(forCalendarId: "CAL-GROCERIES", in: []) == nil)
    }

    @Test("identifier comparison is exact, never a prefix or a case-fold")
    func exactIdentifierComparison() {
        #expect(ListLookup.name(forCalendarId: "CAL-GROCERIES-2", in: table) == nil)
        #expect(ListLookup.name(forCalendarId: "CAL-GROCERIE", in: table) == nil)
        #expect(ListLookup.name(forCalendarId: "cal-groceries", in: table) == nil)
    }

    /// A list whose title cannot be expressed as a `ListName` has no name a caller could send, so
    /// it matches nothing and its reminders stay invisible. Fails closed rather than half-working.
    @Test("a list with an unnameable title is unreachable in both directions")
    func unnameableTitle() throws {
        let weird = ListLookup.Candidate(calendarId: "CAL-WEIRD", title: "line\none")
        #expect(ListLookup.canonicalName(weird) == nil)
        #expect(ListLookup.name(forCalendarId: "CAL-WEIRD", in: [weird]) == nil)
    }

    /// Randomly generated names never resolve to a list that is not there — the property the
    /// fail-closed posture rests on, checked over a lot of input rather than a handful of cases.
    @Test("random names never resolve to something that is not theirs")
    func randomNamesNeverResolve() throws {
        let known: Set<String> = ["groceries", "work", "einkäufe"]
        let charset = Array("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 -_äöü🧾")

        var generator = SeededGenerator(seed: 0x5EED_1234_ABCD_0001)
        var parsed = 0

        for _ in 0..<4000 {
            let length = 1 + Int(generator.next(upperBound: 24))
            let text = String((0..<length).map { _ in charset[Int(generator.next(upperBound: UInt64(charset.count)))] })
            guard let name = ListName(rawValue: text) else { continue }
            parsed += 1
            guard !known.contains(name.rawValue.lowercased()) else { continue }

            #expect(throws: ApiError.noSuchList, "\(text) must not resolve") {
                try ListLookup.resolve(name, in: table)
            }
        }

        // Guards against the test passing vacuously because nothing ever parsed.
        #expect(parsed > 100, "only \(parsed) candidates parsed")
    }
}
