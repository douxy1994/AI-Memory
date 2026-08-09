// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using AIMemory.Core.Models;
using AIMemory.Core.Persistence;
using AIMemory.Core.Services;
using System.Security.Cryptography;

namespace AIMemory.Windows.Services;

public sealed class AgentIntegrationService
{
    private readonly AgentIntegrationManager _manager = new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        DeployHelper());

    public IReadOnlyList<AgentIntegrationStatus> Detect() =>
        _manager.Detect();

    public void SetEnabled(AgentIntegrationStatus status, bool enabled) =>
        _manager.SetEnabled(status, enabled);

    private static string DeployHelper()
    {
        var packaged = Path.Combine(
            AppContext.BaseDirectory,
            "Helpers",
            "aimemory-mcp.exe");
        if (!File.Exists(packaged)) return packaged;
        try
        {
            Directory.CreateDirectory(DataPaths.HelperDirectory);
            if (!File.Exists(DataPaths.McpHelperPath)
                || !FilesMatch(packaged, DataPaths.McpHelperPath))
            {
                File.Copy(packaged, DataPaths.McpHelperPath, overwrite: true);
            }
            return DataPaths.McpHelperPath;
        }
        catch (IOException)
        {
            // A helper already serving an Agent can temporarily hold the
            // durable executable open. Keep existing integrations operational
            // and retry deployment the next time Settings is opened.
            return File.Exists(DataPaths.McpHelperPath)
                ? DataPaths.McpHelperPath
                : packaged;
        }
        catch (UnauthorizedAccessException)
        {
            return packaged;
        }
    }

    private static bool FilesMatch(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length) return false;
        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        return SHA256.HashData(leftStream).SequenceEqual(
            SHA256.HashData(rightStream));
    }
}
