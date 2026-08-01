import BridgeCore
import Foundation

/// The token digest in a 0600 file inside the 0700 application-support directory.
///
/// The honest counterpoint to the Keychain (dossier §5.4): what is stored is a **salted digest**,
/// not a secret. On a FileVault-encrypted disk, a 0600 file inside a 0700 directory protects it
/// about as well — and it has none of the ACL-prompt-at-login failure modes that can hang a
/// background app started from Login Items.
public struct FileTokenStore: TokenStore {
    private let url: URL

    public init(url: URL) {
        self.url = url
    }

    public var backendName: String { "file(\(url.lastPathComponent))" }

    public func load() throws -> TokenMaterial? {
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        return try JSONDecoder().decode(TokenMaterial.self, from: try Data(contentsOf: url))
    }

    public func save(_ material: TokenMaterial) throws {
        let data = try JSONEncoder().encode(material)
        try FilePermissions.createDirectory(at: url.deletingLastPathComponent())

        // Written to a sibling that is created 0600 from the start, then moved into place: an
        // atomic `Data.write(options: .atomic)` would leave the replacement file with the
        // default 0644 for the moment between rename and chmod.
        let temporary = url.deletingLastPathComponent()
            .appendingPathComponent(".\(url.lastPathComponent).new")
        try? FileManager.default.removeItem(at: temporary)
        guard FileManager.default.createFile(
            atPath: temporary.path,
            contents: data,
            attributes: [.posixPermissions: NSNumber(value: FilePermissions.file)]
        ) else {
            throw FileTokenStoreError.writeFailed
        }
        _ = try FileManager.default.replaceItemAt(url, withItemAt: temporary)
        try FilePermissions.hardenFile(at: url)
    }

    public func delete() throws {
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        try FileManager.default.removeItem(at: url)
    }
}

public enum FileTokenStoreError: Error, Equatable {
    case writeFailed
}
