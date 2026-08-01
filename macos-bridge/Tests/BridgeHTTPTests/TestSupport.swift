import BridgeCore
import CryptoKit
import Foundation
import NIOHTTP1
import Testing

@testable import BridgeHTTP

/// The real hasher, injected through `BridgeCore`'s seam — the same wiring the app uses.
struct CryptoKitHasher: Sha256Hasher {
    func sha256(_ bytes: [UInt8]) -> [UInt8] {
        Array(SHA256.hash(data: Data(bytes)))
    }
}

func listName(_ raw: String, sourceLocation: SourceLocation = #_sourceLocation) throws -> ListName {
    try #require(ListName(rawValue: raw), sourceLocation: sourceLocation)
}

func calendarName(_ raw: String, sourceLocation: SourceLocation = #_sourceLocation) throws -> CalendarName {
    try #require(CalendarName(rawValue: raw), sourceLocation: sourceLocation)
}

/// Everything a test needs to drive the responder, with the knobs it needs to poke.
struct TestHarness {
    let token = "erdab_9lQ2xR7vT4pN0aZ8sK1yH6bG3jW5cM2d"
    let reminders: FakeReminders
    let calendar: FakeCalendar
    let audit = MemoryAuditSink()
    let idempotency: MemoryIdempotencyStore
    let clock = ManualClock()
    let services: BridgeServices
    let responder: BridgeResponder

    init(
        lists: [String] = ["Groceries", "Work"],
        readOnly: [String] = [],
        calendars: [String] = ["Privat", "Arbeit"],
        /// The pinned write target. `nil` is a bridge nobody has configured yet, where every create
        /// is a 503.
        writeCalendar: String? = "Privat",
        readOnlyCalendars: [String] = [],
        ambiguousCalendars: [String] = [],
        rateLimiterCapacities: (global: Int, mutation: Int) = (30, 10),
        tokenPresent: Bool = true
    ) throws {
        reminders = FakeReminders(
            lists: Set(try lists.map { try listName($0) }),
            readOnly: Set(try readOnly.map { try listName($0) })
        )
        let clock = self.clock
        calendar = FakeCalendar(
            calendars: Set(try calendars.map { try calendarName($0) }),
            writeCalendar: try writeCalendar.map { try calendarName($0) },
            readOnly: Set(try readOnlyCalendars.map { try calendarName($0) }),
            ambiguous: Set(try ambiguousCalendars.map { try calendarName($0) }),
            clock: clock
        )
        idempotency = MemoryIdempotencyStore(clock: clock)

        let hasher = CryptoKitHasher()
        let material = try TestHarness.material(for: token, hasher: hasher)
        let verifier: TokenVerifier? = tokenPresent
            ? TokenVerifier(material: material, hasher: hasher)
            : nil

        services = BridgeServices(
            reminders: reminders,
            calendar: calendar,
            tokenVerifier: { verifier },
            rateLimiter: RateLimiter(
                clock: clock,
                globalCapacity: rateLimiterCapacities.global,
                mutationCapacity: rateLimiterCapacities.mutation
            ),
            idempotency: idempotency,
            audit: audit,
            hasher: hasher,
            clock: clock
        )
        responder = BridgeResponder(services: services)
    }

    static func material(for token: String, hasher: some Sha256Hasher) throws -> TokenMaterial {
        let salt = [UInt8](repeating: 0xA5, count: TokenMaterial.saltLength)
        let id = try #require(TokenId(tokenDigest: hasher.sha256(Array(token.utf8))))
        return try TokenMaterial(
            tokenId: id,
            salt: salt,
            digest: hasher.sha256(TokenMaterial.digestPreimage(salt: salt, token: Array(token.utf8))),
            createdAt: Date(timeIntervalSince1970: 1_780_000_000)
        )
    }

    func request(
        _ method: HTTPMethod,
        _ uri: String,
        body: String? = nil,
        authorized: Bool = true,
        contentType: String? = "application/json",
        idempotencyKey: String? = "key-\(UUID().uuidString)",
        version: HTTPVersion = .http1_1,
        extraHeaders: [(String, String)] = []
    ) -> BridgeRequest {
        var headers = HTTPHeaders()
        if authorized { headers.add(name: "Authorization", value: "Bearer \(token)") }
        if let body, !body.isEmpty, let contentType { headers.add(name: "Content-Type", value: contentType) }
        if method == .POST, let idempotencyKey { headers.add(name: "Idempotency-Key", value: idempotencyKey) }
        for header in extraHeaders { headers.add(name: header.0, value: header.1) }

        return BridgeRequest(
            method: method,
            version: version,
            uri: uri,
            headers: headers,
            body: body.map { Array($0.utf8) } ?? []
        )
    }

    func json(_ response: BridgeResponse) throws -> [String: Any] {
        try #require(try JSONSerialization.jsonObject(with: Data(response.body)) as? [String: Any])
    }

    func jsonArray(_ response: BridgeResponse) throws -> [[String: Any]] {
        try #require(try JSONSerialization.jsonObject(with: Data(response.body)) as? [[String: Any]])
    }

    /// `GET /v1/reminders` answers with the `{"items":[…]}` wrapper, never a bare array.
    func jsonItems(_ response: BridgeResponse) throws -> [[String: Any]] {
        try #require(try json(response)["items"] as? [[String: Any]])
    }

    func errorCode(_ response: BridgeResponse) throws -> String {
        try #require(try json(response)["error"] as? String)
    }

    func header(_ response: BridgeResponse, _ name: String) -> String? {
        response.extraHeaders.first { $0.name.lowercased() == name.lowercased() }?.value
    }
}

let createBody = #"{"list":"Groceries","title":"Buy milk"}"#

/// A calendar create whose window lands just after `ManualClock`'s default now
/// (`2026-05-28T20:26:40Z`), so the event is genuinely "upcoming" for a harness that has not
/// advanced its clock.
///
/// It names **no calendar**, because the route takes none: the event lands in whatever the harness
/// pinned as its write target.
let createEventBody = #"""
{"title":"Dentist","startAt":"2026-05-29T09:00:00+02:00","endAt":"2026-05-29T10:00:00+02:00"}
"""#
