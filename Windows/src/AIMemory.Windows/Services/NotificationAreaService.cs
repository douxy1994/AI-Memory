using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace AIMemory.Windows.Services;

/// <summary>
/// Owns the native Windows notification-area icon for the lifetime of the main
/// window. WinUI does not expose a notification-area control, so this narrow
/// bridge uses the official Shell_NotifyIcon and window-subclass APIs.
/// </summary>
internal sealed class NotificationAreaService : IDisposable
{
    private const uint CallbackMessage = NativeMethods.WmApp + 42;
    private const nuint SubclassId = 0xA14D454D;
    private const uint IconId = 1;
    private const uint CommandOpen = 1001;
    private const uint CommandSync = 1002;
    private const uint CommandExit = 1003;

    private readonly nint _windowHandle;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Action _open;
    private readonly Action _sync;
    private readonly Action _exit;
    private readonly string _openLabel;
    private readonly string _syncLabel;
    private readonly string _exitLabel;
    private readonly NativeMethods.SubclassProc _subclassProc;
    private readonly uint _taskbarCreatedMessage;
    private nint _iconHandle;
    private bool _ownsIcon;
    private bool _iconAdded;
    private bool _disposed;

    public NotificationAreaService(
        nint windowHandle,
        DispatcherQueue dispatcherQueue,
        Action open,
        Action sync,
        Action exit,
        string openLabel,
        string syncLabel,
        string exitLabel)
    {
        _windowHandle = windowHandle;
        _dispatcherQueue = dispatcherQueue;
        _open = open;
        _sync = sync;
        _exit = exit;
        _openLabel = openLabel;
        _syncLabel = syncLabel;
        _exitLabel = exitLabel;
        _subclassProc = WindowSubclassProc;
        _taskbarCreatedMessage =
            NativeMethods.RegisterWindowMessageW("TaskbarCreated");

        if (!NativeMethods.SetWindowSubclass(
                _windowHandle,
                _subclassProc,
                SubclassId,
                0))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not attach the AI Memory notification-area handler.");
        }

        try
        {
            (_iconHandle, _ownsIcon) = LoadApplicationIcon();
            AddIcon();
        }
        catch
        {
            NativeMethods.RemoveWindowSubclass(
                _windowHandle,
                _subclassProc,
                SubclassId);
            ReleaseIcon();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DeleteIcon();
        NativeMethods.RemoveWindowSubclass(
            _windowHandle,
            _subclassProc,
            SubclassId);
        ReleaseIcon();
    }

    private nint WindowSubclassProc(
        nint hWnd,
        uint message,
        nint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        if (_taskbarCreatedMessage != 0
            && message == _taskbarCreatedMessage)
        {
            _iconAdded = false;
            try
            {
                AddIcon();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Could not restore the notification-area icon: {exception}");
            }
            return 0;
        }

        if (message == CallbackMessage)
        {
            HandleIconEvent(
                unchecked((uint)lParam.ToInt64()) & 0xFFFF);
            return 0;
        }

        return NativeMethods.DefSubclassProc(
            hWnd,
            message,
            wParam,
            lParam);
    }

    private void HandleIconEvent(uint message)
    {
        switch (message)
        {
            case NativeMethods.WmContextMenu:
            case NativeMethods.WmRightButtonUp:
                ShowContextMenu();
                break;
            case NativeMethods.WmLeftButtonUp:
            case NativeMethods.WmLeftButtonDoubleClick:
            case NativeMethods.NinSelect:
            case NativeMethods.NinKeySelect:
                Enqueue(_open);
                break;
        }
    }

    private void ShowContextMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == 0) return;

        try
        {
            NativeMethods.AppendMenuW(
                menu,
                NativeMethods.MfString | NativeMethods.MfDefault,
                CommandOpen,
                _openLabel);
            NativeMethods.AppendMenuW(
                menu,
                NativeMethods.MfString,
                CommandSync,
                _syncLabel);
            NativeMethods.AppendMenuW(
                menu,
                NativeMethods.MfSeparator,
                0,
                null);
            NativeMethods.AppendMenuW(
                menu,
                NativeMethods.MfString,
                CommandExit,
                _exitLabel);

            NativeMethods.GetCursorPos(out var point);
            NativeMethods.SetForegroundWindow(_windowHandle);
            var command = NativeMethods.TrackPopupMenu(
                menu,
                NativeMethods.TpmRightButton
                | NativeMethods.TpmReturnCommand
                | NativeMethods.TpmNonotify,
                point.X,
                point.Y,
                0,
                _windowHandle,
                0);
            NativeMethods.PostMessageW(
                _windowHandle,
                NativeMethods.WmNull,
                0,
                0);

            switch (command)
            {
                case CommandOpen:
                    Enqueue(_open);
                    break;
                case CommandSync:
                    Enqueue(_sync);
                    break;
                case CommandExit:
                    Enqueue(_exit);
                    break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
            var data = CreateIconData();
            NativeMethods.Shell_NotifyIconW(
                NativeMethods.NimSetFocus,
                ref data);
        }
    }

    private void Enqueue(Action action)
    {
        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                if (!_disposed) action();
            }))
        {
            System.Diagnostics.Debug.WriteLine(
                "Could not dispatch a notification-area command.");
        }
    }

    private void AddIcon()
    {
        if (_disposed || _iconAdded) return;
        var data = CreateIconData();
        if (!NativeMethods.Shell_NotifyIconW(
                NativeMethods.NimAdd,
                ref data))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not add the AI Memory notification-area icon.");
        }

        _iconAdded = true;
        data.TimeoutOrVersion = NativeMethods.NotifyIconVersion4;
        NativeMethods.Shell_NotifyIconW(
            NativeMethods.NimSetVersion,
            ref data);
    }

    private void DeleteIcon()
    {
        if (!_iconAdded) return;
        var data = CreateIconData();
        NativeMethods.Shell_NotifyIconW(
            NativeMethods.NimDelete,
            ref data);
        _iconAdded = false;
    }

    private NativeMethods.NotifyIconData CreateIconData() => new()
    {
        Size = Marshal.SizeOf<NativeMethods.NotifyIconData>(),
        WindowHandle = _windowHandle,
        Id = IconId,
        Flags = NativeMethods.NifMessage
                | NativeMethods.NifIcon
                | NativeMethods.NifTip,
        CallbackMessage = CallbackMessage,
        IconHandle = _iconHandle,
        Tip = "AI Memory",
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private static (nint Handle, bool OwnsHandle) LoadApplicationIcon()
    {
        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            var count = NativeMethods.ExtractIconExW(
                executable,
                0,
                out var large,
                out var small,
                1);
            if (count > 0)
            {
                if (small != 0)
                {
                    if (large != 0) NativeMethods.DestroyIcon(large);
                    return (small, true);
                }
                if (large != 0) return (large, true);
            }
        }

        var fallback = NativeMethods.LoadIconW(
            0,
            NativeMethods.IdiApplication);
        if (fallback == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not load a notification-area icon.");
        }
        return (fallback, false);
    }

    private void ReleaseIcon()
    {
        if (_ownsIcon && _iconHandle != 0)
        {
            NativeMethods.DestroyIcon(_iconHandle);
        }
        _iconHandle = 0;
        _ownsIcon = false;
    }

    private static class NativeMethods
    {
        internal const uint WmNull = 0x0000;
        internal const uint WmContextMenu = 0x007B;
        internal const uint WmApp = 0x8000;
        internal const uint WmLeftButtonUp = 0x0202;
        internal const uint WmLeftButtonDoubleClick = 0x0203;
        internal const uint WmRightButtonUp = 0x0205;
        internal const uint WmUser = 0x0400;
        internal const uint NinSelect = WmUser;
        internal const uint NinKeySelect = WmUser + 1;
        internal const uint NimAdd = 0x00000000;
        internal const uint NimDelete = 0x00000002;
        internal const uint NimSetFocus = 0x00000003;
        internal const uint NimSetVersion = 0x00000004;
        internal const uint NifMessage = 0x00000001;
        internal const uint NifIcon = 0x00000002;
        internal const uint NifTip = 0x00000004;
        internal const uint NotifyIconVersion4 = 4;
        internal const uint MfString = 0x00000000;
        internal const uint MfDefault = 0x00001000;
        internal const uint MfSeparator = 0x00000800;
        internal const uint TpmRightButton = 0x0002;
        internal const uint TpmNonotify = 0x0080;
        internal const uint TpmReturnCommand = 0x0100;
        internal static readonly nint IdiApplication = 32512;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct NotifyIconData
        {
            internal int Size;
            internal nint WindowHandle;
            internal uint Id;
            internal uint Flags;
            internal uint CallbackMessage;
            internal nint IconHandle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            internal string Tip;
            internal uint State;
            internal uint StateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            internal string Info;
            internal uint TimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            internal string InfoTitle;
            internal uint InfoFlags;
            internal Guid ItemGuid;
            internal nint BalloonIconHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            internal int X;
            internal int Y;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate nint SubclassProc(
            nint hWnd,
            uint message,
            nint wParam,
            nint lParam,
            nuint subclassId,
            nuint referenceData);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Shell_NotifyIconW(
            uint message,
            ref NotifyIconData data);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint ExtractIconExW(
            string file,
            int iconIndex,
            out nint largeIcon,
            out nint smallIcon,
            uint iconCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint RegisterWindowMessageW(string value);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint LoadIconW(
            nint instance,
            nint iconName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(nint icon);

        [DllImport("user32.dll")]
        internal static extern nint CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AppendMenuW(
            nint menu,
            uint flags,
            nuint item,
            string? text);

        [DllImport("user32.dll")]
        internal static extern uint TrackPopupMenu(
            nint menu,
            uint flags,
            int x,
            int y,
            int reserved,
            nint window,
            nint rectangle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyMenu(nint menu);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(nint window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessageW(
            nint window,
            uint message,
            nint wParam,
            nint lParam);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowSubclass(
            nint window,
            SubclassProc callback,
            nuint subclassId,
            nuint referenceData);

        [DllImport("comctl32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RemoveWindowSubclass(
            nint window,
            SubclassProc callback,
            nuint subclassId);

        [DllImport("comctl32.dll")]
        internal static extern nint DefSubclassProc(
            nint window,
            uint message,
            nint wParam,
            nint lParam);
    }
}
