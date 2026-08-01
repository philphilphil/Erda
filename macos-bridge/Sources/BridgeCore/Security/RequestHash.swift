import Foundation

/// Produces SHA-256 **input**, not digests.
///
/// `BridgeCore` links nothing but Foundation, so the hashing itself belongs to the caller
/// (CryptoKit in `BridgeStore`/`BridgeHTTP`). What lives here is the part that has to be pinned
/// down and unit-testable: the exact byte layout that is hashed. Getting that layout wrong — for
/// example by concatenating without separators, so `POST /v1/a` + body `b` collides with
/// `POST /v1/ab` — is what would break idempotency, and it is testable without any crypto.
public enum RequestHash {
    /// `method ‖ 0x00 ‖ path ‖ 0x00 ‖ rawBodyBytes`.
    ///
    /// The body is the **raw** received bytes, never a re-encoded DTO: two byte sequences that
    /// decode to the same object must still be two different requests, or a client could retry
    /// with a reformatted body and silently bypass replay detection.
    ///
    /// `0x00` is the separator precisely because it cannot occur in an HTTP method or a
    /// request-target, so the encoding is unambiguous.
    public static func preimage(method: String, path: String, body: [UInt8]) -> [UInt8] {
        var bytes: [UInt8] = []
        bytes.reserveCapacity(method.utf8.count + path.utf8.count + body.count + 2)
        bytes.append(contentsOf: method.utf8)
        bytes.append(0x00)
        bytes.append(contentsOf: path.utf8)
        bytes.append(0x00)
        bytes.append(contentsOf: body)
        return bytes
    }
}

/// The SHA-256 seam. `BridgeCore` defines the shape; the implementation is injected.
public protocol Sha256Hasher: Sendable {
    func sha256(_ bytes: [UInt8]) -> [UInt8]
}
