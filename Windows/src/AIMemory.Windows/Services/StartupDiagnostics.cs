// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using System.Text;
using AIMemory.Core.Persistence;

namespace AIMemory.Windows.Services;

/// <summary>
/// Small, local-only startup trace used to diagnose failures before the first
/// WinUI window is visible.  It never records credentials or conversation data.
/// </summary>
internal static class StartupDiagnostics
{
    private static readonly object Gate = new();

    public static string LogPath =>
        Path.Combine(DataPaths.SupportDirectory, "startup.log");

    public static void Reset()
    {
        try
        {
            DataPaths.EnsureDirectories();
            File.WriteAllText(LogPath, string.Empty, Encoding.UTF8);
        }
        catch
        {
            // Diagnostics must never prevent the application from launching.
        }
    }

    public static void Write(string stage, Exception? exception = null)
    {
        try
        {
            DataPaths.EnsureDirectories();
            var line = new StringBuilder()
                .Append(DateTimeOffset.UtcNow.ToString("O"))
                .Append(" ")
                .Append(stage);
            if (exception is not null)
            {
                line.Append(" ")
                    .Append(exception.GetType().FullName)
                    .Append(": ")
                    .Append(exception.Message);
            }
            line.AppendLine();
            lock (Gate)
            {
                File.AppendAllText(LogPath, line.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never prevent the application from launching.
        }
    }
}
