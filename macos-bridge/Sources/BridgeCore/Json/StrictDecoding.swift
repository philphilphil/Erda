import Foundation

/// Decoding helpers shared by every request DTO.
public enum StrictDecoding {
    /// Rejects any key the DTO does not declare. `Codable`'s generated `init(from:)` ignores
    /// extra keys, which would let a typo (`titel`) or a smuggled field pass silently.
    public static func rejectUnknownKeys(
        in container: KeyedDecodingContainer<AnyCodingKey>,
        allowed: Set<String>
    ) throws {
        for key in container.allKeys where !allowed.contains(key.stringValue) {
            throw ApiError.invalidRequest
        }
    }
}

/// Field-level validation. Every function throws a bare `ApiError` — no field name, no offending
/// value — because whatever it returned would end up in a response body.
public enum Validate {
    /// Trims surrounding whitespace and newlines, then enforces 1…512.
    public static func title(_ raw: String) throws -> String {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        let length = trimmed.unicodeScalars.count
        guard length >= Limits.titleMinLength, length <= Limits.titleMaxLength else {
            throw ApiError.invalidRequest
        }
        return trimmed
    }

    public static func notes(_ raw: String) throws -> String {
        guard raw.unicodeScalars.count <= Limits.notesMaxLength else { throw ApiError.invalidRequest }
        return raw
    }

    public static func priority(_ raw: Int) throws -> Int {
        guard Limits.priorityRange.contains(raw) else { throw ApiError.invalidRequest }
        return raw
    }

    public static func listLimit(_ raw: Int) throws -> Int {
        guard raw >= 1, raw <= Limits.listLimitMax else { throw ApiError.invalidRequest }
        return raw
    }

    /// Idempotency keys are opaque to the bridge, but they are stored, so they get a length and
    /// charset cap: printable ASCII only, so a key can never carry a control character into the
    /// database or the log.
    public static func idempotencyKey(_ raw: String) throws -> String {
        let scalars = Array(raw.unicodeScalars)
        guard (1...Limits.idempotencyKeyMaxLength).contains(scalars.count) else {
            throw ApiError.invalidRequest
        }
        for scalar in scalars where scalar.value < 0x21 || scalar.value > 0x7E {
            throw ApiError.invalidRequest
        }
        return raw
    }
}

/// The single entry point for decoding a request body.
public enum StrictJSON {
    /// A decoder whose date strategy is "ISO-8601 **with** an explicit offset, or fail".
    public static func decoder() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let text = try decoder.singleValueContainer().decode(String.self)
            guard let date = ISO8601.parseRequiringOffset(text) else { throw ApiError.invalidRequest }
            return date
        }
        return decoder
    }

    /// Decodes `data`, collapsing every failure — unknown key, missing field, wrong type,
    /// malformed JSON, a cap violation — to `ApiError.invalidRequest`. A `DecodingError`'s
    /// description names types and coding paths; none of that may reach the client.
    public static func decode<T: Decodable>(_ type: T.Type, from data: Data) throws -> T {
        do {
            return try decoder().decode(type, from: data)
        } catch let error as ApiError {
            throw error
        } catch {
            throw ApiError.invalidRequest
        }
    }
}

/// The encoder used for every response body.
public enum ResponseJSON {
    public static func encoder() -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.withoutEscapingSlashes]
        encoder.dateEncodingStrategy = .custom { date, encoder in
            var container = encoder.singleValueContainer()
            try container.encode(ISO8601.string(from: date))
        }
        return encoder
    }

    public static func encode(_ value: some Encodable) throws -> Data {
        try encoder().encode(value)
    }
}
