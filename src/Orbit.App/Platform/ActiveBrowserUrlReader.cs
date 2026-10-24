using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using Microsoft.Win32.SafeHandles;

namespace Orbit.App.Platform;

public sealed record BrowserUrlResult(string? Url, string? Error)
{
    public bool Success => Url is not null && Error is null;
}

/// <summary>Best-effort, read-only address-bar access for the captured foreground browser. Never reads page text, clipboard or profiles.</summary>
public sealed class ActiveBrowserUrlReader : IDisposable
{
    private static string Manual => global::Orbit.Core.Localization.L.Get("Text_02D79C5498FB");
    private static string Changed => global::Orbit.Core.Localization.L.Get("Text_4BEB6067A4F7");
    private readonly SemaphoreSlim _workerGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IBrowserUrlPlatform _platform;
    private readonly TimeSpan _timeout;
    private long _version;
    private int _disposed;

    public ActiveBrowserUrlReader(TimeSpan? timeout = null) : this(new NativeBrowserUrlPlatform(), timeout) { }
    internal ActiveBrowserUrlReader(IBrowserUrlPlatform platform, TimeSpan? timeout = null)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _timeout = timeout ?? TimeSpan.FromMilliseconds(700);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    /// <summary>Cheap browser context classification. Does not use UI Automation or read an address.</summary>
    public static bool IsSupportedBrowserWindow(IntPtr capturedHwnd) => NativeBrowserUrlPlatform.TryDescribe(capturedHwnd, false, out _);

    public async Task<BrowserUrlResult> ReadAsync(IntPtr capturedWindow, CancellationToken cancellationToken = default)
    {
        long version = Interlocked.Increment(ref _version);
        cancellationToken.ThrowIfCancellationRequested();
        if (capturedWindow == IntPtr.Zero || Volatile.Read(ref _disposed) != 0) return Failure(Manual);
        if (!_workerGate.Wait(0)) return Failure(global::Orbit.Core.Localization.L.Get("Text_F51EC91FC25D"));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        // All provider calls run on a worker MTA. A timed-out provider retains this single slot until it actually returns.
        var worker = Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (!_platform.TryCapture(capturedWindow, out var identity)) return Failure(Changed);
                var raw = _platform.ReadAddress(identity, token);
                token.ThrowIfCancellationRequested();
                if (!_platform.IsCurrent(identity)) return Failure(Changed);
                return TryNormalizeUrl(raw, out var url) ? new BrowserUrlResult(url, null) : Failure(Manual);
            }
            finally { _workerGate.Release(); }
        }, CancellationToken.None);
        try
        {
            var result = await worker.WaitAsync(_timeout, token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) != 0 || version != Interlocked.Read(ref _version)) return Failure(Changed);
            return result;
        }
        catch (TimeoutException)
        {
            linked.Cancel(); Observe(worker); return Failure(global::Orbit.Core.Localization.L.Get("Text_296A64421D7B"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { linked.Cancel(); Observe(worker); return Failure(Manual); }
        catch (OperationCanceledException) { linked.Cancel(); Observe(worker); throw; }
        catch (Exception error) when (error is COMException or ElementNotAvailableException or ElementNotEnabledException
            or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or ArgumentException or NotSupportedException or InvalidCastException)
        {
            // Provider exception messages may contain page data, so do not propagate or log them.
            return Failure(Manual);
        }
    }

    internal static bool TryNormalizeUrl(string? value, out string? url)
    {
        url = null;
        if (value is null || value.Length > 8192) return false;
        var text = value.Trim();
        if (text.Length == 0 || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\')) return false;
        for (int index = 0; index < text.Length; index++)
        {
            if (!char.IsSurrogate(text[index])) continue;
            if (!char.IsHighSurrogate(text[index]) || index + 1 >= text.Length || !char.IsLowSurrogate(text[++index])) return false;
        }
        // Browsers may elide HTTPS. Never turn an arbitrary search phrase or an internal scheme into a URL.
        bool elided = !text.Contains("://", StringComparison.Ordinal);
        if (elided) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(parsed.Host) || !string.IsNullOrEmpty(parsed.UserInfo)) return false;
        try { if (Uri.CheckHostName(parsed.IdnHost) == UriHostNameType.Unknown) return false; }
        catch (UriFormatException) { return false; }
        if (elided && parsed.Host != "localhost" && !parsed.Host.Contains('.') && parsed.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6)) return false;
        url = parsed.AbsoluteUri;
        return true;
    }

    internal static bool SupportedProcess(string name) => name is "chrome" or "msedge" or "firefox" or "brave" or "opera" or "vivaldi";
    internal static bool SupportedClass(string process, string windowClass) => process == "firefox" ? windowClass == "MozillaWindowClass" : windowClass == "Chrome_WidgetWin_1";
    internal static bool Prune(BrowserNodeFacts node, uint processId) => node.ProcessId != processId || node.Offscreen
        || node.Kind is BrowserNodeKind.Document or BrowserNodeKind.TabItem or BrowserNodeKind.Menu or BrowserNodeKind.ListItem
        || node.ClassName is "Chrome_RenderWidgetHostHWND" or "MozillaContentWindowClass";

    internal static bool IsAddressBar(BrowserNodeFacts node, uint processId, Rect windowBounds)
    {
        if (Prune(node, processId) || node.Kind != BrowserNodeKind.Edit || node.Password || node.Bounds.IsEmpty
            || node.Bounds.Width < 60 || node.Bounds.Height < 8 || node.Bounds.Height > 180 || !windowBounds.Contains(node.Bounds)
            || node.Bounds.Top > windowBounds.Top + Math.Min(360, windowBounds.Height * .45)) return false;
        return node.AutomationId is "urlbar-input" or "urlbar" or "addressEditBox" or "omnibox"
            || node.ClassName is "OmniboxViewViews" or "Chrome_OmniboxView"
            || node.Name is "Address and search bar" or "Search or enter web address" or "Search or enter address" or "Address bar"
                or "Адресная строка и строка поиска" or "Поиск или ввод веб-адреса" or "Поиск или ввод адреса"
                or "Найдите в Google или введите URL" or "Введите поисковый запрос или URL";
    }

    internal static string? ReadAddressTree(IBrowserUrlNode root, BrowserWindowIdentity identity, CancellationToken token)
    {
        var queue = new Queue<(IBrowserUrlNode Node, int Depth)>();queue.Enqueue((root, 0));
        var candidates = new List<IBrowserUrlNode>();int visited = 0;
        while (queue.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            if (++visited > 512) return null;
            var (node, depth) = queue.Dequeue();var facts = node.Facts();
            if (Prune(facts, identity.ProcessId)) continue;
            if (IsAddressBar(facts, identity.ProcessId, identity.Bounds)) candidates.Add(node);
            if (candidates.Count > 1) return null;
            if (facts.Kind == BrowserNodeKind.Edit || depth >= 12) continue;
            for (var child = node.FirstChild(); child != null; child = child.NextSibling())
            {
                token.ThrowIfCancellationRequested();
                if (queue.Count + visited >= 512) return null;
                queue.Enqueue((child, depth + 1));
            }
        }
        if (candidates.Count != 1) return null;
        token.ThrowIfCancellationRequested();
        var first = candidates[0].ReadValue();
        token.ThrowIfCancellationRequested();
        var second = candidates[0].ReadValue();
        return string.Equals(first, second, StringComparison.Ordinal) ? first : null;
    }

    private static BrowserUrlResult Failure(string error) => new(null, error);
    private static void Observe(Task task) => _ = task.ContinueWith(done => _ = done.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) { Interlocked.Increment(ref _version);_lifetime.Cancel(); } }
}

internal enum BrowserNodeKind { Other, Edit, Document, TabItem, Menu, ListItem }
internal sealed record BrowserNodeFacts(BrowserNodeKind Kind, uint ProcessId, bool Offscreen, bool Password, Rect Bounds, string AutomationId = "", string ClassName = "", string Name = "");
internal sealed record BrowserWindowIdentity(IntPtr Handle, uint ProcessId, string ProcessName, long CreationTime, Rect Bounds);
internal interface IBrowserUrlNode
{
    BrowserNodeFacts Facts();
    IBrowserUrlNode? FirstChild();
    IBrowserUrlNode? NextSibling();
    string? ReadValue();
}
internal interface IBrowserUrlPlatform
{
    bool TryCapture(IntPtr handle, out BrowserWindowIdentity identity);
    bool IsCurrent(BrowserWindowIdentity identity);
    string? ReadAddress(BrowserWindowIdentity identity, CancellationToken token);
}

internal sealed class NativeBrowserUrlPlatform : IBrowserUrlPlatform
{
    public bool TryCapture(IntPtr handle, out BrowserWindowIdentity identity) => TryDescribe(handle, true, out identity);
    public bool IsCurrent(BrowserWindowIdentity expected) => TryDescribe(expected.Handle, true, out var now)
        && now.ProcessId == expected.ProcessId && now.CreationTime == expected.CreationTime && now.ProcessName == expected.ProcessName;
    public string? ReadAddress(BrowserWindowIdentity identity, CancellationToken token)
    {
        var root = AutomationElement.FromHandle(identity.Handle);
        return root is null ? null : ActiveBrowserUrlReader.ReadAddressTree(new UiaNode(root, identity.ProcessId), identity, token);
    }

    internal static bool TryDescribe(IntPtr handle, bool requireForeground, out BrowserWindowIdentity identity)
    {
        identity = null!;
        if (handle == IntPtr.Zero || !IsWindow(handle) || !IsWindowVisible(handle) || GetAncestor(handle, 2) != handle
            || requireForeground && GetForegroundWindow() != handle) return false;
        _ = GetWindowThreadProcessId(handle, out var pid);
        if (pid == 0) return false;
        using var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION only.
        if (process.IsInvalid) return false;
        var path = new StringBuilder(32768);uint size = (uint)path.Capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref size)) return false;
        var name = Path.GetFileNameWithoutExtension(path.ToString()).ToLowerInvariant();
        if (!ActiveBrowserUrlReader.SupportedProcess(name)) return false;
        var windowClass = new StringBuilder(256);_ = GetClassName(handle, windowClass, windowClass.Capacity);
        if (!ActiveBrowserUrlReader.SupportedClass(name, windowClass.ToString()) || !GetProcessTimes(process, out var created, out _, out _, out _)
            || !GetWindowRect(handle, out var bounds) || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return false;
        identity = new(handle, pid, name, created, new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top));
        return !requireForeground || GetForegroundWindow() == handle;
    }

    private sealed class UiaNode(AutomationElement element, uint expectedProcess) : IBrowserUrlNode
    {
        public BrowserNodeFacts Facts()
        {
            var current = element.Current;var type = current.ControlType;
            var kind = type == ControlType.Edit ? BrowserNodeKind.Edit : type == ControlType.Document ? BrowserNodeKind.Document
                : type == ControlType.TabItem ? BrowserNodeKind.TabItem : type == ControlType.Menu || type == ControlType.MenuItem ? BrowserNodeKind.Menu
                : type == ControlType.ListItem ? BrowserNodeKind.ListItem : BrowserNodeKind.Other;
            var pid = (uint)current.ProcessId;
            // Never request document/tab text or data from another process. Edit labels identify the bar; only the selected bar's Value is read.
            if (pid != expectedProcess || kind is BrowserNodeKind.Document or BrowserNodeKind.TabItem or BrowserNodeKind.Menu or BrowserNodeKind.ListItem)
                return new(kind, pid, false, false, Rect.Empty);
            bool offscreen = current.IsOffscreen;string className = current.ClassName;
            if (offscreen || className is "Chrome_RenderWidgetHostHWND" or "MozillaContentWindowClass") return new(kind, pid, offscreen, false, Rect.Empty, ClassName: className);
            var bounds = current.BoundingRectangle;
            return kind == BrowserNodeKind.Edit
                ? new(kind, pid, false, current.IsPassword, bounds, current.AutomationId, className, current.Name)
                : new(kind, pid, false, false, bounds, ClassName: className);
        }
        public IBrowserUrlNode? FirstChild() => Wrap(TreeWalker.ControlViewWalker.GetFirstChild(element));
        public IBrowserUrlNode? NextSibling() => Wrap(TreeWalker.ControlViewWalker.GetNextSibling(element));
        private IBrowserUrlNode? Wrap(AutomationElement? next) => next is null ? null : new UiaNode(next, expectedProcess);
        public string? ReadValue() => element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? ((ValuePattern)pattern).Current.Value : null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maximum);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rectangle);
    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
}
