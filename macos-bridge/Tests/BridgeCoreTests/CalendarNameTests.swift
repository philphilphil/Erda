import Foundation
import Testing

@testable import BridgeCore

/// `CalendarName` is `ListName`'s twin — same validation, via the same `NameHygiene` — and a
/// deliberately different type. These tests pin both halves of that: that the rules really are the
/// same, and that the types really are not interchangeable.
@Suite("Calendar name validation")
struct CalendarNameTests {
    @Test("names people actually use are accepted", arguments: [
        "Privat", "privat", "Arbeit", "Work", "Family / Shared", "Geburtstage",
        "Café ☕️", "日历", "calendar-with-dashes", "2026 planning", "a", "🗓️",
        "Holidays in Germany",
        String(repeating: "a", count: Limits.calendarNameMaxLength),  // exactly at the cap
    ])
    func accepts(candidate: String) {
        #expect(CalendarName(rawValue: candidate) != nil, "\(candidate) should be a valid calendar name")
    }

    @Test("empty, over-long and control-bearing names are refused", arguments: [
        "",
        "   ",                                                             // whitespace only
        "\t", "\n",
        String(repeating: "a", count: Limits.calendarNameMaxLength + 1),   // one past the cap
        "Pri\u{0000}vat",                                                  // NUL, straight into SQLite
        "Pri\nvat", "Pri\rvat",                                            // would break JSONL
        "Pri\u{001B}[31mvat",                                              // ANSI escape
        "Pri\u{0085}vat",                                                  // C1 NEL
    ])
    func rejects(candidate: String) {
        #expect(CalendarName(rawValue: candidate) == nil, "\(candidate.debugDescription) should be refused")
    }

    @Test("surrounding whitespace is trimmed rather than rejected")
    func trims() throws {
        #expect(try calendarName("  Privat  ").rawValue == "Privat")
        #expect(CalendarName(rawValue: " Privat ") == CalendarName(rawValue: "Privat"))
        // Inner whitespace is part of the name and is left exactly as it came.
        #expect(try calendarName("Work  Trips").rawValue == "Work  Trips")
    }

    @Test("isValid describes the canonical form")
    func canonicalPredicate() {
        #expect(CalendarName.isValid("Privat"))
        #expect(!CalendarName.isValid(" Privat"))
        #expect(!CalendarName.isValid("Privat "))
        #expect(!CalendarName.isValid(""))
    }

    @Test("a combining sequence cannot smuggle extra scalars past the length cap")
    func countsUnicodeScalars() {
        let atCap = String(repeating: "a", count: Limits.calendarNameMaxLength)
        #expect(CalendarName(rawValue: atCap + "\u{0301}") == nil)
    }

    @Test("names are case-sensitive as values — folding is a resolution concern, not this type's")
    func casePreserved() throws {
        #expect(try calendarName("Privat") != (try calendarName("privat")))
    }

    @Test("names encode as their bare string")
    func encodesAsString() throws {
        let encoded = try ResponseJSON.encode([try calendarName("Privat")])
        #expect(String(decoding: encoded, as: UTF8.self) == #"["Privat"]"#)
    }

    /// The control character is written as a JSON escape sequence rather than as a literal byte
    /// in this source file. Both reach the decoder as the same NUL, but a literal one makes git
    /// treat the whole file as binary — no diff, no review — which is exactly how one sat unnoticed
    /// in `ListNameTests` until this suite was written against it.
    @Test("decoding an invalid name is an invalid request, not a crash")
    func decodeRejects() throws {
        #expect(throws: ApiError.invalidRequest) {
            try StrictJSON.decode(CalendarName.self, from: json(#""""#))
        }
        #expect(throws: ApiError.invalidRequest) {
            try StrictJSON.decode(CalendarName.self, from: json("\"Pri\\u0000vat\""))
        }
        // A name that is merely *unusual* still decodes: an inner space is part of the title.
        #expect(try StrictJSON.decode(CalendarName.self, from: json(#""Pri vat""#)).rawValue == "Pri vat")
    }

    /// The two names accept and refuse exactly the same strings. If they ever diverge it will be
    /// because somebody edited one and not the other, which is the failure this catches — and the
    /// reason both go through `NameHygiene` rather than each carrying its own copy of the rules.
    @Test("a calendar name and a list name are validated identically")
    func sameRulesAsAListName() {
        var generator = SeededGenerator(seed: 0xCA1E_0000_0000_0001)
        var accepted = 0

        for _ in 0..<4000 {
            let length = 1 + Int(generator.next(upperBound: 40))
            var candidate = ""
            for _ in 0..<length {
                let value = UInt32(generator.next(upperBound: 0x1_F600))
                if let scalar = Unicode.Scalar(value) { candidate.unicodeScalars.append(scalar) }
            }

            let asList = ListName(rawValue: candidate)
            let asCalendar = CalendarName(rawValue: candidate)
            #expect((asList == nil) == (asCalendar == nil), "\(candidate.debugDescription) diverged")
            #expect(asList?.rawValue == asCalendar?.rawValue)
            if asCalendar != nil { accepted += 1 }
        }

        // Guards against the test passing vacuously because nothing ever parsed.
        #expect(accepted > 100, "only \(accepted) candidates parsed")
    }

    /// Whatever anyone throws at it, a name that parses is short, single-line and free of control
    /// characters — which is what lets the audit log carry one without a redaction rule.
    @Test("a parsed name is always loggable")
    func parsedNamesAreLoggable() {
        var generator = SeededGenerator(seed: 0xCA1E_0000_0000_0002)

        for _ in 0..<2000 {
            let length = 1 + Int(generator.next(upperBound: 40))
            var candidate = ""
            for _ in 0..<length {
                let value = UInt32(generator.next(upperBound: 0x1_F600))
                if let scalar = Unicode.Scalar(value) { candidate.unicodeScalars.append(scalar) }
            }

            guard let parsed = CalendarName(rawValue: candidate) else { continue }
            #expect(!parsed.rawValue.contains("\n"))
            #expect(!parsed.rawValue.contains("\r"))
            #expect(parsed.rawValue.unicodeScalars.count <= Limits.calendarNameMaxLength)
            for scalar in parsed.rawValue.unicodeScalars {
                #expect(scalar.value >= 0x20, "a control character survived validation")
                #expect(!(0x7F...0x9F).contains(scalar.value), "a C1 control survived validation")
            }
        }
    }
}
