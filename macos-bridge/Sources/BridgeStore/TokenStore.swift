import BridgeCore
import Foundation

/// Where the token's salted digest lives.
public protocol TokenStore: Sendable {
    /// `nil` when no token has been generated yet.
    func load() throws -> TokenMaterial?
    func save(_ material: TokenMaterial) throws
    func delete() throws
    /// For the status UI and the self-test report.
    var backendName: String { get }
}

/// Which backend to use. A flag rather than a hard choice because the Keychain's advantage here
/// is thin — what is stored is a *digest*, not a secret, and a 0600 file inside a 0700 directory
/// on a FileVault-encrypted disk protects it to within a rounding error of the same. What the
/// Keychain adds is an ACL bound to the code signature, and with it the failure mode in §5.3:
/// a modal "ErdaBridge wants to access key …" prompt, which for a login item on an unattended
/// Mac means the bridge hangs at startup. If that fight is not worth it, flip this.
public enum TokenStoreBackend: String, Sendable, CaseIterable, Codable {
    case keychain
    case file
}

public enum TokenStoreFactory {
    public static func make(backend: TokenStoreBackend, directories: BridgeDirectories) -> any TokenStore {
        switch backend {
        case .keychain: KeychainTokenStore()
        case .file: FileTokenStore(url: directories.tokenFileURL)
        }
    }
}

/// The non-secret half of a stored token, for display.
public struct TokenSummary: Sendable, Equatable {
    public let tokenId: TokenId
    public let createdAt: Date

    public init(tokenId: TokenId, createdAt: Date) {
        self.tokenId = tokenId
        self.createdAt = createdAt
    }
}

/// Reads the stored material and turns it into something that can authenticate a request.
public struct TokenService: Sendable {
    private let store: any TokenStore
    private let hasher: any Sha256Hasher

    public init(store: any TokenStore, hasher: any Sha256Hasher = CryptoKitSha256Hasher()) {
        self.store = store
        self.hasher = hasher
    }

    /// `nil` when no token exists yet — the bridge must then refuse every request rather than
    /// running open.
    public func currentVerifier() throws -> TokenVerifier? {
        try store.load().map { TokenVerifier(material: $0, hasher: hasher) }
    }

    public func currentTokenId() throws -> TokenId? {
        try store.load()?.tokenId
    }

    /// What the status UI shows about the stored token. Neither field is a secret — the id is
    /// already in every audit line, and the age is what tells a human whether the `.env` on the
    /// server is likely to be the matching one.
    public func currentSummary() throws -> TokenSummary? {
        try store.load().map { TokenSummary(tokenId: $0.tokenId, createdAt: $0.createdAt) }
    }

    /// Generates and stores a new token, invalidating the old one immediately. The returned
    /// plaintext is the only copy that will ever exist.
    public func rotate(now: Date = Date()) throws -> GeneratedToken {
        let generated = try TokenFactory.generate(now: now, hasher: hasher)
        try store.save(generated.material)
        return generated
    }

    public func revoke() throws {
        try store.delete()
    }

    public var backendName: String { store.backendName }
}
