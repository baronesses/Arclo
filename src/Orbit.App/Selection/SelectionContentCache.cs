using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Orbit.Core;

namespace Orbit.App.Selection;

/// <summary>
/// Memory-only cache of previously validated selection metadata and native PNG previews.
/// Call TryGet before awaiting the producer, then Store in that same asynchronous flow.
/// A changed file, an unavailable identity, or a missing before-stamp always produces a miss.
/// Execution engines remain responsible for validating their actual inputs.
/// </summary>
public sealed class SelectionContentCache
{
    private const long MaximumItemPreviewBytes = 8L * 1024 * 1024;
    private readonly object _sync = new();
    private readonly int _maximumEntries;
    private readonly long _maximumPreviewBytes;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private readonly AsyncLocal<Dictionary<string, ReadStamp>?> _metadataReads = new();
    private readonly AsyncLocal<Dictionary<string, ReadStamp>?> _previewReads = new();
    private long _previewBytes, _generation;

    public SelectionContentCache(int maximumEntries = 24, long maximumPreviewBytes = 32L * 1024 * 1024)
    {
        if (maximumEntries is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if (maximumPreviewBytes is < 1 or > 512L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumPreviewBytes));
        _maximumEntries = maximumEntries;
        _maximumPreviewBytes = maximumPreviewBytes;
    }

    public bool TryGetMetadata(string path, out ToolkitMetadata metadata)
    {
        metadata = null!;
        if (!TryStamp(path, out var normalized, out var stamp)) return false;
        lock (_sync)
        {
            Remember(_metadataReads, normalized, new(stamp, _generation));
            if (!Find(normalized, stamp, out var entry) || entry.Metadata is null) return false;
            Touch(entry);
            metadata = entry.Metadata;
            return true;
        }
    }

    public bool StoreMetadata(string path, ToolkitMetadata metadata)
    {
        if (metadata is null || !TryStamp(path, out var normalized, out var stamp)
            || !SamePath(metadata.Path, normalized) || metadata.Bytes != stamp.Length
            || !Expected(_metadataReads, normalized, stamp, out var expected)) return false;
        lock (_sync)
        {
            if (expected.Generation != _generation) return false;
            var entry = GetOrAdd(normalized, stamp);
            entry.Metadata = metadata;
            Touch(entry);
            Trim();
            return true;
        }
    }

    public bool TryGetPreview(string path, out byte[] png)
    {
        png = Array.Empty<byte>();
        if (!TryStamp(path, out var normalized, out var stamp)) return false;
        lock (_sync)
        {
            Remember(_previewReads, normalized, new(stamp, _generation));
            if (!Find(normalized, stamp, out var entry) || entry.Preview is null) return false;
            Touch(entry);
            png = (byte[])entry.Preview.Clone();
            return true;
        }
    }

    public bool StorePreview(string path, byte[] png)
    {
        if (png is null || png.LongLength > MaximumItemPreviewBytes || png.LongLength > _maximumPreviewBytes
            || png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || !TryStamp(path, out var normalized, out var stamp)
            || !Expected(_previewReads, normalized, stamp, out var expected)) return false;
        var copy = (byte[])png.Clone();
        lock (_sync)
        {
            if (expected.Generation != _generation) return false;
            var entry = GetOrAdd(normalized, stamp);
            _previewBytes -= entry.Preview?.LongLength ?? 0;
            entry.Preview = copy;
            _previewBytes += copy.LongLength;
            Touch(entry);
            Trim();
            return true;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();_lru.Clear();_previewBytes = 0;
            unchecked { ++_generation; }
        }
        _metadataReads.Value = null;_previewReads.Value = null;
    }

    private static void Remember(AsyncLocal<Dictionary<string, ReadStamp>?> context, string path, ReadStamp stamp)
    {
        // Copy before mutation: child async operations can inherit this execution context.
        var copy = context.Value is { Count: < 64 } prior
            ? new Dictionary<string, ReadStamp>(prior, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ReadStamp>(StringComparer.OrdinalIgnoreCase);
        copy[path] = stamp;
        context.Value = copy;
    }

    private static bool Expected(AsyncLocal<Dictionary<string, ReadStamp>?> context, string path, FileStamp stamp, out ReadStamp expected)
    {
        expected = default;
        return context.Value is { } values && values.TryGetValue(path, out expected) && expected.File == stamp;
    }

    private bool Find(string path, FileStamp stamp, out Entry entry)
    {
        if (_entries.TryGetValue(path, out entry!))
        {
            if (entry.Stamp == stamp) return true;
            Remove(entry);
        }
        entry = null!;
        return false;
    }

    private Entry GetOrAdd(string path, FileStamp stamp)
    {
        if (Find(path, stamp, out var entry)) return entry;
        entry = new(stamp, _lru.AddFirst(path));
        _entries.Add(path, entry);
        return entry;
    }

    private void Touch(Entry entry) { _lru.Remove(entry.Node);_lru.AddFirst(entry.Node); }
    private void Remove(Entry entry) { _previewBytes -= entry.Preview?.LongLength ?? 0;_entries.Remove(entry.Node.Value);_lru.Remove(entry.Node); }
    private void Trim() { while ((_entries.Count > _maximumEntries || _previewBytes > _maximumPreviewBytes) && _lru.Last is { } last) Remove(_entries[last.Value]); }

    private static bool SamePath(string path, string normalized)
    {
        try { return string.Equals(Path.GetFullPath(path), normalized, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool TryStamp(string path, out string normalized, out FileStamp stamp)
    {
        normalized = "";stamp = default;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            normalized = Path.GetFullPath(path);
            // Never synchronously probe network/UNC/device locations on the UI cache path.
            if (normalized.StartsWith(@"\\", StringComparison.Ordinal) || normalized.Length < 3
                || normalized[1] != ':' || normalized.IndexOf(':', 2) >= 0) return false;
            var drive = GetDriveTypeW(Path.GetPathRoot(normalized)!);
            if (drive is not (3 or 6)) return false; // fixed or RAM disk
            using var handle = CreateFileW(normalized, 0x80000000, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)
                || (info.Attributes & 0x410) != 0 // directory or reparse point
                || !GetBasicInfo(handle, 0, out var first, (uint)Marshal.SizeOf<BasicInfo>())
                || !GetIdInfo(handle, 18, out var id, (uint)Marshal.SizeOf<IdInfo>())
                || !GetBasicInfo(handle, 0, out var last, (uint)Marshal.SizeOf<BasicInfo>())
                || first.Creation != last.Creation || first.LastWrite != last.LastWrite || first.Change != last.Change) return false;
            var length = ((ulong)info.SizeHigh << 32) | info.SizeLow;
            if (length > long.MaxValue || (id.Low == 0 && id.High == 0)) return false;
            stamp = new(id.Volume, id.Low, id.High, (long)length, last.Creation, last.LastWrite, last.Change);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { return false; }
    }

    private sealed class Entry
    {
        internal Entry(FileStamp stamp, LinkedListNode<string> node) { Stamp = stamp;Node = node; }
        internal FileStamp Stamp { get; }
        internal LinkedListNode<string> Node { get; }
        internal ToolkitMetadata? Metadata { get; set; }
        internal byte[]? Preview { get; set; }
    }
    private readonly record struct ReadStamp(FileStamp File, long Generation);
    private readonly record struct FileStamp(ulong Volume, ulong IdLow, ulong IdHigh, long Length, long Creation, long LastWrite, long Change);
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo { public long Creation, LastAccess, LastWrite, Change;public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct IdInfo { public ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct HandleInfo { public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out HandleInfo info);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetBasicInfo(SafeFileHandle file, int kind, out BasicInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetIdInfo(SafeFileHandle file, int kind, out IdInfo info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetDriveTypeW(string root);
}
