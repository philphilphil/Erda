import Foundation

/// Comparison that does not leak the position of the first differing byte through timing,
/// mirroring `crypto/subtle.ConstantTimeCompare` used by `whatsapp-bridge/send.go`.
public enum ConstantTime {
    /// Compares two byte buffers, **always reading both of them in full** — including when the
    /// lengths differ, where the naive `count` guard would return before touching any bytes and
    /// turn the length itself into a fast oracle.
    ///
    /// Generic over `RandomAccessCollection` so tests can pass an instrumented buffer and assert
    /// the read counts; every element access is O(1).
    public static func equal<L: RandomAccessCollection, R: RandomAccessCollection>(
        _ lhs: L,
        _ rhs: R
    ) -> Bool where L.Element == UInt8, R.Element == UInt8 {
        // Seeded with the length mismatch so a differing length can never compare equal, no
        // matter what the byte loop finds.
        var difference: UInt8 = lhs.count == rhs.count ? 0 : 1

        let steps = max(lhs.count, rhs.count)
        guard steps > 0 else { return difference == 0 }

        for step in 0..<steps {
            // Wrap the shorter side rather than short-circuiting, so the loop length depends only
            // on the (public) buffer lengths and never on their contents.
            let left = lhs.isEmpty ? 0 : lhs[lhs.index(lhs.startIndex, offsetBy: step % lhs.count)]
            let right = rhs.isEmpty ? 0 : rhs[rhs.index(rhs.startIndex, offsetBy: step % rhs.count)]
            difference |= left ^ right
        }

        return difference == 0
    }
}
