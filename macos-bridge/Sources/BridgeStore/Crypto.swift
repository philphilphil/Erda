import BridgeCore
import CryptoKit
import Foundation
import Security

/// The concrete hasher behind `BridgeCore.Sha256Hasher`.
///
/// `BridgeCore` defines the seam and the byte layouts but links no crypto framework, so this is
/// the only place SHA-256 is actually computed.
public struct CryptoKitSha256Hasher: Sha256Hasher {
    public init() {}

    public func sha256(_ bytes: [UInt8]) -> [UInt8] {
        Array(SHA256.hash(data: Data(bytes)))
    }
}

public enum SecureRandomError: Error, Equatable {
    case failed(OSStatus)
}

public enum SecureRandom {
    /// Cryptographically secure bytes from the system CSPRNG.
    public static func bytes(_ count: Int) throws -> [UInt8] {
        var buffer = [UInt8](repeating: 0, count: count)
        let status = buffer.withUnsafeMutableBytes { pointer in
            SecRandomCopyBytes(kSecRandomDefault, count, pointer.baseAddress!)
        }
        guard status == errSecSuccess else { throw SecureRandomError.failed(status) }
        return buffer
    }
}

/// A freshly minted token. The `token` string is shown to the operator **exactly once** and is
/// never persisted anywhere — only `material` (a salted digest) reaches disk.
public struct GeneratedToken: Sendable {
    public let token: String
    public let material: TokenMaterial
}

public enum TokenFactory {
    public static let prefix = "erdab_"
    public static let tokenByteCount = 32

    /// Generates a 256-bit token and the material that verifies it.
    ///
    /// The digest is a single SHA-256 over `salt ‖ token`, not a slow KDF. With 256 bits of
    /// entropy there is nothing to brute-force, and the digest is recomputed on *every* request
    /// including rate-limited ones — an Argon2 on that path would be a self-inflicted denial of
    /// service.
    public static func generate(
        now: Date,
        hasher: any Sha256Hasher = CryptoKitSha256Hasher()
    ) throws -> GeneratedToken {
        let secret = try SecureRandom.bytes(tokenByteCount)
        let salt = try SecureRandom.bytes(TokenMaterial.saltLength)
        let token = prefix + base64URL(secret)

        let tokenBytes = Array(token.utf8)
        guard let tokenId = TokenId(tokenDigest: hasher.sha256(tokenBytes)) else {
            throw TokenMaterialError.malformed
        }

        let material = try TokenMaterial(
            tokenId: tokenId,
            salt: salt,
            digest: hasher.sha256(TokenMaterial.digestPreimage(salt: salt, token: tokenBytes)),
            // Truncated to whole seconds because that is the granularity `TokenMaterial`
            // serialises to (dossier §5.2 stores `createdAt` as epoch seconds). Doing it here
            // means "generate, save, load" round-trips to an equal value for any caller,
            // instead of quietly differing by a fraction of a second.
            createdAt: Date(timeIntervalSince1970: now.timeIntervalSince1970.rounded(.down))
        )
        return GeneratedToken(token: token, material: material)
    }

    /// Unpadded base64url — safe in an HTTP header and in a shell single-quoted string, which
    /// is where this value spends its life (`.env` on `leela`, then an `Authorization` header).
    static func base64URL(_ bytes: [UInt8]) -> String {
        Data(bytes).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}
