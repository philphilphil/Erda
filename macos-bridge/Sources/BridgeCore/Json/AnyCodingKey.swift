import Foundation

/// A `CodingKey` that can name any key, so a decoder can inspect `allKeys` and reject the
/// ones it does not know about. `Codable`'s generated conformance silently ignores unknown
/// keys; strict decoding is the deliberate opposite.
public struct AnyCodingKey: CodingKey, Hashable, Sendable {
    public let stringValue: String
    public let intValue: Int?

    public init(_ stringValue: String) {
        self.stringValue = stringValue
        self.intValue = nil
    }

    public init?(stringValue: String) {
        self.init(stringValue)
    }

    public init?(intValue: Int) {
        self.stringValue = String(intValue)
        self.intValue = intValue
    }
}

extension AnyCodingKey: ExpressibleByStringLiteral {
    public init(stringLiteral value: String) {
        self.init(value)
    }
}
