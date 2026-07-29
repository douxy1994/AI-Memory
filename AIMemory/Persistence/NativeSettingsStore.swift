import Foundation

struct FavoriteConversationSnapshot: Codable, Hashable, Sendable {
    var id: String
    var sourceAgent: String
    var projectDir: String
    var title: String
    var createdAt: String
    var updatedAt: String
    var note: String
    var tags: [String]
    var pinned: Bool

    enum CodingKeys: String, CodingKey {
        case id, sourceAgent, projectDir, title, createdAt, updatedAt, note, tags, pinned
        case legacySourceAgent = "source_agent"
        case legacyProjectDir = "project_dir"
        case legacyCreatedAt = "created_at"
        case legacyUpdatedAt = "updated_at"
        case legacySummary = "summary"
    }

    init(
        id: String,
        sourceAgent: String,
        projectDir: String,
        title: String,
        createdAt: String,
        updatedAt: String,
        note: String,
        tags: [String],
        pinned: Bool
    ) {
        self.id = id
        self.sourceAgent = sourceAgent
        self.projectDir = projectDir
        self.title = title
        self.createdAt = createdAt
        self.updatedAt = updatedAt
        self.note = note
        self.tags = tags
        self.pinned = pinned
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(String.self, forKey: .id)
        sourceAgent = try container.decodeIfPresent(String.self, forKey: .sourceAgent)
            ?? container.decodeIfPresent(String.self, forKey: .legacySourceAgent)
            ?? ""
        projectDir = try container.decodeIfPresent(String.self, forKey: .projectDir)
            ?? container.decodeIfPresent(String.self, forKey: .legacyProjectDir)
            ?? ""
        title = try container.decodeIfPresent(String.self, forKey: .title)
            ?? container.decodeIfPresent(String.self, forKey: .legacySummary)
            ?? id
        createdAt = try container.decodeIfPresent(String.self, forKey: .createdAt)
            ?? container.decodeIfPresent(String.self, forKey: .legacyCreatedAt)
            ?? ""
        updatedAt = try container.decodeIfPresent(String.self, forKey: .updatedAt)
            ?? container.decodeIfPresent(String.self, forKey: .legacyUpdatedAt)
            ?? ""
        note = try container.decodeIfPresent(String.self, forKey: .note) ?? ""
        tags = try container.decodeIfPresent([String].self, forKey: .tags) ?? []
        pinned = try container.decodeIfPresent(Bool.self, forKey: .pinned) ?? false
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(id, forKey: .id)
        try container.encode(sourceAgent, forKey: .sourceAgent)
        try container.encode(projectDir, forKey: .projectDir)
        try container.encode(title, forKey: .title)
        try container.encode(createdAt, forKey: .createdAt)
        try container.encode(updatedAt, forKey: .updatedAt)
        try container.encode(note, forKey: .note)
        try container.encode(tags, forKey: .tags)
        try container.encode(pinned, forKey: .pinned)
    }
}

struct SyncPreferences: Codable, Hashable, Sendable {
    var provider = "off"
    var webdavScheme = "https"
    var webdavHost = ""
    var webdavPath = ""
    var username = ""
    var remotePath = "chatmem"
    var downloadMode = "on-sync"
    var syncFolder = ""

    enum CodingKeys: String, CodingKey {
        case provider, webdavScheme, webdavHost, webdavPath, username
        case remotePath, downloadMode, syncFolder
        case legacyWebdavScheme = "webdav_scheme"
        case legacyWebdavHost = "webdav_host"
        case legacyWebdavPath = "webdav_path"
        case legacyUsername = "webdav_username"
        case legacyRemotePath = "remote_path"
        case legacyDownloadMode = "download_mode"
        case legacySyncFolder = "sync_folder"
    }

    init() {}

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        provider = try container.decodeIfPresent(String.self, forKey: .provider) ?? "off"
        webdavScheme = try container.decodeIfPresent(String.self, forKey: .webdavScheme)
            ?? container.decodeIfPresent(String.self, forKey: .legacyWebdavScheme)
            ?? "https"
        webdavHost = try container.decodeIfPresent(String.self, forKey: .webdavHost)
            ?? container.decodeIfPresent(String.self, forKey: .legacyWebdavHost)
            ?? ""
        webdavPath = try container.decodeIfPresent(String.self, forKey: .webdavPath)
            ?? container.decodeIfPresent(String.self, forKey: .legacyWebdavPath)
            ?? ""
        username = try container.decodeIfPresent(String.self, forKey: .username)
            ?? container.decodeIfPresent(String.self, forKey: .legacyUsername)
            ?? ""
        remotePath = try container.decodeIfPresent(String.self, forKey: .remotePath)
            ?? container.decodeIfPresent(String.self, forKey: .legacyRemotePath)
            ?? "chatmem"
        downloadMode = try container.decodeIfPresent(String.self, forKey: .downloadMode)
            ?? container.decodeIfPresent(String.self, forKey: .legacyDownloadMode)
            ?? "on-sync"
        syncFolder = try container.decodeIfPresent(String.self, forKey: .syncFolder)
            ?? container.decodeIfPresent(String.self, forKey: .legacySyncFolder)
            ?? ""
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(provider, forKey: .provider)
        try container.encode(webdavScheme, forKey: .webdavScheme)
        try container.encode(webdavHost, forKey: .webdavHost)
        try container.encode(webdavPath, forKey: .webdavPath)
        try container.encode(username, forKey: .username)
        try container.encode(remotePath, forKey: .remotePath)
        try container.encode(downloadMode, forKey: .downloadMode)
        try container.encode(syncFolder, forKey: .syncFolder)
    }
}

struct AppPreferences: Codable, Hashable, Sendable {
    static let schemaVersion = 1
    static let defaultUpdateFeedURL =
        "https://api.github.com/repos/douxy1994/AI-Memory/releases/latest"

    var schemaVersion = Self.schemaVersion
    var locale = "zh-CN"
    var fontFamily = "system"
    var autoCheckUpdates = true
    var updateFeedURL = Self.defaultUpdateFeedURL
    var autoCaptureMemory = true
    var trashRetentionDays = 14
    var sync = SyncPreferences()
    var autoBackupEnabled = false
    var autoBackupIntervalMinutes = 30
    var favorites: [FavoriteConversationSnapshot] = []
    var machineGroupNames: [String: String] = [:]
    var machineGroupOverrides: [String: String] = [:]
    var favoriteConversations: [String: FavoriteConversationSnapshot] = [:]

    enum CodingKeys: String, CodingKey {
        case schemaVersion, locale, fontFamily, autoCheckUpdates, updateFeedURL, autoCaptureMemory
        case trashRetentionDays, sync, autoBackupEnabled, autoBackupIntervalMinutes
        case favorites, machineGroupNames, machineGroupOverrides, favoriteConversations
        case windowsSettingsVersion = "settingsVersion"
        case windowsLanguage = "language"
        case legacySchemaVersion = "schema_version"
        case legacyFontFamily = "font_family"
        case legacyAutoCheckUpdates = "auto_check_updates"
        case legacyUpdateFeedURL = "update_feed_url"
        case legacyAutoCaptureMemory = "auto_capture_memory"
        case legacyTrashRetentionDays = "trash_retention_days"
        case legacyAutoBackupEnabled = "auto_backup_enabled"
        case legacyAutoBackupIntervalMinutes = "auto_backup_interval_minutes"
        case legacyMachineGroupNames = "machine_group_names"
        case legacyMachineGroupOverrides = "machine_group_overrides"
        case legacyFavoriteConversations = "favorite_conversations"
    }

    init() {}

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        schemaVersion = try container.decodeIfPresent(Int.self, forKey: .schemaVersion)
            ?? container.decodeIfPresent(Int.self, forKey: .windowsSettingsVersion)
            ?? container.decodeIfPresent(Int.self, forKey: .legacySchemaVersion)
            ?? 0
        locale = try container.decodeIfPresent(String.self, forKey: .locale)
            ?? container.decodeIfPresent(String.self, forKey: .windowsLanguage)
            ?? "zh-CN"
        fontFamily = try container.decodeIfPresent(String.self, forKey: .fontFamily)
            ?? container.decodeIfPresent(String.self, forKey: .legacyFontFamily)
            ?? "system"
        autoCheckUpdates = try container.decodeIfPresent(Bool.self, forKey: .autoCheckUpdates)
            ?? container.decodeIfPresent(Bool.self, forKey: .legacyAutoCheckUpdates)
            ?? true
        updateFeedURL = try container.decodeIfPresent(String.self, forKey: .updateFeedURL)
            ?? container.decodeIfPresent(String.self, forKey: .legacyUpdateFeedURL)
            ?? Self.defaultUpdateFeedURL
        autoCaptureMemory = try container.decodeIfPresent(Bool.self, forKey: .autoCaptureMemory)
            ?? container.decodeIfPresent(Bool.self, forKey: .legacyAutoCaptureMemory)
            ?? true
        trashRetentionDays = try container.decodeIfPresent(Int.self, forKey: .trashRetentionDays)
            ?? container.decodeIfPresent(Int.self, forKey: .legacyTrashRetentionDays)
            ?? 14
        sync = try container.decodeIfPresent(SyncPreferences.self, forKey: .sync) ?? .init()
        autoBackupEnabled = try container.decodeIfPresent(Bool.self, forKey: .autoBackupEnabled)
            ?? container.decodeIfPresent(Bool.self, forKey: .legacyAutoBackupEnabled)
            ?? false
        autoBackupIntervalMinutes = try container.decodeIfPresent(
            Int.self,
            forKey: .autoBackupIntervalMinutes
        ) ?? container.decodeIfPresent(Int.self, forKey: .legacyAutoBackupIntervalMinutes) ?? 30
        favorites = try container.decodeIfPresent(
            [FavoriteConversationSnapshot].self,
            forKey: .favorites
        ) ?? []
        machineGroupNames = try container.decodeIfPresent(
            [String: String].self,
            forKey: .machineGroupNames
        ) ?? container.decodeIfPresent([String: String].self, forKey: .legacyMachineGroupNames) ?? [:]
        machineGroupOverrides = try container.decodeIfPresent(
            [String: String].self,
            forKey: .machineGroupOverrides
        ) ?? container.decodeIfPresent(
            [String: String].self,
            forKey: .legacyMachineGroupOverrides
        ) ?? [:]
        favoriteConversations = try container.decodeIfPresent(
            [String: FavoriteConversationSnapshot].self,
            forKey: .favoriteConversations
        ) ?? container.decodeIfPresent(
            [String: FavoriteConversationSnapshot].self,
            forKey: .legacyFavoriteConversations
        ) ?? [:]
        normalize()
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(schemaVersion, forKey: .schemaVersion)
        try container.encode(locale, forKey: .locale)
        try container.encode(fontFamily, forKey: .fontFamily)
        try container.encode(autoCheckUpdates, forKey: .autoCheckUpdates)
        try container.encode(updateFeedURL, forKey: .updateFeedURL)
        try container.encode(autoCaptureMemory, forKey: .autoCaptureMemory)
        try container.encode(trashRetentionDays, forKey: .trashRetentionDays)
        try container.encode(sync, forKey: .sync)
        try container.encode(autoBackupEnabled, forKey: .autoBackupEnabled)
        try container.encode(autoBackupIntervalMinutes, forKey: .autoBackupIntervalMinutes)
        try container.encode(favorites, forKey: .favorites)
        try container.encode(machineGroupNames, forKey: .machineGroupNames)
        try container.encode(machineGroupOverrides, forKey: .machineGroupOverrides)
        try container.encode(favoriteConversations, forKey: .favoriteConversations)
    }

    mutating func normalize() {
        locale = locale == "en" ? "en" : "zh-CN"
        if fontFamily == "sourceSans" { fontFamily = "source-sans" }
        if fontFamily == "sourceSerif" { fontFamily = "source-serif" }
        if updateFeedURL.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            updateFeedURL = Self.defaultUpdateFeedURL
        }
        trashRetentionDays = min(365, max(1, trashRetentionDays))
        autoBackupIntervalMinutes = max(5, autoBackupIntervalMinutes)
        if !["system", "source-sans", "source-serif", "wenkai"].contains(fontFamily) {
            fontFamily = "system"
        }
    }
}

enum NativeSettingsError: LocalizedError {
    case unsupportedSchema(Int)
    case invalidRoot

    var errorDescription: String? {
        switch self {
        case .unsupportedSchema(let version):
            "设置文件版本 \(version) 高于当前应用支持的版本。"
        case .invalidRoot:
            "设置文件必须是 JSON 对象。"
        }
    }
}

actor NativeSettingsStore {
    let url: URL

    init(url: URL = DataPaths.settingsURL) {
        self.url = url
    }

    func load() throws -> AppPreferences {
        guard FileManager.default.fileExists(atPath: url.path) else {
            return AppPreferences()
        }
        let data = try Data(contentsOf: url)
        var settings = try JSONDecoder().decode(AppPreferences.self, from: data)
        guard settings.schemaVersion <= AppPreferences.schemaVersion else {
            throw NativeSettingsError.unsupportedSchema(settings.schemaVersion)
        }
        settings.schemaVersion = AppPreferences.schemaVersion
        settings.normalize()
        return settings
    }

    func save(_ settings: AppPreferences) throws {
        var normalized = settings
        normalized.schemaVersion = AppPreferences.schemaVersion
        normalized.normalize()
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        let data = try encoder.encode(normalized)
        try data.write(to: url, options: [.atomic])
    }

    func loadDictionary() throws -> [String: Any] {
        let data = try JSONEncoder().encode(load())
        guard let dictionary = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw NativeSettingsError.invalidRoot
        }
        return dictionary
    }

    func saveDictionary(_ dictionary: [String: Any]) throws -> [String: Any] {
        let data = try JSONSerialization.data(withJSONObject: dictionary)
        let settings = try JSONDecoder().decode(AppPreferences.self, from: data)
        try save(settings)
        return try loadDictionary()
    }
}
