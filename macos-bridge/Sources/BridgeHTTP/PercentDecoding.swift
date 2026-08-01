import Foundation

/// Percent-decoding for **query values only**.
///
/// The path is still never decoded — that is what keeps `%2e%2e` from becoming `..` — but a query
/// value now has to be, because a list is addressed by its real name and real names contain
/// spaces, umlauts and emoji. Decoding is confined here, to one function with one job, rather than
/// reached for with `URLComponents`: the router hand-parses the target precisely so there is no
/// second implementation of "what does this URL mean".
enum PercentDecoding {
    /// Decodes `%XX` escapes and nothing else.
    ///
    /// `nil` for a truncated or non-hex escape, and for bytes that are not valid UTF-8 — a bad
    /// escape is a malformed request, never a best-effort guess at what was meant.
    ///
    /// `+` is **not** translated to a space. That is an HTML form convention, not RFC 3986, and a
    /// list genuinely called "a+b" must not silently become "a b"; clients send `%20`.
    static func decode(_ text: Substring) -> String? {
        var bytes: [UInt8] = []
        bytes.reserveCapacity(text.utf8.count)

        var iterator = text.utf8.makeIterator()
        while let byte = iterator.next() {
            guard byte == UInt8(ascii: "%") else {
                bytes.append(byte)
                continue
            }
            guard let high = iterator.next().flatMap(hexDigit),
                  let low = iterator.next().flatMap(hexDigit)
            else { return nil }
            bytes.append(high << 4 | low)
        }

        // `String(bytes:encoding:)` answers nil on invalid UTF-8; `String(decoding:as:)` would
        // substitute replacement characters instead, turning mojibake into a plausible name.
        return String(bytes: bytes, encoding: .utf8)
    }

    private static func hexDigit(_ byte: UInt8) -> UInt8? {
        switch byte {
        case UInt8(ascii: "0")...UInt8(ascii: "9"): byte - UInt8(ascii: "0")
        case UInt8(ascii: "a")...UInt8(ascii: "f"): byte - UInt8(ascii: "a") + 10
        case UInt8(ascii: "A")...UInt8(ascii: "F"): byte - UInt8(ascii: "A") + 10
        default: nil
        }
    }
}
