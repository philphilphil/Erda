import CryptoKit
import Foundation
import Testing

@testable import BridgeCore

@Suite("Constant-time comparison")
struct ConstantTimeTests {
    private let reference: [UInt8] = (0..<32).map { UInt8($0 &* 7 &+ 3) }

    @Test("identical buffers compare equal")
    func acceptsIdentical() {
        #expect(ConstantTime.equal(reference, reference))
    }

    @Test("a single flipped bit is caught, wherever it is", arguments: [0, 1, 15, 30, 31])
    func rejectsBitFlip(index: Int) {
        var other = reference
        other[index] ^= 0x01
        #expect(!ConstantTime.equal(reference, other))
    }

    @Test("truncation is caught")
    func rejectsTruncation() {
        #expect(!ConstantTime.equal(reference, Array(reference.dropLast())))
        #expect(!ConstantTime.equal(reference, Array(reference.prefix(1))))
    }

    @Test("extension is caught, including a repeat of the whole buffer")
    func rejectsExtension() {
        #expect(!ConstantTime.equal(reference, reference + [0x00]))
        // The implementation wraps the shorter side; a doubled buffer would compare equal
        // byte-for-byte if the length were not folded into the result.
        #expect(!ConstantTime.equal(reference, reference + reference))
    }

    @Test("empty buffers")
    func handlesEmpty() {
        #expect(ConstantTime.equal([UInt8](), [UInt8]()))
        #expect(!ConstantTime.equal(reference, [UInt8]()))
        #expect(!ConstantTime.equal([UInt8](), reference))
    }

    @Test("both buffers are read in full even when the first byte already differs")
    func readsBothBuffersFully() {
        let leftCounter = ReadCounter()
        let rightCounter = ReadCounter()
        var differing = reference
        differing[0] ^= 0xFF

        _ = ConstantTime.equal(
            CountingBytes(storage: reference, counter: leftCounter),
            CountingBytes(storage: differing, counter: rightCounter)
        )

        #expect(leftCounter.reads == reference.count)
        #expect(rightCounter.reads == differing.count)
    }

    @Test("a length mismatch still walks the longer buffer")
    func readsBothBuffersOnLengthMismatch() {
        let leftCounter = ReadCounter()
        let rightCounter = ReadCounter()

        _ = ConstantTime.equal(
            CountingBytes(storage: Array(reference.prefix(4)), counter: leftCounter),
            CountingBytes(storage: reference, counter: rightCounter)
        )

        #expect(leftCounter.reads == reference.count)
        #expect(rightCounter.reads == reference.count)
    }
}

@Suite("Token verification")
struct TokenVerifierTests {
    private let token = "erdab_9lQ2xR7vT4pN0aZ8sK1yH6bG3jW5cM2d"

    private func verifier() throws -> TokenVerifier {
        TokenVerifier(material: try tokenMaterial(for: token), hasher: CryptoKitHasher())
    }

    @Test("the correct token verifies and yields its id")
    func acceptsCorrectToken() throws {
        let material = try tokenMaterial(for: token)
        let verified = TokenVerifier(material: material, hasher: CryptoKitHasher())
            .verify(presentedToken: token)
        #expect(verified == material.tokenId)
    }

    @Test("a one-character change is rejected")
    func rejectsBitFlip() throws {
        let subject = try verifier()
        #expect(subject.verify(presentedToken: "erdab_9lQ2xR7vT4pN0aZ8sK1yH6bG3jW5cM2e") == nil)
    }

    @Test("truncation, extension and emptiness are rejected", arguments: [
        "", "erdab_", "erdab_9lQ2xR7vT4pN0aZ8sK1yH6bG3jW5cM2", "erdab_9lQ2xR7vT4pN0aZ8sK1yH6bG3jW5cM2dd",
    ])
    func rejectsMalformedToken(candidate: String) throws {
        let subject = try verifier()
        #expect(subject.verify(presentedToken: candidate) == nil)
    }

    @Test("the same token under a different salt does not verify")
    func rejectsWrongSalt() throws {
        let material = try tokenMaterial(for: token, salt: Array(repeating: 0x5A, count: TokenMaterial.saltLength))
        let other = try tokenMaterial(for: token)
        // Digest from one salt, verifier holding the other.
        let mismatched = try TokenMaterial(
            tokenId: other.tokenId,
            salt: other.salt,
            digest: material.digest,
            createdAt: other.createdAt
        )
        #expect(TokenVerifier(material: mismatched, hasher: CryptoKitHasher()).verify(presentedToken: token) == nil)
    }

    @Test("the digest pre-image is salt then token, in that order")
    func digestPreimageLayout() {
        #expect(TokenMaterial.digestPreimage(salt: [1, 2], token: [3, 4]) == [1, 2, 3, 4])
        // Concatenation without a fixed field order would let (salt, token) and (token, salt)
        // collide; the order is part of the contract with `BridgeStore`.
        #expect(TokenMaterial.digestPreimage(salt: [1, 2], token: [3, 4])
            != TokenMaterial.digestPreimage(salt: [3, 4], token: [1, 2]))
    }

    @Test("material rejects a wrong-sized salt or digest")
    func validatesMaterialShape() throws {
        let id = try #require(TokenId(rawValue: "a1b2c3d4"))
        #expect(throws: TokenMaterialError.malformed) {
            try TokenMaterial(tokenId: id, salt: [0x01], digest: Array(repeating: 0, count: 32), createdAt: Date())
        }
        #expect(throws: TokenMaterialError.malformed) {
            try TokenMaterial(tokenId: id, salt: Array(repeating: 0, count: 16), digest: [0x01], createdAt: Date())
        }
        #expect(throws: TokenMaterialError.malformed) {
            try TokenMaterial(
                version: 2,
                tokenId: id,
                salt: Array(repeating: 0, count: 16),
                digest: Array(repeating: 0, count: 32),
                createdAt: Date()
            )
        }
    }

    @Test("material round-trips through the keychain JSON shape")
    func materialRoundTrips() throws {
        let material = try tokenMaterial(for: token)
        let data = try JSONEncoder().encode(material)
        let parsed = try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
        #expect(Set(parsed.keys) == ["v", "tokenId", "salt", "digest", "createdAt"])
        #expect(parsed["v"] as? Int == 1)

        let decoded = try JSONDecoder().decode(TokenMaterial.self, from: data)
        #expect(decoded == material)
    }

    @Test("token ids are 8 lowercase hex characters derived from the digest")
    func derivesTokenId() {
        let digest = CryptoKitHasher().sha256(Array("hello".utf8))
        let id = TokenId(tokenDigest: digest)
        #expect(id?.rawValue == digest.prefix(4).map { String(format: "%02x", $0) }.joined())
        #expect(id?.rawValue.count == 8)

        #expect(TokenId(rawValue: "A1B2C3D4") == nil, "uppercase hex is not the canonical form")
        #expect(TokenId(rawValue: "a1b2c3d") == nil)
        #expect(TokenId(rawValue: "a1b2c3d4e") == nil)
        #expect(TokenId(rawValue: "zzzzzzzz") == nil)
        #expect(TokenId(tokenDigest: [0x01, 0x02]) == nil)
    }

    @Test("bearer extraction", arguments: [
        ("Bearer abc", "abc"),
        ("bearer abc", "abc"),
        ("BEARER abc", "abc"),
        ("Bearer  abc", " abc"),
    ])
    func extractsBearer(header: String, expected: String) {
        #expect(TokenVerifier.bearerToken(from: header) == expected)
    }

    @Test("non-bearer credentials are refused", arguments: [
        "", "Bearer", "Bearer ", "Basic abc", "abc", "Token abc",
    ])
    func refusesNonBearer(header: String) {
        #expect(TokenVerifier.bearerToken(from: header) == nil)
    }

    @Test("an absent header is refused")
    func refusesMissingHeader() {
        #expect(TokenVerifier.bearerToken(from: nil) == nil)
    }
}

@Suite("Request hashing")
struct RequestHashTests {
    @Test("the pre-image is method, path and raw body separated by NUL")
    func layout() {
        let bytes = RequestHash.preimage(method: "POST", path: "/v1/reminders", body: [0x7B, 0x7D])
        #expect(bytes == Array("POST".utf8) + [0x00] + Array("/v1/reminders".utf8) + [0x00] + [0x7B, 0x7D])
    }

    @Test("the separator prevents field-boundary collisions")
    func separatorPreventsCollisions() {
        // Without the NUL, "/v1/a" + body "b" and "/v1/ab" + empty body would be identical.
        #expect(RequestHash.preimage(method: "POST", path: "/v1/a", body: Array("b".utf8))
            != RequestHash.preimage(method: "POST", path: "/v1/ab", body: []))
        #expect(RequestHash.preimage(method: "POST", path: "/v1/x", body: [])
            != RequestHash.preimage(method: "GET", path: "/v1/x", body: []))
    }

    @Test("the body is hashed as raw bytes, not as a re-encoded object")
    func hashesRawBytes() {
        let compact = Array(#"{"a":1}"#.utf8)
        let spaced = Array(#"{"a": 1}"#.utf8)
        #expect(RequestHash.preimage(method: "POST", path: "/v1/reminders", body: compact)
            != RequestHash.preimage(method: "POST", path: "/v1/reminders", body: spaced))
    }

    @Test("an empty body still produces a well-formed pre-image")
    func handlesEmptyBody() {
        #expect(RequestHash.preimage(method: "GET", path: "/v1/status", body: [])
            == Array("GET".utf8) + [0x00] + Array("/v1/status".utf8) + [0x00])
    }
}
