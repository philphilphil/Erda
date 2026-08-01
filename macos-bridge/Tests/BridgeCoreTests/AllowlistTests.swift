import Foundation
import Testing

@testable import BridgeCore

@Suite("Alias validation")
struct AliasTests {
    @Test("valid aliases", arguments: [
        "a", "0", "inbox", "work-2026", "shopping_list", "x9", "z-_-z",
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",  // exactly 32
    ])
    func accepts(candidate: String) {
        #expect(Alias(rawValue: candidate) != nil, "\(candidate) should be a valid alias")
    }

    @Test("invalid aliases", arguments: [
        "",                                    // empty
        "-inbox", "_inbox",                    // must start alphanumeric
        "Inbox", "INBOX", "inBox",             // uppercase
        "in box", "in\tbox", "in\nbox",        // whitespace
        "inbox.", "inbox!", "in/box", "in:box", "in\\box",
        "ínbox", "inbox€", "🧾",               // non-ASCII
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",   // 33
        " inbox", "inbox ",                    // untrimmed
    ])
    func rejects(candidate: String) {
        #expect(Alias(rawValue: candidate) == nil, "\(candidate) should not be a valid alias")
    }

    @Test("a combining sequence cannot smuggle extra scalars past the length cap")
    func countsUnicodeScalars() {
        // 32 "a" plus a combining acute would be 32 `Character`s but 33 scalars — and the
        // combining mark is not in the charset anyway.
        #expect(Alias(rawValue: String(repeating: "a", count: 32) + "\u{0301}") == nil)
    }

    @Test("aliases encode as their bare string")
    func encodesAsString() throws {
        let encoded = try ResponseJSON.encode([try alias("inbox")])
        #expect(String(decoding: encoded, as: UTF8.self) == #"["inbox"]"#)
    }
}

@Suite("Allowlist resolution")
struct AllowlistTests {
    private func table() throws -> Allowlist {
        Allowlist(entries: [
            try allowlistEntry("inbox"),
            try allowlistEntry("work"),
            try allowlistEntry("gone", state: .broken),
        ])
    }

    @Test("a known healthy alias resolves to its calendar")
    func resolvesKnownAlias() throws {
        let entry = try table().resolve(try alias("inbox"))
        #expect(entry.calendarId == "cal-inbox")
    }

    @Test("an unknown alias fails closed — never a default list")
    func unknownAliasFailsClosed() throws {
        let list = try table()
        let unknown = try alias("personal")
        #expect(throws: ApiError.aliasUnknown) { try list.resolve(unknown) }
        #expect(list.resolveHealthy(unknown) == nil)
        #expect(list.entry(for: unknown) == nil)
    }

    @Test("a broken alias is refused rather than re-bound by title")
    func brokenAliasFailsClosed() throws {
        let list = try table()
        let broken = try alias("gone")
        #expect(throws: ApiError.aliasBroken) { try list.resolve(broken) }
        #expect(list.resolveHealthy(broken) == nil)
        #expect(list.brokenAliases == [broken])
        #expect(list.healthyAliases == [try alias("inbox"), try alias("work")])
    }

    @Test("availability reflects authorization and allowlist health")
    func reportsAvailability() throws {
        let list = try table()
        #expect(list.availability(authorized: true) == .ok)
        #expect(list.availability(authorized: false) == .unauthorized)

        let onlyBroken = Allowlist(entries: [try allowlistEntry("gone", state: .broken)])
        #expect(onlyBroken.availability(authorized: true) == .noAllowlist)
        #expect(Allowlist(entries: []).availability(authorized: true) == .noAllowlist)
    }

    /// The property the whole fail-closed posture rests on: nothing outside the table resolves,
    /// and there is no code path that substitutes a default.
    @Test("random aliases never resolve to anything")
    func randomAliasesNeverResolve() throws {
        let list = try table()
        let known: Set<String> = ["inbox", "work", "gone"]
        let aliasCharset = Array("abcdefghijklmnopqrstuvwxyz0123456789_-")
        let wildCharset = Array("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-. /\\:!\u{00e9}\u{1F600}\t\n\u{0000}")

        var generator = SeededGenerator(seed: 0x5EED_1234_ABCD_0001)
        var validAliasesGenerated = 0

        for iteration in 0..<4000 {
            // Half the draws come from the alias charset so a decent number of them actually
            // pass validation and exercise the resolver rather than only the parser.
            let charset = iteration.isMultiple(of: 2) ? aliasCharset : wildCharset
            let length = Int(generator.next(upperBound: 40))
            let candidate = String((0..<length).map { _ in charset[Int(generator.next(upperBound: UInt64(charset.count)))] })

            guard let parsed = Alias(rawValue: candidate) else { continue }
            validAliasesGenerated += 1
            guard !known.contains(parsed.rawValue) else { continue }

            #expect(list.resolveHealthy(parsed) == nil, "\(candidate) must not resolve")
            #expect(list.entry(for: parsed) == nil, "\(candidate) must not resolve")
            #expect(throws: ApiError.aliasUnknown) { try list.resolve(parsed) }
        }

        // Guards against the test passing vacuously because nothing ever parsed.
        #expect(validAliasesGenerated > 100, "generated only \(validAliasesGenerated) valid aliases")
    }
}

/// A deterministic generator, so a failure reproduces instead of appearing once a week.
struct SeededGenerator: RandomNumberGenerator {
    private var state: UInt64

    init(seed: UInt64) {
        self.state = seed == 0 ? 0x9E37_79B9_7F4A_7C15 : seed
    }

    mutating func next() -> UInt64 {
        // xorshift64*
        state ^= state >> 12
        state ^= state << 25
        state ^= state >> 27
        return state &* 2_685_821_657_736_338_717
    }

    mutating func next(upperBound: UInt64) -> UInt64 {
        upperBound == 0 ? 0 : next() % upperBound
    }
}
