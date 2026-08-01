import CryptoKit
import Foundation
import Testing

@testable import BridgeCore

/// The real hasher the app will inject in M2/M3. Using it here rather than a stub proves the
/// `Sha256Hasher` seam actually fits CryptoKit, without `BridgeCore` importing it.
struct CryptoKitHasher: Sha256Hasher {
    func sha256(_ bytes: [UInt8]) -> [UInt8] {
        Array(SHA256.hash(data: Data(bytes)))
    }
}

/// Counts element reads, so a test can assert `ConstantTime.equal` really walked both buffers.
final class ReadCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0

    var reads: Int {
        lock.lock()
        defer { lock.unlock() }
        return value
    }

    func bump() {
        lock.lock()
        defer { lock.unlock() }
        value += 1
    }
}

struct CountingBytes: RandomAccessCollection {
    let storage: [UInt8]
    let counter: ReadCounter

    var startIndex: Int { 0 }
    var endIndex: Int { storage.count }

    subscript(position: Int) -> UInt8 {
        counter.bump()
        return storage[position]
    }
}

// MARK: - Convenience constructors

func listName(_ raw: String, sourceLocation: SourceLocation = #_sourceLocation) throws -> ListName {
    try #require(ListName(rawValue: raw), sourceLocation: sourceLocation)
}

func calendarName(_ raw: String, sourceLocation: SourceLocation = #_sourceLocation) throws -> CalendarName {
    try #require(CalendarName(rawValue: raw), sourceLocation: sourceLocation)
}

func tokenMaterial(for token: String, salt: [UInt8]? = nil) throws -> TokenMaterial {
    let hasher = CryptoKitHasher()
    let saltBytes = salt ?? Array(repeating: 0xA5, count: TokenMaterial.saltLength)
    let digest = hasher.sha256(TokenMaterial.digestPreimage(salt: saltBytes, token: Array(token.utf8)))
    let id = try #require(TokenId(tokenDigest: hasher.sha256(Array(token.utf8))))
    return try TokenMaterial(
        tokenId: id,
        salt: saltBytes,
        digest: digest,
        createdAt: Date(timeIntervalSince1970: 1_780_000_000)
    )
}

func json(_ text: String) -> Data {
    Data(text.utf8)
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
