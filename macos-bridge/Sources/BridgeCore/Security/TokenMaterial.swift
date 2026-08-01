import Foundation

/// The public half of the API token: the first four bytes of `SHA-256(token)`, hex.
///
/// It is what rate limiting and the audit log are keyed on — enough to correlate requests,
/// useless for authenticating.
public struct TokenId: Sendable, Hashable, Codable, RawRepresentable, CustomStringConvertible {
    public static let length = 8

    public let rawValue: String

    public init?(rawValue: String) {
        let scalars = Array(rawValue.unicodeScalars)
        guard scalars.count == TokenId.length else { return nil }
        for scalar in scalars where !(("0"..."9").contains(scalar) || ("a"..."f").contains(scalar)) {
            return nil
        }
        self.rawValue = rawValue
    }

    /// Derives the id from a full `SHA-256(token)` digest.
    public init?(tokenDigest: [UInt8]) {
        guard tokenDigest.count >= 4 else { return nil }
        self.init(rawValue: tokenDigest.prefix(4).map { String(format: "%02x", $0) }.joined())
    }

    public var description: String { rawValue }
}

/// What the bridge persists about its token: a salted digest, never the token itself.
///
/// A slow KDF would be the wrong tool — the token has 256 bits of entropy, so there is nothing to
/// brute-force, and it is verified on every request including the rate-limited path.
public struct TokenMaterial: Sendable, Equatable, Codable {
    public static let currentVersion = 1
    public static let saltLength = 16
    public static let digestLength = 32

    public let version: Int
    public let tokenId: TokenId
    public let salt: [UInt8]
    public let digest: [UInt8]
    public let createdAt: Date

    public init(
        version: Int = TokenMaterial.currentVersion,
        tokenId: TokenId,
        salt: [UInt8],
        digest: [UInt8],
        createdAt: Date
    ) throws {
        guard version == TokenMaterial.currentVersion,
              salt.count == TokenMaterial.saltLength,
              digest.count == TokenMaterial.digestLength
        else { throw TokenMaterialError.malformed }
        self.version = version
        self.tokenId = tokenId
        self.salt = salt
        self.digest = digest
        self.createdAt = createdAt
    }

    /// The bytes that are hashed to produce `digest`: `salt ‖ token`.
    public static func digestPreimage(salt: [UInt8], token: [UInt8]) -> [UInt8] {
        var bytes: [UInt8] = []
        bytes.reserveCapacity(salt.count + token.count)
        bytes.append(contentsOf: salt)
        bytes.append(contentsOf: token)
        return bytes
    }

    // Serialised exactly as the Keychain item's value (design dossier §5.2):
    // {"v":1,"tokenId":"…","salt":"<b64>","digest":"<b64>","createdAt":<epochSeconds>}
    private enum CodingKeys: String, CodingKey {
        case version = "v"
        case tokenId
        case salt
        case digest
        case createdAt
    }

    public init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let saltData = Data(base64Encoded: try container.decode(String.self, forKey: .salt))
        let digestData = Data(base64Encoded: try container.decode(String.self, forKey: .digest))
        guard let saltData, let digestData else { throw TokenMaterialError.malformed }
        try self.init(
            version: try container.decode(Int.self, forKey: .version),
            tokenId: try container.decode(TokenId.self, forKey: .tokenId),
            salt: [UInt8](saltData),
            digest: [UInt8](digestData),
            createdAt: Date(timeIntervalSince1970: try container.decode(Double.self, forKey: .createdAt))
        )
    }

    public func encode(to encoder: any Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(version, forKey: .version)
        try container.encode(tokenId, forKey: .tokenId)
        try container.encode(Data(salt).base64EncodedString(), forKey: .salt)
        try container.encode(Data(digest).base64EncodedString(), forKey: .digest)
        try container.encode(Int(createdAt.timeIntervalSince1970), forKey: .createdAt)
    }
}

public enum TokenMaterialError: Error, Equatable, Sendable {
    case malformed
}

/// The verification entry point. Hashing is injected so `BridgeCore` needs no crypto framework.
public struct TokenVerifier: Sendable {
    private let material: TokenMaterial
    private let hasher: any Sha256Hasher

    public init(material: TokenMaterial, hasher: any Sha256Hasher) {
        self.material = material
        self.hasher = hasher
    }

    /// Returns the token id on success, `nil` on any failure.
    ///
    /// There is no early exit for an empty or wrong-length token: every presented value is hashed
    /// and compared in full, so failure costs the same regardless of how wrong it was.
    public func verify(presentedToken: String) -> TokenId? {
        let computed = hasher.sha256(
            TokenMaterial.digestPreimage(salt: material.salt, token: Array(presentedToken.utf8))
        )
        return ConstantTime.equal(computed, material.digest) ? material.tokenId : nil
    }

    /// Extracts the credential from an `Authorization` header value, or `nil` if the scheme is
    /// wrong or absent. Split out so the header parsing is testable on its own.
    public static func bearerToken(from headerValue: String?) -> String? {
        guard let headerValue else { return nil }
        let scheme = "Bearer "
        guard headerValue.count > scheme.count,
              headerValue.prefix(scheme.count).caseInsensitiveCompare(scheme) == .orderedSame
        else { return nil }
        return String(headerValue.dropFirst(scheme.count))
    }
}
