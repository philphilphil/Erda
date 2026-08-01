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

func alias(_ raw: String, sourceLocation: SourceLocation = #_sourceLocation) throws -> Alias {
    try #require(Alias(rawValue: raw), sourceLocation: sourceLocation)
}

func allowlistEntry(_ raw: String, state: AllowlistState = .ok) throws -> AllowlistEntry {
    AllowlistEntry(
        alias: try alias(raw),
        calendarId: "cal-\(raw)",
        titleAtBind: "List \(raw)",
        sourceAtBind: "iCloud",
        boundAt: Date(timeIntervalSince1970: 1_780_000_000),
        state: state
    )
}

/// Everything a test needs to drive the responder, with the knobs it needs to poke.
struct TestHarness {
    let token = "erdab_9lQ2xR7vT4pN0aZ8sK1yH6bG3jW5cM2d"
    let reminders: FakeReminders
    let audit = MemoryAuditSink()
    let idempotency: MemoryIdempotencyStore
    let clock = ManualClock()
    let services: BridgeServices
    let responder: BridgeResponder

    init(
        aliases: [String] = ["inbox", "work"],
        broken: [String] = [],
        rateLimiterCapacities: (global: Int, mutation: Int) = (30, 10),
        tokenPresent: Bool = true
    ) throws {
        let parsedAliases = try aliases.map { try alias($0) }
        let parsedBroken = try broken.map { try alias($0) }
        let entries = try aliases.map { try allowlistEntry($0, state: broken.contains($0) ? .broken : .ok) }
        let allowlist = Allowlist(entries: entries)

        reminders = FakeReminders(aliases: Set(parsedAliases), brokenAliases: Set(parsedBroken))
        let clock = self.clock
        idempotency = MemoryIdempotencyStore(clock: clock)

        let hasher = CryptoKitHasher()
        let material = try TestHarness.material(for: token, hasher: hasher)
        let verifier: TokenVerifier? = tokenPresent
            ? TokenVerifier(material: material, hasher: hasher)
            : nil

        services = BridgeServices(
            reminders: reminders,
            allowlist: { allowlist },
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

let createBody = #"{"alias":"inbox","title":"Buy milk"}"#
