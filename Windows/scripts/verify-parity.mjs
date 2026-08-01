// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import fs from "node:fs";
import path from "node:path";

const root = path.resolve(import.meta.dirname, "../..");
const parityPath = path.join(root, "Windows", "parity.json");
const parity = JSON.parse(fs.readFileSync(parityPath, "utf8"));

const source = (files, patterns) => ({ files, patterns });
const contracts = {
  "native-ui": source(
    ["Windows/src/AIMemory.Windows/AIMemory.Windows.csproj"],
    [/<UseWinUI>true<\/UseWinUI>/, /Microsoft\.WindowsAppSDK/],
  ),
  "single-instance": source(
    ["Windows/src/AIMemory.Windows/Program.cs"],
    [/FindOrRegisterForKey\("AIMemory\.Main"\)/, /RedirectActivationToAsync/],
  ),
  "notification-area-lifecycle": source(
    [
      "Windows/src/AIMemory.Windows/Services/NotificationAreaService.cs",
      "Windows/src/AIMemory.Windows/MainWindow.xaml.cs",
    ],
    [/Shell_NotifyIcon/, /OnAppWindowClosing/, /sender\.Hide\(\)/],
  ),
  "launch-at-login": source(
    [
      "Windows/src/AIMemory.Windows/Services/StartupService.cs",
      "Windows/src/AIMemory.Windows/Package.appxmanifest",
    ],
    [/StartupTask\.GetAsync/, /windows\.startupTask/, /AIMemoryStartup/],
  ),
  "independent-data": source(
    ["Windows/src/AIMemory.Core/Persistence/DataPaths.cs"],
    [/LocalApplicationData/, /AIMemory/],
  ),
  "database-schema": source(
    [
      "Windows/src/AIMemory.Core/Persistence/SchemaV1.sql",
      "Windows/src/AIMemory.Core/Persistence/AIMemoryDatabase.cs",
    ],
    [/CREATE TABLE/, /InitializeAsync/],
  ),
  "settings-persistence": source(
    [
      "Windows/src/AIMemory.Core/Persistence/SettingsStore.cs",
      "Windows/src/AIMemory.Core/Models/AppSettings.cs",
    ],
    [/SaveAsync/, /File\.Move\(temporary, _path, true\)/, /Normalize\(\)/],
  ),
  workbench: source(
    [
      "Windows/src/AIMemory.Windows/Pages/WorkbenchPage.xaml",
      "Windows/src/AIMemory.Windows/Pages/WorkbenchPage.xaml.cs",
    ],
    [/WorkbenchPage/, /ReloadAsync/, /ProjectList/],
  ),
  "history-search": source(
    [
      "Windows/src/AIMemory.Windows/Pages/HistoryPage.xaml",
      "Windows/src/AIMemory.Windows/Pages/HistoryPage.xaml.cs",
    ],
    [/HistoryPage/, /Search/, /ConversationListProjectionService/],
  ),
  "conversation-reader": source(
    [
      "Windows/src/AIMemory.Windows/Pages/ConversationPage.xaml",
      "Windows/src/AIMemory.Windows/Pages/ConversationPage.xaml.cs",
    ],
    [/ConversationPage/, /LoadDetailAsync/, /MessageList/],
  ),
  "approved-memory-reader": source(
    [
      "Windows/src/AIMemory.Windows/Pages/MemoryPage.xaml.cs",
      "Windows/src/AIMemory.Core/Services/MemoryGovernanceService.cs",
    ],
    [/ApproveCandidateAsync/, /ListApprovedAsync/, /ApprovedList/],
  ),
  favorites: source(
    [
      "Windows/src/AIMemory.Windows/Pages/FavoritesPage.xaml.cs",
      "Windows/src/AIMemory.Core/Services/FavoriteService.cs",
    ],
    [/FavoritesPage/, /TogglePin_Click/, /SaveMetadata_Click/],
  ),
  "trash-restore-delete": source(
    [
      "Windows/src/AIMemory.Windows/Pages/TrashPage.xaml.cs",
      "Windows/src/AIMemory.Core/Services/TrashService.cs",
    ],
    [/TrashPage/, /RestoreAsync/, /DeleteAsync/, /EmptyAsync/],
  ),
  "agent-catalog": source(
    ["Windows/src/AIMemory.Core/Services/AgentCatalog.cs"],
    [/AgentDescriptor/, /AgentCatalog/, /public static IReadOnlyList<AgentDescriptor> All/],
  ),
  "agent-integration": source(
    [
      "Windows/src/AIMemory.Core/Services/AgentIntegrationManager.cs",
      "Windows/src/AIMemory.Windows/Services/AgentIntegrationService.cs",
    ],
    [/SetEnabled/, /SupportsAutomaticIntegration/, /Detect\(\)/],
  ),
  "credential-storage": source(
    ["Windows/src/AIMemory.Windows/Services/CredentialService.cs"],
    [/PasswordVault/, /PasswordCredential/],
  ),
  "webdav-verification": source(
    ["Windows/src/AIMemory.Core/Services/WebDavService.cs"],
    [/VerifyAsync/, /new HttpMethod\("PROPFIND"\)/],
  ),
  backup: source(
    ["Windows/src/AIMemory.Core/Services/BackupService.cs"],
    [/CreateRecoveryPointDetailedAsync/, /ValidateAsync/, /CreateHardLink/],
  ),
  "chatmem-database-import": source(
    ["Windows/src/AIMemory.Core/Services/ChatMemImportService.cs"],
    [/OnlineBackupAsync/, /integrity_check/, /ImportGate/],
  ),
  "chatmem-webdav-import": source(
    ["Windows/src/AIMemory.Core/Services/ChatMemWebDavImportService.cs"],
    [/ImportAsync/, /loadLegacyCredential/, /different_endpoint_configured/],
  ),
  "incremental-webdav-sync": source(
    ["Windows/src/AIMemory.Core/Services/WebDavService.cs"],
    [/SemanticDigest/, /SyncProgress/, /skipped/i],
  ),
  "local-folder-incremental-sync": source(
    ["Windows/src/AIMemory.Core/Services/LocalFolderSyncService.cs"],
    [/LayoutSchemaVersion/, /AtomicWriteAsync/, /SemanticHash/],
  ),
  "native-source-history-import": source(
    ["Windows/src/AIMemory.Core/Services/NativeHistoryImportService.cs"],
    [/ImportAllAsync/, /ImportCodexAsync/, /ImportClaudeAsync/],
  ),
  "native-conversation-migration": source(
    ["Windows/src/AIMemory.Core/Services/ConversationMigrationService.cs"],
    [/MigrateAsync/, /RestoreSourceArchiveAsync/, /ConversationMigrationResult/],
  ),
  "memory-review-editing": source(
    [
      "Windows/src/AIMemory.Core/Services/MemoryGovernanceService.cs",
      "Windows/src/AIMemory.Windows/Pages/MemoryPage.xaml.cs",
    ],
    [/ReviewCandidateAsync/, /UpdateApprovedAsync/, /RejectCandidate_Click/],
  ),
  "checkpoint-handoff-actions": source(
    ["Windows/src/AIMemory.Core/Services/RecoveryService.cs"],
    [/CreateCheckpointAsync/, /CreateHandoffAsync/, /MarkHandoffConsumedAsync/],
  ),
  "automatic-memory-capture": source(
    [
      "Windows/src/AIMemory.Core/Services/AutomaticCaptureService.cs",
      "Windows/src/AIMemory.Windows/Pages/ConversationPage.xaml.cs",
    ],
    [/CaptureAsync/, /UpsertAutomaticCheckpointAsync/, /StartAutomaticCapture/],
  ),
  "mcp-tool-contract": source(
    [
      "Windows/src/AIMemory.Mcp/Program.cs",
      "Windows/src/AIMemory.Windows/Services/AgentIntegrationService.cs",
    ],
    [/tools\/list|tools\/call/, /detect_agent_integrations/],
  ),
  "menu-and-shortcut-parity": source(
    [
      "Windows/src/AIMemory.Windows/MainWindow.xaml",
      "Windows/src/AIMemory.Windows/MainWindow.xaml.cs",
    ],
    [/<MenuBar/, /KeyboardAcceleratorTextOverride/, /RegisterAccelerators/],
  ),
  "settings-navigation": source(
    [
      "Windows/src/AIMemory.Windows/Pages/SettingsPage.xaml",
      "Windows/src/AIMemory.Windows/Pages/SettingsPage.xaml.cs",
    ],
    [/SettingsCategories/, /ShowCategory/, /SyncPanel/],
  ),
  "localized-ui": source(
    [
      "Windows/src/AIMemory.Windows/Strings/zh-CN/Resources.resw",
      "Windows/src/AIMemory.Windows/Strings/en-US/Resources.resw",
      "Windows/src/AIMemory.Windows/Services/LocalizationService.cs",
    ],
    [/Settings/, /About/, /Get\(/],
  ),
  "font-preferences": source(
    [
      "Windows/src/AIMemory.Core/Services/FontPreferenceService.cs",
      "Windows/src/AIMemory.Windows/MainWindow.xaml.cs",
    ],
    [/ResolveWindowsFamily/, /ApplyFontFamily/],
  ),
  "update-and-diagnostics": source(
    [
      "Windows/src/AIMemory.Core/Services/UpdateService.cs",
      "Windows/src/AIMemory.Core/Services/DiagnosticsService.cs",
      "Windows/src/AIMemory.Windows/AboutWindow.xaml.cs",
    ],
    [/CheckAsync/, /CheckForUpdatesAsync/, /DiagnosticsService/],
  ),
  "upgrade-readiness": source(
    [
      "Windows/src/AIMemory.Core/Services/UpgradeReadinessService.cs",
      "Windows/src/AIMemory.Windows/Pages/SettingsPage.xaml.cs",
    ],
    [/UpgradeReadinessReport/, /quick_check/, /RunReadinessButton/],
  ),
};

const readContract = (id, contract) => {
  const contents = contract.files.map((relative) => {
    const absolute = path.join(root, relative);
    if (!fs.existsSync(absolute)) {
      throw new Error(`${id}: source file is missing: ${relative}`);
    }
    return fs.readFileSync(absolute, "utf8");
  }).join("\n");
  for (const pattern of contract.patterns) {
    if (!pattern.test(contents)) {
      throw new Error(`${id}: source contract is missing ${pattern}`);
    }
  }
};

const implemented = parity.features.filter(
  (feature) => feature.status === "implemented",
);
const pending = parity.features.filter(
  (feature) => feature.status !== "implemented",
);
for (const feature of implemented) {
  const contract = contracts[feature.id];
  if (!contract) {
    throw new Error(`No concrete source contract is registered for ${feature.id}`);
  }
  readContract(feature.id, contract);
}

const expectedPending = new Set([
  "x64-arm64-builds",
  "mcp-runtime-smoke",
  "desktop-lifecycle-smoke",
]);
const actualPending = new Set(pending.map((feature) => feature.id));
if (actualPending.size !== expectedPending.size
    || [...expectedPending].some((id) => !actualPending.has(id))) {
  throw new Error(
    `Unexpected parity gate set: ${[...actualPending].join(",")}`,
  );
}

console.log(
  `Parity source contract verified: ${implemented.length} implemented features ` +
    `have concrete Windows source markers; ${pending.length} runtime gates remain.`,
);
