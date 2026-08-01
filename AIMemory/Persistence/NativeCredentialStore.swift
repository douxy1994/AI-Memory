// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import Foundation
import Security

/// Password storage isolated under AI Memory's own Keychain service.
actor NativeCredentialStore {
    private let service: String

    init(service: String = DataPaths.keychainService) {
        self.service = service
    }

    func save(password: String, account: String) throws {
        let encodedAccount = account.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !encodedAccount.isEmpty else {
            throw NativeCredentialError.emptyAccount
        }
        let passwordData = Data(password.utf8)
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: encodedAccount,
        ]
        let attributes: [CFString: Any] = [
            kSecValueData: passwordData,
            kSecAttrAccessible: kSecAttrAccessibleAfterFirstUnlock,
        ]
        let updateStatus = SecItemUpdate(
            query as CFDictionary,
            attributes as CFDictionary
        )
        if updateStatus == errSecSuccess { return }
        guard updateStatus == errSecItemNotFound else {
            throw NativeCredentialError.keychain(updateStatus)
        }
        var insert = query
        attributes.forEach { insert[$0.key] = $0.value }
        let addStatus = SecItemAdd(insert as CFDictionary, nil)
        guard addStatus == errSecSuccess else {
            throw NativeCredentialError.keychain(addStatus)
        }
    }

    func load(account: String) throws -> String? {
        let encodedAccount = account.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !encodedAccount.isEmpty else { return nil }
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: encodedAccount,
            kSecReturnData: true,
            kSecMatchLimit: kSecMatchLimitOne,
        ]
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data else {
            throw NativeCredentialError.keychain(status)
        }
        guard let password = String(data: data, encoding: .utf8) else {
            throw NativeCredentialError.invalidEncoding
        }
        return password
    }
}

enum NativeCredentialError: LocalizedError {
    case emptyAccount
    case invalidEncoding
    case keychain(OSStatus)

    var errorDescription: String? {
        switch self {
        case .emptyAccount:
            "钥匙串账户名不能为空。"
        case .invalidEncoding:
            "钥匙串中的密码不是有效 UTF-8。"
        case .keychain(let status):
            if let message = SecCopyErrorMessageString(status, nil) as String? {
                "钥匙串操作失败：\(message)"
            } else {
                "钥匙串操作失败（\(status)）。"
            }
        }
    }
}
