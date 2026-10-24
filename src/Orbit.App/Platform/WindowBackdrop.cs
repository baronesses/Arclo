using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Orbit.App.Platform;

public enum WindowBackdropMode { None, AccentBlur }

/// <summary>
/// Best-effort native blur for the full window. The caller must disable it in compact modes:
/// Windows blurs the entire HWND, including visually transparent gaps in a layered WPF window.
/// </summary>
public sealed class WindowBackdrop : IDisposable
{
    private readonly Window _owner;
    private bool _requested, _refreshPending, _disposed;
    private IntPtr _handle;
    private static IntPtr _nativeModule;
    private static readonly Lazy<SetCompositionAttribute?> NativeSet = new(LoadNativeSet);

    public WindowBackdrop(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.Dispatcher.VerifyAccess();
        _owner = owner;
        owner.SourceInitialized += OnSourceInitialized;
        owner.IsVisibleChanged += OnVisibleChanged;
        owner.Closed += OnClosed;
    }

    /// <summary>The accepted native request; actual effects remain subject to Windows composition/accessibility policy.</summary>
    public WindowBackdropMode AppliedMode { get; private set; }
    public bool IsApplied => AppliedMode != WindowBackdropMode.None;
    public string? UnavailableReason { get; private set; }

    /// <summary>Dispatcher-affine. Disable for compact mode, reduced transparency, and opaque fallback rendering.</summary>
    public bool Apply(bool enabled)
    {
        _owner.Dispatcher.VerifyAccess();
        if (_disposed) return false;
        _requested = enabled;
        return ApplyCore();
    }

    /// <summary>One coalesced native refresh after a show or presentation change; no bitmap/desktop capture.</summary>
    public void RequestRefresh()
    {
        _owner.Dispatcher.VerifyAccess();
        if (_disposed || _refreshPending || _owner.Dispatcher.HasShutdownStarted) return;
        _refreshPending = true;
        _owner.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _refreshPending = false;
            if (!_disposed) ApplyCore();
        }));
    }

    private bool ApplyCore()
    {
        _handle = new WindowInteropHelper(_owner).Handle;
        if (_handle == IntPtr.Zero)
        {
            AppliedMode = WindowBackdropMode.None;
            UnavailableReason = _requested ? global::Orbit.Core.Localization.L.Get("Text_6E5943406D25") : null;
            return false;
        }

        bool enabled = _requested && !SystemParameters.HighContrast;
        if (!enabled)
        {
            if (IsApplied && !TrySetAccent(_handle, 0))
            {
                UnavailableReason = global::Orbit.Core.Localization.L.Get("Text_EBABFA7C406B");
                return false;
            }
            AppliedMode = WindowBackdropMode.None;
            UnavailableReason = _requested && SystemParameters.HighContrast ? global::Orbit.Core.Localization.L.Get("Text_1C29E19F96B9") : null;
            return false;
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            UnavailableReason = global::Orbit.Core.Localization.L.Get("Text_12ACDCECEAEB");
            AppliedMode = WindowBackdropMode.None;
            return false;
        }
        if (TrySetAccent(_handle, 3))
        {
            AppliedMode = WindowBackdropMode.AccentBlur;
            UnavailableReason = null;
            return true;
        }
        AppliedMode = WindowBackdropMode.None;
        UnavailableReason = global::Orbit.Core.Localization.L.Get("Text_2A226E7D1262");
        return false;
    }

    private static bool TrySetAccent(IntPtr window, int state)
    {
        var setter = NativeSet.Value;
        if (setter is null) return false;
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        try
        {
            Marshal.StructureToPtr(new AccentPolicy { State = state, Flags = 0, GradientColor = 0, AnimationId = 0 }, pointer, false);
            var data = new CompositionData { Attribute = 19, Data = pointer, Size = new UIntPtr((uint)Marshal.SizeOf<AccentPolicy>()) };
            return setter(window, ref data);
        }
        catch (Exception error) when (error is ExternalException or InvalidOperationException) { return false; }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static SetCompositionAttribute? LoadNativeSet()
    {
        try
        {
            // This optional export has no SDK import library. Load only the system DLL; keep it alive with its delegate.
            if (!NativeLibrary.TryLoad("user32.dll", typeof(WindowBackdrop).Assembly, DllImportSearchPath.System32, out _nativeModule)) return null;
            if (NativeLibrary.TryGetExport(_nativeModule, "SetWindowCompositionAttribute", out var address))
                return Marshal.GetDelegateForFunctionPointer<SetCompositionAttribute>(address);
            NativeLibrary.Free(_nativeModule);
            _nativeModule = IntPtr.Zero;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { }
        return null;
    }

    private void OnSourceInitialized(object? sender, EventArgs args) => RequestRefresh();
    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs args) { if (_owner.IsVisible) RequestRefresh(); }
    private void OnClosed(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        _owner.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _owner.SourceInitialized -= OnSourceInitialized;
        _owner.IsVisibleChanged -= OnVisibleChanged;
        _owner.Closed -= OnClosed;
        if (_handle != IntPtr.Zero && IsApplied) TrySetAccent(_handle, 0);
        _handle = IntPtr.Zero;
        AppliedMode = WindowBackdropMode.None;
        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy { public int State, Flags; public uint GradientColor; public int AnimationId; }
    [StructLayout(LayoutKind.Sequential)] private struct CompositionData { public int Attribute; public IntPtr Data; public UIntPtr Size; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool SetCompositionAttribute(IntPtr window, ref CompositionData data);
}
