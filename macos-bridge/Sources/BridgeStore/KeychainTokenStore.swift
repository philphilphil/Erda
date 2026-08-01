import BridgeCore
import Foundation
import Security

public struct KeychainError: Error, Equatable, CustomStringConvertible {
    public let status: OSStatus
    public let operation: String

    public var message: String {
        SecCopyErrorMessageString(status, nil).map { $0 as String } ?? "unknown error"
    }

    public var description: String {
        "keychain \(operation) failed: \(status) (\(message))"
    }

    /// -34018. The symptom of asking for the data-protection keychain without the entitlement
    /// that grants an access group — see the note on `KeychainTokenStore`.
    public var isMissingEntitlement: Bool { status == errSecMissingEntitlement }
}

/// The token digest in the **legacy** (file-based `login.keychain-db`) keychain.
///
/// Three attributes are conspicuously absent, and their absence is the design:
///
/// - **`kSecUseDataProtectionKeychain`** — `SecItem.h` says it exists so that
///   `kSecAttrAccessGroup` / `kSecAttrAccessible` work on macOS, and access groups derive from
///   the app's `application-identifier` / `keychain-access-groups` entitlements. There is no
///   provisioning profile on this machine, `keychain-access-groups` is a restricted entitlement
///   that needs one, and codesign would happily embed it only for the OS to reject the app at
///   launch. Asking for it yields `errSecMissingEntitlement` (-34018).
/// - **`kSecAttrAccessGroup`** — same reason.
/// - **`kSecAttrSynchronizable`** — setting it silently migrates the item into the
///   data-protection keychain and syncs a credential-shaped item to iCloud. Omitting it also
///   means queries match non-synchronizable items only, which is what we want.
///
/// The legacy keychain works for any signed, non-sandboxed app with no entitlements at all,
/// which is exactly what this bundle is.
///
/// **The ACL is bound to the code signature**, so a round trip here cannot be meaningfully
/// tested from `swift test` — the test host is not the signed bundle. `StoreSelfTest` exercises
/// it from inside the app instead.
public struct KeychainTokenStore: TokenStore {
    public static let defaultService = BridgeDirectories.bundleIdentifier
    public static let defaultAccount = "api-token"

    private let service: String
    private let account: String

    /// - Parameter service: overridable so the self-test can use its own item and never touch
    ///   the production token.
    public init(service: String = KeychainTokenStore.defaultService, account: String = KeychainTokenStore.defaultAccount) {
        self.service = service
        self.account = account
    }

    public var backendName: String { "keychain(\(service)/\(account))" }

    private var baseQuery: [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
    }

    public func load() throws -> TokenMaterial? {
        var query = baseQuery
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne

        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess else { throw KeychainError(status: status, operation: "copyMatching") }
        guard let data = result as? Data else {
            throw KeychainError(status: errSecInvalidData, operation: "copyMatching")
        }
        return try JSONDecoder().decode(TokenMaterial.self, from: data)
    }

    public func save(_ material: TokenMaterial) throws {
        let data = try JSONEncoder().encode(material)

        let updateStatus = SecItemUpdate(
            baseQuery as CFDictionary,
            [kSecValueData as String: data] as CFDictionary
        )
        if updateStatus == errSecSuccess { return }
        guard updateStatus == errSecItemNotFound else {
            throw KeychainError(status: updateStatus, operation: "update")
        }

        var attributes = baseQuery
        attributes[kSecValueData as String] = data
        attributes[kSecAttrLabel as String] = "ErdaBridge API token digest"
        attributes[kSecAttrDescription as String] = "digest only — the token itself is not stored"
        let addStatus = SecItemAdd(attributes as CFDictionary, nil)
        guard addStatus == errSecSuccess else {
            throw KeychainError(status: addStatus, operation: "add")
        }
    }

    public func delete() throws {
        let status = SecItemDelete(baseQuery as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw KeychainError(status: status, operation: "delete")
        }
    }
}
