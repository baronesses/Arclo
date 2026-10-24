using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Orbit.App.Platform;

/// <summary>Owns the tray icon and a user-configurable registered global shortcut.</summary>
public sealed class WindowsBridge : IDisposable
{
    private const int WmHotkey = 0x0312;

    private readonly Window _owner;
    private readonly Action _show;
    private readonly Action _exit;
    private readonly Action<HotkeyInvocation>? _invokeSelected;
    private readonly Action? _showJobs;
    private readonly Action? _toggleQueuePause;
    private readonly HotkeyRegistration _hotkey;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _pauseShortcutItem;
    private Forms.ToolStripMenuItem? _shortcutStatusItem;
    private Forms.ToolStripMenuItem? _showJobsItem;
    private Forms.ToolStripMenuItem? _pauseQueueItem;
    private Forms.ToolStripMenuItem? _queueStatusItem;
    private QueueTrayState _queueState;
    private Icon? _icon;
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _disposed;

    public WindowsBridge(Window owner, Action show, Action exit, Action<HotkeyInvocation>? invokeSelected = null, HotkeyGesture? gesture = null,
        Action? showJobs = null, Action? toggleQueuePause = null, bool initialQueuePaused = false)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(exit);
        owner.Dispatcher.VerifyAccess();

        _owner = owner;
        _show = show;
        _exit = exit;
        _invokeSelected = invokeSelected;
        _showJobs = showJobs;
        _toggleQueuePause = toggleQueuePause;
        _queueState = QueueTrayState.Create(initialQueuePaused, 0, 0, toggleQueuePause is not null, showJobs is not null);
        _hotkey = new HotkeyRegistration(new NativeHotkeyRegistrar(), gesture ?? HotkeyGesture.Default);
        _owner.SourceInitialized += OnSourceInitialized;
        _owner.Closed += OnOwnerClosed;

        CreateTrayIcon();

        // A bridge can be constructed before or after Window.Show().
        var existingHandle = new WindowInteropHelper(owner).Handle;
        if (existingHandle != IntPtr.Zero)
        {
            AttachToWindow(existingHandle);
        }
    }

    public bool HotkeyRegistered => _hotkey.IsRegistered;

    public HotkeyGesture CurrentHotkey => _hotkey.Current;

    public bool TrayAvailable { get; private set; }

    /// <summary>The last Win32 registration error, or zero after success.</summary>
    public int HotkeyErrorCode => _hotkey.ErrorCode;

    public bool HotkeyEnabled
    {
        get => _hotkey.Enabled;
        set
        {
            _owner.Dispatcher.VerifyAccess();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_hotkey.Enabled == value)
            {
                return;
            }

            _hotkey.SetEnabled(value);
            UpdateHotkeyStatus();
        }
    }

    public event EventHandler? StateChanged;

    /// <summary>A failed native replacement leaves the previous registration and gesture intact.</summary>
    public bool TryChangeHotkey(HotkeyGesture gesture, out string? error)
    {
        _owner.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var changed = _hotkey.TryChange(gesture, out error);
        UpdateHotkeyStatus();
        return changed;
    }

    public void ShowContextMenu()
    {
        Dispatch(() => _trayMenu?.Show(Forms.Cursor.Position));
    }

    /// <summary>Updates queue presentation on the owner dispatcher; running jobs and shortcut state are untouched.</summary>
    public void SetQueueState(bool paused, int running, int waiting)
    {
        _owner.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _queueState = QueueTrayState.Create(paused, running, waiting, _toggleQueuePause is not null, _showJobs is not null);
        UpdateQueuePresentation();
    }

    private void CreateTrayIcon()
    {
        try
        {
            _trayMenu = new Forms.ContextMenuStrip();
            var openItem = new Forms.ToolStripMenuItem(global::Orbit.Core.Localization.L.Get("Text_FE9FBD390F12")) { Name="Tray.Open" };
            openItem.Click += (_, _) => Dispatch(_show);
            _trayMenu.Items.Add(openItem);

            _showJobsItem = new Forms.ToolStripMenuItem(global::Orbit.Core.Localization.L.Get("Text_DFF729E1AF66"));
            _showJobsItem.Click += (_, _) => { if (_showJobs is { } showJobs) Dispatch(showJobs); };
            _trayMenu.Items.Add(_showJobsItem);

            _pauseQueueItem = new Forms.ToolStripMenuItem();
            _pauseQueueItem.Click += (_, _) => { if (_toggleQueuePause is { } toggleQueuePause) Dispatch(toggleQueuePause); };
            _trayMenu.Items.Add(_pauseQueueItem);
            _queueStatusItem = new Forms.ToolStripMenuItem { Enabled = false };
            _trayMenu.Items.Add(_queueStatusItem);
            _trayMenu.Items.Add(new Forms.ToolStripSeparator());

            _pauseShortcutItem = new Forms.ToolStripMenuItem(global::Orbit.Core.Localization.L.Get("Text_CD93160C73AB"))
            {
                CheckOnClick = true
            };
            _pauseShortcutItem.Click += (_, _) => Dispatch(() =>
            {
                HotkeyEnabled = !_pauseShortcutItem.Checked;
            });
            _trayMenu.Items.Add(_pauseShortcutItem);

            _shortcutStatusItem = new Forms.ToolStripMenuItem(CurrentHotkey.Display)
            {
                Enabled = false
            };
            _trayMenu.Items.Add(_shortcutStatusItem);
            _trayMenu.Items.Add(new Forms.ToolStripSeparator());

            var exitItem = new Forms.ToolStripMenuItem(global::Orbit.Core.Localization.L.Get("Text_5690C2E63B9D")) { Name="Tray.Exit" };
            exitItem.Click += (_, _) => Dispatch(_exit);
            _trayMenu.Items.Add(exitItem);

            _icon = LoadApplicationIcon();
            _trayIcon = new Forms.NotifyIcon
            {
                Icon = _icon,
                Text = global::Orbit.Core.Localization.L.Get("Text_ADD6D0CC33DE"),
                ContextMenuStrip = _trayMenu,
                Visible = true
            };
            _trayIcon.DoubleClick += (_, _) => Dispatch(_show);
            TrayAvailable = true;
            UpdateQueuePresentation();
        }
        catch (Exception exception) when (
            exception is Win32Exception or ExternalException or ArgumentException or InvalidOperationException)
        {
            Debug.WriteLine($"Arclo tray initialization failed: {exception.Message}");
            DisposeTray();
        }
    }

    private void UpdateQueuePresentation()
    {
        if (_showJobsItem is not null) _showJobsItem.Enabled = _queueState.CanShowJobs;
        if (_pauseQueueItem is not null)
        {
            _pauseQueueItem.Text = _queueState.PauseCommandText;
            _pauseQueueItem.Enabled = _queueState.CanToggleQueuePause;
        }
        if (_queueStatusItem is not null) _queueStatusItem.Text = _queueState.CountsText;
        if (_trayIcon is not null) _trayIcon.Text = _queueState.ToolTipText;
    }

    public void RefreshLocalization()
    {
        _owner.Dispatcher.VerifyAccess();
        if(_trayMenu is not null)foreach(Forms.ToolStripItem item in _trayMenu.Items) {
            if(item.Name is "Tray.Open" or "Tray.Exit")item.Text=Orbit.Core.Localization.L.Get(item.Name);
        }
        if(_showJobsItem is not null)_showJobsItem.Text=Orbit.Core.Localization.L.Get("Tray.Jobs");
        if(_pauseShortcutItem is not null)_pauseShortcutItem.Text=Orbit.Core.Localization.L.Get("Tray.PauseShortcut");
        UpdateQueuePresentation();
    }

    private static Icon LoadApplicationIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } executablePath)
            {
                var extracted = Icon.ExtractAssociatedIcon(executablePath);
                if (extracted is not null)
                {
                    return extracted;
                }
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or ExternalException or System.IO.IOException)
        {
            Debug.WriteLine($"Arclo application icon could not be loaded: {exception.Message}");
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        AttachToWindow(new WindowInteropHelper(_owner).Handle);
    }

    private void AttachToWindow(IntPtr handle)
    {
        if (_disposed || handle == IntPtr.Zero || _source is not null)
        {
            return;
        }

        _source = HwndSource.FromHwnd(handle);
        if (_source is null)
        {
            return;
        }

        _handle = handle;
        _source.AddHook(WindowProc);
        _hotkey.Attach(_handle);
        UpdateHotkeyStatus();
    }

    private void UpdateHotkeyStatus()
    {
        if (_pauseShortcutItem is not null)
        {
            _pauseShortcutItem.Checked = !HotkeyEnabled;
        }

        if (_shortcutStatusItem is not null)
        {
            var label = CurrentHotkey.Display;
            _shortcutStatusItem.Text = !HotkeyEnabled
                ? label + global::Orbit.Core.Localization.L.Get("Text_92F38338DE4D")
                : HotkeyRegistered
                    ? label + global::Orbit.Core.Localization.L.Get("Text_632CB81A0515")
                    : _handle == IntPtr.Zero
                        ? label + global::Orbit.Core.Localization.L.Get("Text_692B3C134CAA")
                        : label + global::Orbit.Core.Localization.L.Get("Text_D4484A740481");
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var id = message == WmHotkey ? wParam.ToInt32() : -1;
        if (message == WmHotkey && _hotkey.ShouldDispatch(id))
        {
            handled = true;
            // Capture before dispatching: the callback may activate the Orbit window.
            var invocation = NativeScreen.GetCursorInvocation();
            Dispatch(() =>
            {
                if (!_hotkey.ShouldDispatch(id)) return;
                if (_invokeSelected is not null)
                {
                    _invokeSelected(invocation);
                }
                else
                {
                    _show();
                }
            });
        }

        return IntPtr.Zero;
    }

    private void Dispatch(Action action)
    {
        if (_disposed || _owner.Dispatcher.HasShutdownStarted || _owner.Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _owner.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (!_disposed)
            {
                action();
            }
        }));
    }

    private void OnOwnerClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (!_owner.Dispatcher.CheckAccess() && !_owner.Dispatcher.HasShutdownStarted)
        {
            _owner.Dispatcher.Invoke(Dispose);
            return;
        }

        _disposed = true;
        _owner.SourceInitialized -= OnSourceInitialized;
        _owner.Closed -= OnOwnerClosed;

        _hotkey.Dispose();
        if (_source is { IsDisposed: false })
        {
            _source.RemoveHook(WindowProc);
        }

        _source = null;
        _handle = IntPtr.Zero;
        DisposeTray();
        GC.SuppressFinalize(this);
    }

    private void DisposeTray()
    {
        TrayAvailable = false;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayMenu?.Dispose();
        _trayMenu = null;
        _pauseShortcutItem = null;
        _shortcutStatusItem = null;
        _showJobsItem = null;
        _pauseQueueItem = null;
        _queueStatusItem = null;
        _icon?.Dispose();
        _icon = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    private sealed class NativeHotkeyRegistrar : IHotkeyRegistrar
    {
        public bool Register(IntPtr hwnd, int id, uint modifiers, uint virtualKey, out int error)
        {
            var success = RegisterHotKey(hwnd, id, modifiers, virtualKey);
            error = success ? 0 : Marshal.GetLastWin32Error();
            return success;
        }
        public bool Unregister(IntPtr hwnd, int id, out int error)
        {
            var success = UnregisterHotKey(hwnd, id);
            error = success ? 0 : Marshal.GetLastWin32Error();
            return success;
        }
    }
}

/// <summary>Pure, immutable queue labels; no queue ownership, native calls, or shortcut state.</summary>
internal readonly record struct QueueTrayState(bool IsPaused, int Running, int Waiting,
    bool CanToggleQueuePause, bool CanShowJobs)
{
    public string PauseCommandText => IsPaused ? global::Orbit.Core.Localization.L.Get("Text_67F9AD62344E") : global::Orbit.Core.Localization.L.Get("Text_710DA0C0168D");
    public string CountsText => global::Orbit.Core.Localization.L.Format("Text_C586C14EB5DA",(object?)(Running),(object?)(Waiting));
    // Keep even two Int32.MaxValue counters within the conservative 63-character NotifyIcon limit.
    public string ToolTipText => global::Orbit.Core.Localization.L.Format("Text_EEF35719A953",(object?)((IsPaused ? global::Orbit.Core.Localization.L.Get("Text_8BD357C9BE45") : "")),(object?)(Running),(object?)(Waiting));

    public static QueueTrayState Create(bool paused, int running, int waiting, bool canToggleQueuePause, bool canShowJobs)
        => new(paused, Math.Max(0, running), Math.Max(0, waiting), canToggleQueuePause, canShowJobs);
}
