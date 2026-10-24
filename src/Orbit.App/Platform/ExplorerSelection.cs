using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.CSharp.RuntimeBinder;

namespace Orbit.App.Platform;

public sealed record ExplorerSelectionResult(IReadOnlyList<string> Files, string? Error)
{
    public bool Success => Error is null && Files.Count > 0;
}

public sealed record SelectionSafetyTest(string Name, bool Passed);

/// <summary>
/// Reads only the captured foreground Explorer/desktop selection. Call on an STA thread,
/// and await completion before activating Orbit. This class never uses the clipboard.
/// </summary>
public static class ExplorerSelection
{
    private static readonly Guid ShellBrowserId = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid TopLevelBrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid DispatchId = new("00020400-0000-0000-C000-000000000046");
    private static string ActiveTabUnavailable => global::Orbit.Core.Localization.L.Get("Text_95B78BB2229D");
    private static string ForegroundChanged => global::Orbit.Core.Localization.L.Get("Text_41D55B386672");

    public static ExplorerSelectionResult ReadSelectedFiles(IntPtr foregroundWindow)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            return Failure(global::Orbit.Core.Localization.L.Get("Text_511FC50E0830"));
        }

        if (foregroundWindow == IntPtr.Zero || !IsWindow(foregroundWindow) || GetForegroundWindow() != foregroundWindow)
        {
            return Failure(ForegroundChanged);
        }

        var frame = GetAncestor(foregroundWindow, 2); // GA_ROOT
        var windowClass = ClassName(frame);
        var desktop = windowClass is "Progman" or "WorkerW";
        if (!desktop && windowClass is not ("CabinetWClass" or "ExploreWClass"))
        {
            return Failure(global::Orbit.Core.Localization.L.Get("Text_5A55B27A5C72"));
        }

        // ShellWindows may also contain browser windows; validate the captured native owner first.
        if (!IsExplorerProcess(frame))
        {
            return Failure(global::Orbit.Core.Localization.L.Get("Text_22E76F33331E"));
        }

        try
        {
            using var scope = new ComScope();
            var shellType = Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!;
            var shell = scope.Keep(Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException(global::Orbit.Core.Localization.L.Get("Text_C735C151F6A4")));
            var windows = scope.Keep((object)((dynamic)shell).Windows());
            ExplorerSelectionResult result;
            IntPtr expectedTab = IntPtr.Zero;
            if (desktop)
            {
                result = ReadDesktop(windows, scope);
            }
            else
            {
                var tabs = FindTabWindows(frame);
                expectedTab = ResolveActiveTab(frame, tabs);
                if (tabs.Count > 0 && expectedTab == IntPtr.Zero)
                {
                    return Failure(ActiveTabUnavailable);
                }

                result = ReadExplorer(windows, frame, expectedTab, tabs.Count, scope);
            }

            if (GetForegroundWindow() != foregroundWindow)
            {
                return Failure(ForegroundChanged);
            }

            if (!desktop && expectedTab != IntPtr.Zero && ResolveActiveTab(frame, FindTabWindows(frame)) != expectedTab)
            {
                return Failure(global::Orbit.Core.Localization.L.Get("Text_8DEDA5636936"));
            }

            return result;
        }
        catch (Exception exception) when (IsExpectedInteropFailure(exception))
        {
            Debug.WriteLine($"Arclo Explorer selection read failed: {exception}");
            return Failure(global::Orbit.Core.Localization.L.Get("Text_7B59EA354A2D"));
        }
    }

    private static ExplorerSelectionResult ReadExplorer(object windows, IntPtr frame, IntPtr activeTab, int tabCount, ComScope scope)
    {
        var count = Convert.ToInt32((object)((dynamic)windows).Count);
        if (count < 0 || count > 512)
        {
            return Failure(global::Orbit.Core.Localization.L.Get("Text_992915491404"));
        }

        var candidates = new List<BrowserCandidate>();
        for (var index = 0; index < count; index++)
        {
            try
            {
                object? item = ((dynamic)windows).Item(index);
                if (item is null)
                {
                    continue;
                }

                var browserObject = scope.Keep(item);
                var itemFrame = new IntPtr(Convert.ToInt64((object)((dynamic)browserObject).HWND));
                if (itemFrame != frame)
                {
                    continue;
                }

                var browser = GetShellBrowser(browserObject, scope, preferTabService: true);
                if (browser is null || browser.GetWindow(out var browserWindow) < 0)
                {
                    continue;
                }

                var view = scope.Keep(browser.QueryActiveShellView());
                var visible = view.GetWindow(out var viewWindow) >= 0
                    && viewWindow != IntPtr.Zero
                    && IsWindowVisible(viewWindow)
                    && GetAncestor(viewWindow, 2) == frame;
                candidates.Add(new BrowserCandidate(browserObject, new CandidateIdentity(itemFrame, browserWindow, visible)));
            }
            catch (Exception exception) when (IsExpectedInteropFailure(exception))
            {
                // A closing/navigating window is not a reason to inspect another foreground window.
                Debug.WriteLine($"Arclo skipped unavailable ShellWindows item: {exception.Message}");
            }
        }

        var candidateIndex = ChooseCandidate(candidates.Select(candidate => candidate.Identity).ToArray(), frame, activeTab, tabCount);
        if (candidateIndex < 0)
        {
            return Failure(ActiveTabUnavailable);
        }

        // Read Document only after the frame AND native tab identity have been established.
        var document = scope.Keep((object)((dynamic)candidates[candidateIndex].Browser).Document);
        return ReadAutomationSelection(document, scope);
    }

    private static ExplorerSelectionResult ReadDesktop(object windows, ComScope scope)
    {
        object location = 0; // CSIDL_DESKTOP
        object root = null!; // VT_EMPTY
        var desktopDispatch = scope.Keep(((IShellWindowsDispatch)windows)
            .FindWindowSW(ref location, ref root, 8, out _, 1)); // SWC_DESKTOP, SWFO_NEEDDISPATCH
        var browser = GetShellBrowser(desktopDispatch, scope, preferTabService: false);
        if (browser is null)
        {
            return Failure(global::Orbit.Core.Localization.L.Get("Text_AF31EC3CD937"));
        }

        var view = scope.Keep(browser.QueryActiveShellView());
        if (view.GetWindow(out var desktopView) < 0 || !IsWindowVisible(desktopView))
        {
            return Failure(global::Orbit.Core.Localization.L.Get("Text_FD9F7E222028"));
        }

        var dispatchId = DispatchId;
        var document = scope.Keep(view.GetItemObject(0, ref dispatchId)); // SVGIO_BACKGROUND
        return ReadAutomationSelection(document, scope);
    }

    private static ExplorerSelectionResult ReadAutomationSelection(object document, ComScope scope)
    {
        var selection = scope.Keep((object)((dynamic)document).SelectedItems());
        var count = Convert.ToInt32((object)((dynamic)selection).Count);
        if (count <= 0)
        {
            return Failure(global::Orbit.Core.Localization.L.Get("Text_FAC68FA4BA75"));
        }

        if (count > 4096)
        {
            return Failure(global::Orbit.Core.Localization.L.Get("Text_D74322A9D8E9"));
        }

        var paths = new List<string?>();
        for (var index = 0; index < count; index++)
        {
            var item = scope.Keep((object)((dynamic)selection).Item(index));
            string? path = ((dynamic)item).Path as string;
            paths.Add(path);
        }

        var normalized = SelectedPathPolicy.NormalizeExisting(paths);
        if (normalized.RejectedCount > 0)
        {
            return Failure(normalized.Error ?? global::Orbit.Core.Localization.L.Get("Text_A82CF7D34C41"));
        }
        return new ExplorerSelectionResult(normalized.Paths, normalized.Error);
    }

    private static IShellBrowser? GetShellBrowser(object dispatch, ComScope scope, bool preferTabService)
    {
        if (dispatch is not IComServiceProvider provider)
        {
            return null;
        }

        var interfaceId = ShellBrowserId;
        var serviceId = preferTabService ? ShellBrowserId : TopLevelBrowserService;
        if (provider.QueryService(ref serviceId, ref interfaceId, out var browser) < 0 || browser is null)
        {
            serviceId = preferTabService ? TopLevelBrowserService : ShellBrowserId;
            if (provider.QueryService(ref serviceId, ref interfaceId, out browser) < 0 || browser is null)
            {
                return null;
            }
        }

        return scope.Keep(browser) as IShellBrowser;
    }

    private static int ChooseCandidate(IReadOnlyList<CandidateIdentity> candidates, IntPtr frame, IntPtr activeTab, int tabCount)
    {
        if (tabCount > 0 && activeTab == IntPtr.Zero)
        {
            return -1;
        }

        var chosen = -1;
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (candidate.Frame != frame || !candidate.Visible || (activeTab != IntPtr.Zero && candidate.Tab != activeTab))
            {
                continue;
            }

            if (chosen >= 0)
            {
                return -1; // Never take a first match when Explorer returned ambiguous objects.
            }

            chosen = index;
        }

        return chosen;
    }

    /// <summary>Pure regression checks; no COM, registry, clipboard, focus, or file mutations.</summary>
    public static IReadOnlyList<SelectionSafetyTest> RunSafetySelfTests()
    {
        var frame = new IntPtr(100);
        var active = new IntPtr(101);
        var inactive = new IntPtr(102);
        return new[]
        {
            new SelectionSafetyTest("active tab wins over stale same-frame tab", ChooseCandidate(new[]
            {
                new CandidateIdentity(frame, inactive, true), new CandidateIdentity(frame, active, true)
            }, frame, active, 2) == 1),
            new SelectionSafetyTest("multiple tabs without focus fail closed", ChooseCandidate(new[]
            {
                new CandidateIdentity(frame, inactive, true), new CandidateIdentity(frame, active, true)
            }, frame, IntPtr.Zero, 2) == -1),
            new SelectionSafetyTest("another Explorer frame is never used", ChooseCandidate(new[]
            {
                new CandidateIdentity(new IntPtr(200), active, true)
            }, frame, active, 1) == -1),
            new SelectionSafetyTest("ambiguous matching browser objects fail closed", ChooseCandidate(new[]
            {
                new CandidateIdentity(frame, active, true), new CandidateIdentity(frame, active, true)
            }, frame, active, 1) == -1),
            new SelectionSafetyTest("invisible browser is never read", ChooseCandidate(new[]
            {
                new CandidateIdentity(frame, active, false)
            }, frame, active, 1) == -1),
            new SelectionSafetyTest("single legacy browser remains supported", ChooseCandidate(new[]
            {
                new CandidateIdentity(frame, frame, true)
            }, frame, IntPtr.Zero, 0) == 0)
        };
    }

    private static List<IntPtr> FindTabWindows(IntPtr frame)
    {
        var tabs = new List<IntPtr>();
        EnumChildWindows(frame, (child, _) =>
        {
            if (ClassName(child) == "ShellTabWindowClass")
            {
                tabs.Add(child);
            }

            return true;
        }, IntPtr.Zero);
        return tabs;
    }

    private static IntPtr ResolveActiveTab(IntPtr frame, IReadOnlyList<IntPtr> tabs)
    {
        if (tabs.Count == 0)
        {
            return IntPtr.Zero;
        }

        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        var thread = GetWindowThreadProcessId(frame, out _);
        if (GetGUIThreadInfo(thread, ref info))
        {
            var focus = info.Focus;
            for (var depth = 0; focus != IntPtr.Zero && depth < 64; depth++)
            {
                if (tabs.Contains(focus))
                {
                    return focus;
                }

                if (focus == frame)
                {
                    break;
                }

                focus = GetParent(focus);
            }
        }

        // Count every native tab, including hidden ones. WS_VISIBLE alone cannot identify a tab.
        return tabs.Count == 1 ? tabs[0] : IntPtr.Zero;
    }

    private static bool IsExplorerProcess(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var processId);
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string ClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private static bool IsExpectedInteropFailure(Exception exception) =>
        exception is COMException or InvalidCastException or InvalidOperationException or RuntimeBinderException
            or ArgumentException or NotSupportedException or System.Security.SecurityException;

    private static ExplorerSelectionResult Failure(string error) => new(Array.Empty<string>(), error);
    private sealed record BrowserCandidate(object Browser, CandidateIdentity Identity);
    private readonly record struct CandidateIdentity(IntPtr Frame, IntPtr Tab, bool Visible);

    private sealed class ComScope : IDisposable
    {
        private readonly List<object> _objects = new();
        public T Keep<T>(T value) where T : class
        {
            if (!_objects.Any(existing => ReferenceEquals(existing, value)))
            {
                _objects.Add(value);
            }

            return value;
        }

        public void Dispose()
        {
            for (var index = _objects.Count - 1; index >= 0; index--)
            {
                if (Marshal.IsComObject(_objects[index]))
                {
                    // These RCWs are created only inside this STA call; never FinalRelease shared RCWs.
                    try { Marshal.ReleaseComObject(_objects[index]); }
                    catch (InvalidComObjectException) { }
                }
            }
        }
    }

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellWindowsDispatch
    {
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object location,
            [MarshalAs(UnmanagedType.Struct)] ref object root, int windowClass, out int hwnd, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object? result);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp();
        void InsertMenus();
        void SetMenu();
        void RemoveMenus();
        void SetStatusText();
        void EnableModeless();
        void TranslateAccelerator();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMessage();
        [return: MarshalAs(UnmanagedType.Interface)] IShellView QueryActiveShellView();
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        [PreserveSig] int GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp();
        void TranslateAccelerator();
        void EnableModeless();
        void UIActivate();
        void Refresh();
        void CreateViewWindow();
        void DestroyViewWindow();
        void GetCurrentInfo();
        void AddPropertySheetPages();
        void SaveViewState();
        void SelectItem();
        [return: MarshalAs(UnmanagedType.Interface)] object GetItemObject(uint aspect, ref Guid interfaceId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public NativeRect CaretRect;
    }

    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsCallback callback, IntPtr parameter);
}
