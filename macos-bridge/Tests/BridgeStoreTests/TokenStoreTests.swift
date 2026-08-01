import BridgeCore
import Foundation
import Security
import Testing

@testable import BridgeStore

@Suite("Token generation")
struct TokenFactoryTests {
    @Test("a generated token is a 256-bit secret in an HTTP-header-safe encoding")
    func generatesWellFormedToken() throws {
        let generated = try TokenFactory.generate(now: Date(timeIntervalSince1970: 1_780_000_000))

        #expect(generated.token.hasPrefix("erdab_"))
        let encoded = generated.token.dropFirst(TokenFactory.prefix.count)
        // Unpadded base64url: 32 bytes → 43 characters, no `+`, `/` or `=`.
        #expect(encoded.count == 43)
        #expect(encoded.allSatisfy { $0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" })
    }

    @Test("the material verifies its own token and nothing else")
    func materialVerifiesItsToken() throws {
        let generated = try TokenFactory.generate(now: Date())
        let verifier = TokenVerifier(material: generated.material, hasher: CryptoKitSha256Hasher())

        #expect(verifier.verify(presentedToken: generated.token) == generated.material.tokenId)
        #expect(verifier.verify(presentedToken: generated.token + "x") == nil)
        #expect(verifier.verify(presentedToken: String(generated.token.dropLast())) == nil)
        #expect(verifier.verify(presentedToken: "") == nil)
    }

    @Test("the token id is the first four bytes of SHA-256 over the presented token")
    func derivesTokenIdFromTheToken() throws {
        let generated = try TokenFactory.generate(now: Date())
        let hasher = CryptoKitSha256Hasher()
        let expected = TokenId(tokenDigest: hasher.sha256(Array(generated.token.utf8)))
        #expect(generated.material.tokenId == expected)
    }

    @Test("two generations share neither secret nor salt")
    func generationsAreUnique() throws {
        let first = try TokenFactory.generate(now: Date())
        let second = try TokenFactory.generate(now: Date())

        #expect(first.token != second.token)
        #expect(first.material.salt != second.material.salt)
        #expect(first.material.digest != second.material.digest)
        #expect(first.material.tokenId != second.material.tokenId)
    }

    @Test("the same token under a fresh salt yields a different digest")
    func saltIsUsed() throws {
        let hasher = CryptoKitSha256Hasher()
        let token = Array("erdab_fixed".utf8)
        let a = hasher.sha256(TokenMaterial.digestPreimage(salt: Array(repeating: 1, count: 16), token: token))
        let b = hasher.sha256(TokenMaterial.digestPreimage(salt: Array(repeating: 2, count: 16), token: token))
        #expect(a != b)
    }

    @Test("the CSPRNG returns the requested number of distinct-looking bytes")
    func secureRandom() throws {
        let first = try SecureRandom.bytes(32)
        let second = try SecureRandom.bytes(32)
        #expect(first.count == 32)
        #expect(first != second)
        #expect(try SecureRandom.bytes(0).isEmpty)
    }

    @Test("CryptoKit satisfies the BridgeCore hashing seam")
    func hasherMatchesKnownVector() {
        // SHA-256("abc")
        let digest = CryptoKitSha256Hasher().sha256(Array("abc".utf8))
        #expect(digest.map { String(format: "%02x", $0) }.joined()
            == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")
    }
}

@Suite("File token store")
struct FileTokenStoreTests {
    private let root = TemporaryRoot()

    private func store() -> FileTokenStore {
        FileTokenStore(url: root.directories.tokenFileURL)
    }

    @Test("an absent token file reads as no token, not as an error")
    func absentIsNil() throws {
        #expect(try store().load() == nil)
    }

    @Test("material round-trips byte for byte")
    func roundTrips() throws {
        let subject = store()
        // A wall-clock `Date()` on purpose: the stored format is whole epoch seconds, so this
        // is what catches a generator that hands back sub-second precision it cannot persist.
        let generated = try TokenFactory.generate(now: Date())
        try subject.save(generated.material)

        let loaded = try #require(try subject.load())
        #expect(loaded == generated.material)
        #expect(loaded.salt == generated.material.salt)
        #expect(loaded.digest == generated.material.digest)
        #expect(loaded.createdAt == generated.material.createdAt)
    }

    @Test("the token file is 0600 inside a 0700 directory")
    func setsPermissions() throws {
        let subject = store()
        try subject.save(try TokenFactory.generate(now: Date()).material)

        #expect(try FilePermissions.mode(of: root.directories.tokenFileURL) == FilePermissions.file)
        #expect(try FilePermissions.mode(of: root.directories.applicationSupport) == FilePermissions.directory)
    }

    @Test("saving again replaces the material and leaves no scratch file behind")
    func overwrites() throws {
        let subject = store()
        let first = try TokenFactory.generate(now: Date())
        let second = try TokenFactory.generate(now: Date())
        try subject.save(first.material)
        try subject.save(second.material)

        #expect(try subject.load() == second.material)
        #expect(try FilePermissions.mode(of: root.directories.tokenFileURL) == FilePermissions.file)

        let leftovers = try FileManager.default
            .contentsOfDirectory(atPath: root.directories.applicationSupport.path)
        #expect(leftovers == ["token.json"])
    }

    @Test("deleting is idempotent")
    func deletes() throws {
        let subject = store()
        try subject.save(try TokenFactory.generate(now: Date()).material)

        try subject.delete()
        #expect(try subject.load() == nil)
        try subject.delete()  // must not throw
    }

    @Test("a corrupt token file is an error, not a silently absent token")
    func rejectsCorruptFile() throws {
        try root.directories.create()
        try Data("not json".utf8).write(to: root.directories.tokenFileURL)

        #expect(throws: (any Error).self) { try store().load() }
    }
}

@Suite("Token service")
struct TokenServiceTests {
    private let root = TemporaryRoot()

    private func service() -> TokenService {
        TokenService(store: FileTokenStore(url: root.directories.tokenFileURL))
    }

    @Test("with no token stored there is no verifier — the bridge must not run open")
    func noTokenNoVerifier() throws {
        #expect(try service().currentVerifier() == nil)
        #expect(try service().currentTokenId() == nil)
    }

    @Test("rotation invalidates the previous token immediately")
    func rotationInvalidatesTheOldToken() throws {
        let subject = service()
        let first = try subject.rotate(now: Date(timeIntervalSince1970: 1_780_000_000))
        let second = try subject.rotate(now: Date(timeIntervalSince1970: 1_780_000_100))

        let verifier = try #require(try subject.currentVerifier())
        #expect(verifier.verify(presentedToken: second.token) == second.material.tokenId)
        #expect(verifier.verify(presentedToken: first.token) == nil)
        #expect(try subject.currentTokenId() == second.material.tokenId)
    }

    @Test("revoking leaves nothing behind")
    func revoke() throws {
        let subject = service()
        _ = try subject.rotate(now: Date())
        try subject.revoke()
        #expect(try subject.currentVerifier() == nil)
    }

    @Test("the backend is named for the status UI")
    func reportsBackend() {
        #expect(service().backendName == "file(token.json)")
        #expect(TokenStoreFactory.make(backend: .file, directories: root.directories).backendName
            == "file(token.json)")
        #expect(TokenStoreFactory.make(backend: .keychain, directories: root.directories).backendName
            == "keychain(de.philippbaum.erdabridge/api-token)")
    }
}

@Suite("Keychain token store")
struct KeychainTokenStoreTests {
    /// A **read-only** probe. It asserts the query dictionary is well-formed and that a missing
    /// item reads as `nil` rather than as an error — no ACL is involved, so no prompt appears.
    ///
    /// Everything that actually depends on the code signature (add, update, read-back after a
    /// rebuild) cannot be tested here at all: `swift test` runs under `xctest`, not under our
    /// signed bundle, and the legacy keychain's ACL names the trusted application by its
    /// signature. `StoreSelfTest`, run from `ErdaBridge.app --selftest`, is the authority.
    @Test("a lookup for an unused service reads as absent")
    func missingItemIsNil() throws {
        let store = KeychainTokenStore(
            service: "de.philippbaum.erdabridge.unit-test-\(UUID().uuidString)",
            account: "api-token"
        )
        #expect(try store.load() == nil)
    }

    @Test("deleting something that was never there is not an error")
    func deleteIsIdempotent() throws {
        let store = KeychainTokenStore(
            service: "de.philippbaum.erdabridge.unit-test-\(UUID().uuidString)",
            account: "api-token"
        )
        try store.delete()
    }

    @Test("the error type carries a readable status without leaking it to clients")
    func errorDescribesItself() {
        let error = KeychainError(status: errSecItemNotFound, operation: "copyMatching")
        #expect(error.description.contains("copyMatching"))
        #expect(!error.isMissingEntitlement)
        #expect(KeychainError(status: errSecMissingEntitlement, operation: "add").isMissingEntitlement)
    }
}
