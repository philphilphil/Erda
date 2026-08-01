import Foundation
import Testing

@testable import BridgeCore

@Suite("Bridge ids")
struct BridgeIDTests {
    @Test("generated ids are well-formed and unique")
    func generates() {
        let first = BridgeID.generate()
        let second = BridgeID.generate()
        #expect(first != second)
        #expect(first.rawValue.hasPrefix("rem_"))
        #expect(first.rawValue.count == 4 + 36)
        #expect(BridgeID(rawValue: first.rawValue) == first)
    }

    @Test("a generated id is already lowercase")
    func generatesLowercase() {
        let id = BridgeID.generate()
        #expect(id.rawValue == id.rawValue.lowercased())
    }

    @Test("an uppercase uuid is accepted and normalised, so it can be used as a storage key")
    func normalisesCase() throws {
        let lower = try #require(BridgeID(rawValue: "rem_6f0c1b6e-1f4a-4a9d-9f3e-1b2c3d4e5f60"))
        let upper = try #require(BridgeID(rawValue: "rem_6F0C1B6E-1F4A-4A9D-9F3E-1B2C3D4E5F60"))
        #expect(lower == upper)
        #expect(upper.rawValue == "rem_6f0c1b6e-1f4a-4a9d-9f3e-1b2c3d4e5f60")
    }

    @Test("malformed ids are rejected", arguments: [
        "",
        "rem_",
        "6f0c1b6e-1f4a-4a9d-9f3e-1b2c3d4e5f60",                  // no prefix
        "REM_6f0c1b6e-1f4a-4a9d-9f3e-1b2c3d4e5f60",              // wrong-case prefix
        "rem-6f0c1b6e-1f4a-4a9d-9f3e-1b2c3d4e5f60",              // wrong separator
        "rem_6f0c1b6e1f4a4a9d9f3e1b2c3d4e5f60",                  // unhyphenated
        "rem_6f0c1b6e-1f4a-4a9d-9f3e-1b2c3d4e5f6",               // one short
        "rem_6f0c1b6e-1f4a-4a9d-9f3e-1b2c3d4e5f600",             // one long
        "rem_6f0c1b6e-1f4a-4a9d-9f3e-1b2c3d4e5f60 ",             // trailing space
        "rem_6f0c1b6e-1f4a-4a9d-9f3e-1b2c3d4e5f60/../status",    // path traversal attempt
        "rem_zzzzzzzz-1f4a-4a9d-9f3e-1b2c3d4e5f60",              // non-hex
        "rem_../../etc/passwd",
    ])
    func rejectsMalformed(candidate: String) {
        #expect(BridgeID(rawValue: candidate) == nil, "\(candidate) should not parse")
    }

    @Test("ids decode strictly and encode as their bare string")
    func codesAsString() throws {
        let decoded = try StrictJSON.decode(
            CompleteOutcome.self,
            from: json(#"{"id":"rem_6f0c1b6e-1f4a-4a9d-9f3e-1b2c3d4e5f60","alreadyCompleted":true}"#)
        )
        #expect(decoded.id.rawValue == "rem_6f0c1b6e-1f4a-4a9d-9f3e-1b2c3d4e5f60")
        #expect(decoded.alreadyCompleted)

        let encoded = String(decoding: try ResponseJSON.encode(decoded), as: UTF8.self)
        #expect(encoded.contains(#""id":"rem_6f0c1b6e-1f4a-4a9d-9f3e-1b2c3d4e5f60""#))
    }

    @Test("a malformed id in a body is a plain invalid_request")
    func rejectsMalformedIdInBody() {
        #expect(throws: ApiError.invalidRequest) {
            try StrictJSON.decode(
                CompleteOutcome.self,
                from: json(#"{"id":"rem_nope","alreadyCompleted":false}"#)
            )
        }
    }
}
