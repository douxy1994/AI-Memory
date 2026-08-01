[CmdletBinding(DefaultParameterSetName = "Executable")]
param(
    [Parameter(Mandatory, ParameterSetName = "Executable")]
    [string]$ExecutablePath,

    [Parameter(Mandatory, ParameterSetName = "Package")]
    [string]$AppUserModelId,

    [string]$ProcessName,

    [ValidateRange(5, 120)]
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"

if (-not [Environment]::UserInteractive) {
    throw "The desktop smoke test requires an interactive Windows session."
}
if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) {
    throw "Windows Explorer is not running, so the notification area is unavailable."
}

$launchMode = $PSCmdlet.ParameterSetName
if ($launchMode -eq "Executable") {
    $resolvedExecutable = (
        Resolve-Path -LiteralPath $ExecutablePath -ErrorAction Stop
    ).Path
    if (-not (Test-Path -LiteralPath $resolvedExecutable -PathType Leaf)) {
        throw "The executable path is not a file: $resolvedExecutable"
    }
    if (-not $ProcessName) {
        $ProcessName = [IO.Path]::GetFileNameWithoutExtension(
            $resolvedExecutable
        )
    }
}
elseif (-not $ProcessName) {
    throw "-ProcessName is required when launching an installed MSIX package."
}

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class AIMemoryWindowProbe
{
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(
        EnumWindowsCallback callback,
        IntPtr data);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        IntPtr window,
        StringBuilder text,
        int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        IntPtr window,
        StringBuilder className,
        int maxCount);

    public static string DescribeProcessWindows(int processId)
    {
        var lines = new List<string>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner != processId) return true;
            var title = new StringBuilder(256);
            var className = new StringBuilder(256);
            GetWindowText(window, title, title.Capacity);
            GetClassName(window, className, className.Capacity);
            lines.Add(
                $"0x{window.ToInt64():X} visible={IsWindowVisible(window)} " +
                $"class={className} title={title}");
            return true;
        }, IntPtr.Zero);
        return lines.Count == 0
            ? "<no top-level windows>"
            : String.Join(Environment.NewLine, lines);
    }

    public static IntPtr FindVisibleWindow(int processId)
    {
        var result = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner == processId && IsWindowVisible(window))
            {
                result = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static IntPtr FindAnyWindow(int processId)
    {
        var result = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner == processId)
            {
                result = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
'@

function Start-AIMemory {
    if ($launchMode -eq "Executable") {
        Start-Process -FilePath $resolvedExecutable | Out-Null
        return
    }
    Start-Process `
        -FilePath "explorer.exe" `
        -ArgumentList "shell:AppsFolder\$AppUserModelId" | Out-Null
}

function Wait-Until {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Condition,
        [Parameter(Mandatory)]
        [string]$FailureMessage
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (& $Condition) {
            return
        }
        Start-Sleep -Milliseconds 100
    }
    throw $FailureMessage
}

function Get-AIMemoryProcesses {
    @(
        Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
    )
}

function Get-MainWindowHandle {
    param([int]$ProcessId)

    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $process) {
        return [IntPtr]::Zero
    }
    $process.Refresh()
    if ($process.MainWindowHandle -ne [IntPtr]::Zero -and
        [AIMemoryWindowProbe]::IsWindowVisible($process.MainWindowHandle)) {
        return $process.MainWindowHandle
    }
    return [AIMemoryWindowProbe]::FindVisibleWindow($ProcessId)
}

function Get-StartupLogPath {
    Join-Path `
        ([Environment]::GetFolderPath("LocalApplicationData")) `
        "AIMemory\startup.log"
}

function Write-StartupDiagnostics {
    $startupLog = Get-StartupLogPath
    if (Test-Path -LiteralPath $startupLog -PathType Leaf) {
        Write-Host "AI Memory startup diagnostics ($startupLog):"
        Get-Content -LiteralPath $startupLog
    }
    else {
        Write-Host "AI Memory startup diagnostics were not written: $startupLog"
    }
}

function Write-WindowDiagnostics {
    param([int]$ProcessId)

    if (-not $ProcessId) {
        Write-Host "AI Memory process id was not captured."
        return
    }
    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $process) {
        Write-Host "AI Memory process $ProcessId is no longer running."
        return
    }
    $process.Refresh()
    Write-Host (
        "AI Memory process {0}: MainWindowHandle=0x{1:X}, " +
        "MainWindowTitle='{2}', SessionId={3}" -f
        $process.Id,
        $process.MainWindowHandle.ToInt64(),
        $process.MainWindowTitle,
        $process.SessionId)
    Write-Host ([AIMemoryWindowProbe]::DescribeProcessWindows($ProcessId))
}

$preexisting = Get-AIMemoryProcesses
if ($preexisting.Count -ne 0) {
    throw ((
        "Close all existing {0} processes before running the smoke test. " +
        "Found: {1}"
    ) -f $ProcessName, (($preexisting.Id | Sort-Object) -join ", "))
}

$testProcessId = $null
try {
    Start-AIMemory
    Wait-Until {
        (Get-AIMemoryProcesses).Count -eq 1
    } "AI Memory did not start as exactly one process."

    $processes = Get-AIMemoryProcesses
    $testProcessId = $processes[0].Id
    $mainWindow = [IntPtr]::Zero
    Wait-Until {
        $script:mainWindow = Get-MainWindowHandle $testProcessId
        if ($script:mainWindow -eq [IntPtr]::Zero) {
            # A just-created WinUI HWND can be hidden for one dispatcher turn.
            # Restore it before declaring startup unhealthy.
            $script:mainWindow = [AIMemoryWindowProbe]::FindAnyWindow($testProcessId)
            if ($script:mainWindow -ne [IntPtr]::Zero) {
                [AIMemoryWindowProbe]::ShowWindow($script:mainWindow, 9) | Out-Null
                [AIMemoryWindowProbe]::SetForegroundWindow($script:mainWindow) | Out-Null
            }
        }
        $script:mainWindow -ne [IntPtr]::Zero -and
            [AIMemoryWindowProbe]::IsWindowVisible($script:mainWindow)
    } "AI Memory started, but its main window did not become visible."

    $startupLog = Get-StartupLogPath
    Wait-Until {
        (Test-Path -LiteralPath $startupLog -PathType Leaf) -and
            (Select-String -LiteralPath $startupLog -Pattern " launch.complete$" -Quiet)
    } "AI Memory displayed a window, but did not complete startup."

    $process = Get-Process -Id $testProcessId
    if (-not $process.CloseMainWindow()) {
        throw "Windows could not send the close request to the main window."
    }
    Wait-Until {
        $running = Get-Process `
            -Id $testProcessId `
            -ErrorAction SilentlyContinue
        $running -and
            -not [AIMemoryWindowProbe]::IsWindowVisible($mainWindow)
    } "Closing the main window did not hide it while keeping AI Memory alive."

    Start-AIMemory
    Wait-Until {
        $running = Get-AIMemoryProcesses
        $running.Count -eq 1 -and
            $running[0].Id -eq $testProcessId
    } "A second launch created another persistent AI Memory process."
    Wait-Until {
        $reopened = Get-MainWindowHandle $testProcessId
        $reopened -ne [IntPtr]::Zero -and
            [AIMemoryWindowProbe]::IsWindowVisible($reopened)
    } "The second launch did not restore the hidden main window."

    [pscustomobject]@{
        Result = "passed"
        ProcessName = $ProcessName
        ProcessId = $testProcessId
        SingleInstance = $true
        CloseHidesWindow = $true
        RelaunchRestoresWindow = $true
        NotificationAreaRequired = $true
    } | ConvertTo-Json
}
catch {
    Write-WindowDiagnostics $testProcessId
    Write-StartupDiagnostics
    throw
}
finally {
    if ($testProcessId) {
        $testProcess = Get-Process `
            -Id $testProcessId `
            -ErrorAction SilentlyContinue
        if ($testProcess) {
            Stop-Process -Id $testProcessId -Force
            $testProcess.WaitForExit(5000) | Out-Null
        }
    }
}
