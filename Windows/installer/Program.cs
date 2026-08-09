// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace AIMemory.Setup;

internal static class Program
{
    private const string PackageName = "com.aimemory.windows";
    private const string DisplayName = "AI Memory";
    private const string Version = "0.1.3";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(
        nint hWnd,
        string text,
        string caption,
        uint type);

    private static int Main()
    {
        try
        {
            Install();
            MessageBox(
                0,
                $"{DisplayName} {Version} 已安装并启动。",
                $"{DisplayName} 安装完成",
                0x40);
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox(
                0,
                $"{DisplayName} 安装失败：\n\n{exception.Message}",
                $"{DisplayName} 安装程序",
                0x10);
            return 1;
        }
    }

    private static void Install()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("无法定位当前用户的本地应用数据目录。");
        }

        var installRoot = Path.Combine(
            localAppData,
            "Programs",
            "AI Memory");
        var installDirectory = Path.Combine(installRoot, Version);
        var stagingDirectory = Path.Combine(
            installRoot,
            $"{Version}.staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(installRoot);

        var payloadPath = Path.Combine(
            Path.GetTempPath(),
            $"aimemory-{Guid.NewGuid():N}.msix");
        try
        {
            RemoveExistingPackage();
            ExtractEmbeddedPayload(payloadPath, stagingDirectory);
            ValidatePackageLayout(stagingDirectory);

            if (Directory.Exists(installDirectory))
            {
                Directory.Delete(installDirectory, recursive: true);
            }
            Directory.Move(stagingDirectory, installDirectory);
            RegisterAndLaunch(Path.Combine(installDirectory, "AppxManifest.xml"));
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
            throw;
        }
        finally
        {
            if (File.Exists(payloadPath))
            {
                File.Delete(payloadPath);
            }
        }
    }

    private static void ExtractEmbeddedPayload(
        string payloadPath,
        string stagingDirectory)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var payload = assembly.GetManifestResourceStream(
            "AIMemory.Windows.msix")
            ?? throw new InvalidOperationException(
                "安装程序缺少 Windows 应用负载。");
        using (var output = File.Create(payloadPath))
        {
            payload.CopyTo(output);
        }

        Directory.CreateDirectory(stagingDirectory);
        ZipFile.ExtractToDirectory(payloadPath, stagingDirectory);
    }

    private static void ValidatePackageLayout(string packageDirectory)
    {
        var manifestPath = Path.Combine(packageDirectory, "AppxManifest.xml");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                "应用负载缺少 AppxManifest.xml。");
        }

        var manifest = File.ReadAllText(manifestPath, Encoding.UTF8);
        if (!manifest.Contains(
                "Identity Name=\"com.aimemory.windows\"",
                StringComparison.Ordinal)
            || !manifest.Contains(
                "Version=\"0.1.3.0\"",
                StringComparison.Ordinal)
            || !File.Exists(Path.Combine(
                packageDirectory,
                "AIMemory.Windows.exe")))
        {
            throw new InvalidOperationException(
                "应用负载的身份、版本或可执行文件不完整。");
        }
    }

    private static void RemoveExistingPackage()
    {
        RunPowerShell(
            $"$ErrorActionPreference='Stop'; " +
            $"$package=Get-AppxPackage -Name '{PackageName}' " +
            "-ErrorAction SilentlyContinue; " +
            "if ($package) { $package | Remove-AppxPackage " +
            "-ErrorAction Stop };");
    }

    private static void RegisterAndLaunch(string manifestPath)
    {
        var escapedManifest = manifestPath.Replace("'", "''");
        RunPowerShell(
            "$ErrorActionPreference='Stop'; " +
            $"Add-AppxPackage -Register -Path '{escapedManifest}' " +
            "-ForceApplicationShutdown; " +
            $"$package=Get-AppxPackage -Name '{PackageName}'; " +
            "if (-not $package) { throw '应用注册失败。' }; " +
            "Start-Process \"shell:AppsFolder\\$($package.PackageFamilyName)!App\";");
    }

    private static void RunPowerShell(string command)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command.Replace("\\\"", "\\\\\"")}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException(
            "无法启动 Windows PowerShell。");

        var standardError = process.StandardError.ReadToEnd();
        var standardOutput = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(standardError)
                ? standardOutput
                : standardError;
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? "Windows 应用注册命令失败。"
                    : detail.Trim());
        }
    }
}
