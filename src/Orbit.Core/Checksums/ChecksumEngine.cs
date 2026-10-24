using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Orbit.Core.Checksums;

/// <summary>Read-only SHA256 checks. Manifest paths are literal relative paths below the explicitly selected root.</summary>
public sealed class ChecksumEngine
{
    public const long MaximumFileBytes = 16L * 1024 * 1024 * 1024;
    public const long MaximumManifestBytes = 2L * 1024 * 1024;
    public const int MaximumManifestEntries = 10000;
    public const long MaximumManifestEntryBytes = 1024L * 1024 * 1024;
    public const long MaximumManifestTotalBytes = 2L * 1024 * 1024 * 1024;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private readonly SemaphoreSlim _ioGate = new(2, 2);
    internal Action<string>? HashStarted { get; set; }

    public ChecksumEngine(TimeSpan? operationTimeout = null)
    {
        _timeout = operationTimeout ?? TimeSpan.FromMinutes(10);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }

    public async Task<ChecksumFileResult> VerifyFileAsync(string path, string expectedSha256, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();var expected = NormalizeHash(expectedSha256);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(_timeout);var token = deadline.Token;
        var full = path;long bytes = 0;
        try
        {
            full = NormalizeAbsolutePath(path);progress?.Report(0);
            using var lease = await OpenFileAsync(full, MaximumFileBytes, token).ConfigureAwait(false);bytes = lease.Bytes;
            var actual = await HashAsync(lease, progress, token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
            return Result(full, expected, actual, bytes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Error(full, expected, bytes, global::Orbit.Core.Localization.L.Get("Text_F17A371B982F")); }
        catch (Exception exception) when (Expected(exception)) { return Failure(full, expected, bytes, exception); }
    }

    public async Task<ChecksumVerificationResult> VerifyManifestAsync(string manifestPath, string rootDirectory, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(_timeout);var token = deadline.Token;
        try
        {
            progress?.Report(0);var root = NormalizeAbsolutePath(rootDirectory);var source = NormalizeAbsolutePath(manifestPath);
            using var rootLease = await OpenDirectoryAsync(root, token).ConfigureAwait(false);
            using var manifest = await OpenFileAsync(source, MaximumManifestBytes, token).ConfigureAwait(false);
            var data = await ReadSnapshotAsync(manifest, token).ConfigureAwait(false);
            // Decode and validate the complete manifest before touching any referenced file.
            var entries = await Task.Run(() => ParseManifest(data, root, token), token).ConfigureAwait(false);
            var plan = new List<PlannedEntry>(entries.Count);var identities = new HashSet<string>(StringComparer.Ordinal);long plannedBytes = 0;
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var file = await OpenFileAsync(entry.Path, long.MaxValue, token).ConfigureAwait(false);
                    if (!identities.Add(file.Identity)) throw new ManifestLimitException(global::Orbit.Core.Localization.L.Get("Text_B43BE2C212B9"));
                    if (file.Bytes > MaximumManifestEntryBytes) throw new ManifestLimitException(global::Orbit.Core.Localization.L.Get("Text_E045A018B7DE"));
                    plannedBytes = checked(plannedBytes + file.Bytes);
                    if (plannedBytes > MaximumManifestTotalBytes) throw new ManifestLimitException(global::Orbit.Core.Localization.L.Get("Text_67A33A0BA69E"));
                    plan.Add(new PlannedEntry(entry, file.Bytes, file.Identity, null));
                }
                catch (ManifestLimitException) { throw; }
                catch (Exception exception) when (Expected(exception)) { plan.Add(new PlannedEntry(entry, 0, null, Failure(entry.Path, entry.Expected, 0, exception))); }
            }
            var results = new List<ChecksumFileResult>(plan.Count);long totalBytes = 0;
            for (var index = 0; index < plan.Count; index++)
            {
                token.ThrowIfCancellationRequested();var item = plan[index];ChecksumFileResult result;
                if (item.Failure is { } failure) result = failure;
                else
                {
                    try
                    {
                        using var file = await OpenFileAsync(item.Entry.Path, MaximumManifestEntryBytes, token).ConfigureAwait(false);
                        if (file.Bytes != item.Bytes || file.Identity != item.Identity) throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_E9D2886CC48D"));
                        var currentIndex = index;var current = new CallbackProgress(value => progress?.Report((currentIndex + value) / plan.Count));
                        var actual = await HashAsync(file, current, token).ConfigureAwait(false);result = Result(item.Entry.Path, item.Entry.Expected, actual, file.Bytes);totalBytes = checked(totalBytes + file.Bytes);
                    }
                    catch (Exception exception) when (Expected(exception)) { result = Failure(item.Entry.Path, item.Entry.Expected, 0, exception); }
                }
                results.Add(result);progress?.Report((index + 1d) / plan.Count);
            }
            token.ThrowIfCancellationRequested();return new ChecksumVerificationResult(results.AsReadOnly(), results.Count > 0 && results.All(item => item.Status == ChecksumStatus.Match), totalBytes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_7C3D215C24DF")); }
        catch (ChecksumProcessingException) { throw; }
        catch (DecoderFallbackException exception) { throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_0B675BFC7738"), exception); }
        catch (Exception exception) when (Expected(exception)) { throw new ChecksumProcessingException(Explain(exception), exception); }
    }

    private async Task<string> HashAsync(FileLease lease, IProgress<double>? progress, CancellationToken token)
    {
        await WaitForIoAsync(token).ConfigureAwait(false);lease.Retain();
        var worker = Task.Run(async () =>
        {
            try
            {
                HashStarted?.Invoke(lease.Path);token.ThrowIfCancellationRequested();using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];long copied = 0;int read;
                while ((read = await lease.Input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    token.ThrowIfCancellationRequested();copied = checked(copied + read);if (copied > lease.Bytes) throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_15E88E899D81"));
                    hash.AppendData(buffer.AsSpan(0, read));progress?.Report(lease.Bytes == 0 ? 0 : copied / (double)lease.Bytes);
                }
                if (copied != lease.Bytes || lease.Input.Length != lease.Bytes) throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_15E88E899D81"));
                token.ThrowIfCancellationRequested();var result = Convert.ToHexString(hash.GetHashAndReset());progress?.Report(1);return result;
            }
            finally { lease.Dispose();_ioGate.Release(); }
        }, CancellationToken.None);
        try { return await worker.WaitAsync(token).ConfigureAwait(false); } catch { Observe(worker);throw; }
    }

    private async Task<byte[]> ReadSnapshotAsync(FileLease lease, CancellationToken token)
    {
        await WaitForIoAsync(token).ConfigureAwait(false);lease.Retain();
        var worker = Task.Run(async () =>
        {
            try
            {
                var bytes = new byte[checked((int)lease.Bytes)];await lease.Input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                if (lease.Input.Length != lease.Bytes) throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_F05B06A7993E"));return bytes;
            }
            finally { lease.Dispose();_ioGate.Release(); }
        }, CancellationToken.None);
        try { return await worker.WaitAsync(token).ConfigureAwait(false); } catch { Observe(worker);throw; }
    }
    private async Task WaitForIoAsync(CancellationToken token)
    {
        if (!await _ioGate.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false)) throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_463F46C78EEE"));
    }
    private async Task<T> OpenAsync<T>(Func<T> open, CancellationToken token) where T : IDisposable
    {
        if (!await _openGate.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false)) throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_4C0300510CB5"));
        var pending = Task.Run(() => { try { token.ThrowIfCancellationRequested();return open(); } finally { _openGate.Release(); } }, CancellationToken.None);
        try { return await pending.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
        catch { _ = pending.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose();else _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);throw; }
    }
    private Task<FileLease> OpenFileAsync(string path, long maximum, CancellationToken token) => OpenAsync(() => FileLease.Open(path, maximum, token), token);
    private Task<DirectoryLease> OpenDirectoryAsync(string path, CancellationToken token) => OpenAsync(() => DirectoryLease.Open(path, token), token);

    private static IReadOnlyList<ManifestEntry> ParseManifest(byte[] bytes, string root, CancellationToken token)
    {
        var text = new UTF8Encoding(false, true).GetString(bytes);if (text.StartsWith('\uFEFF')) text = text[1..];
        var entries = new List<ManifestEntry>();var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);var number = 0;
        foreach (var original in text.Split('\n'))
        {
            token.ThrowIfCancellationRequested();number++;var line = original.EndsWith('\r') ? original[..^1] : original;
            if (string.IsNullOrWhiteSpace(line) || line.AsSpan().TrimStart().StartsWith("#")) continue;
            if (line.Length < 67 || line[64] != ' ' || line[65] is not (' ' or '*')) throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Format("Text_31AD7ADCFA15",(object?)(number)));
            string expected;
            try { expected = NormalizeHash(line[..64]); } catch (ChecksumProcessingException exception) { throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Format("Text_16B9EC4A37E5",(object?)(number)), exception); }
            var relative = ValidateRelativeName(line[66..]);
            if (!aliases.Add(relative.Normalize(NormalizationForm.FormC))) throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Format("Text_EE0E7234AA8C",(object?)(number)));
            if (entries.Count >= MaximumManifestEntries) throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_E72CE2795F87"));
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            var rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (full.Length > 32760 || !full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_FA3906DA2CF0"));
            entries.Add(new ManifestEntry(full, expected));
        }
        if (entries.Count == 0) throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_EDDB6D0E56C5"));
        return entries.AsReadOnly();
    }
    private static string NormalizeHash(string hash)
    {
        if (hash is null) throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_5C345846D50A"));
        var value = hash.Trim();if (value.Length != 64 || value.Any(character => !IsHex(character))) throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_5C345846D50A"));return value.ToUpperInvariant();
    }
    private static bool IsHex(char character) => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    private static string ValidateRelativeName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 32760 || name[0] is '/' or '\\' || name.Contains(':')) throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_4A6B58C9E34E"));
        var value = name.Replace('\\', '/');var parts = value.Split('/');
        if (parts.Length > 128) throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_8EA2F9CB84BB"));
        foreach (var part in parts) ValidateSegment(part);return value;
    }
    private static string NormalizeAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile) throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_EF4517D30B0A"));
        var raw = path.Replace('/', '\\');var offset = raw.Length > 1 && raw[1] == ':' ? 2 : 0;
        if (raw.IndexOf(':', offset) >= 0) throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_37C0B7961546"));
        foreach (var part in raw[offset..].Split('\\', StringSplitOptions.RemoveEmptyEntries)) ValidateSegment(part);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));if (full.Length > 32760) throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_DD5CDC0ACCD7"));return full;
    }
    private static void ValidateSegment(string part)
    {
        if (part.Length is < 1 or > 255 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character)))
            throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_B27BFCA183CD"));
        _ = part.Normalize(NormalizationForm.FormC);
        var stem = part.Split('.')[0].Trim().ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$"
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3]))
            throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_3A3FF9230E38"));
    }

    private static ChecksumFileResult Result(string path, string expected, string actual, long bytes) => new(path, expected, actual,
        string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase) ? ChecksumStatus.Match : ChecksumStatus.Mismatch, bytes,
        string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase) ? global::Orbit.Core.Localization.L.Get("Text_2ED5E1152438") : global::Orbit.Core.Localization.L.Get("Text_81DAF98A5EEB"));
    private static ChecksumFileResult Error(string path, string expected, long bytes, string message) => new(path, expected, null, ChecksumStatus.Error, bytes, message);
    private static ChecksumFileResult Failure(string path, string expected, long bytes, Exception exception) => exception is FileNotFoundException or DirectoryNotFoundException
        ? new(path, expected, null, ChecksumStatus.Missing, 0, global::Orbit.Core.Localization.L.Get("Text_93CF0C841FC3")) : Error(path, expected, bytes, Explain(exception));
    private static string Explain(Exception exception) => exception switch
    {
        ChecksumProcessingException => exception.Message,
        FileNotFoundException or DirectoryNotFoundException => global::Orbit.Core.Localization.L.Get("Text_818FECB6B475"),
        UnauthorizedAccessException => global::Orbit.Core.Localization.L.Get("Text_BB9E7F69A9AC"),
        TimeoutException => global::Orbit.Core.Localization.L.Get("Text_4E0DDE44AEA4"),
        DecoderFallbackException => global::Orbit.Core.Localization.L.Get("Text_0CE53406ACF2"),
        _ => global::Orbit.Core.Localization.L.Get("Text_0CD22284B16C")
    };
    private static bool Expected(Exception exception) => exception is ChecksumProcessingException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or TimeoutException or OverflowException or System.Security.SecurityException;
    private static void Observe(Task task) => _ = task.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private sealed record ManifestEntry(string Path, string Expected);
    private sealed record PlannedEntry(ManifestEntry Entry, long Bytes, string? Identity, ChecksumFileResult? Failure);
    private sealed class ManifestLimitException(string message) : ChecksumProcessingException(message);
    private sealed class CallbackProgress(Action<double> callback) : IProgress<double> { public void Report(double value) => callback(value); }

    private sealed class FileLease : IDisposable
    {
        private readonly DirectoryLease _parents;private int _references = 1;
        internal FileStream Input { get; }
        internal string Path { get; }
        internal long Bytes { get; }
        internal string Identity { get; }
        private FileLease(string path, FileStream input, DirectoryLease parents, string identity) { Path = path;Input = input;_parents = parents;Bytes = input.Length;Identity = identity; }
        internal void Retain() => Interlocked.Increment(ref _references);
        public void Dispose() { if (Interlocked.Decrement(ref _references) != 0) return;try { Input.Dispose(); } finally { _parents.Dispose(); } }
        internal static FileLease Open(string path, long maximum, CancellationToken token)
        {
            var parents = DirectoryLease.Open(System.IO.Path.GetDirectoryName(path)!, token);FileStream? input = null;
            try
            {
                token.ThrowIfCancellationRequested();string identity;
                if (OperatingSystem.IsWindows())
                {
                    var handle = Native.Open(path, directory: false);
                    try
                    {
                        var info = Native.Info(handle);Native.Validate(info, directory: false);
                        if (info.Links != 1) throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_7BAB1623C60F"));
                        identity = info.Volume + ":" + info.IndexHigh + ":" + info.IndexLow;
                        input = new FileStream(handle, FileAccess.Read, 81920, isAsync: true);
                    }
                    catch { handle.Dispose();throw; }
                }
                else
                {
                    if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_68D2A33C1AE6"));
                    input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);identity = path;
                }
                if (input.Length < 0 || input.Length > maximum) throw new ChecksumProcessingException(maximum == MaximumManifestBytes ? global::Orbit.Core.Localization.L.Get("Text_E2CF66277432") : maximum == MaximumManifestEntryBytes ? global::Orbit.Core.Localization.L.Get("Text_BE2FC1181DE4") : global::Orbit.Core.Localization.L.Get("Text_48AD782CDD06"));
                token.ThrowIfCancellationRequested();return new FileLease(path, input, parents, identity);
            }
            catch { input?.Dispose();parents.Dispose();throw; }
        }
    }
    private sealed class DirectoryLease : IDisposable
    {
        private readonly List<SafeFileHandle> _handles;
        private DirectoryLease(List<SafeFileHandle> handles) { _handles = handles; }
        public void Dispose() { for (var index = _handles.Count - 1; index >= 0; index--) _handles[index].Dispose(); }
        internal static DirectoryLease Open(string path, CancellationToken token)
        {
            var handles = new List<SafeFileHandle>();
            try
            {
                var root = System.IO.Path.GetPathRoot(path) ?? throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_BDE2EE89D29E"));
                var current = root;var parts = path[root.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in new[] { string.Empty }.Concat(parts))
                {
                    token.ThrowIfCancellationRequested();if (part.Length > 0) current = System.IO.Path.Combine(current, part);
                    if (handles.Count > 256) throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_79647D12D68F"));
                    if (OperatingSystem.IsWindows())
                    {
                        var handle = Native.Open(current, directory: true);handles.Add(handle);Native.Validate(Native.Info(handle), directory: true);
                    }
                    else
                    {
                        var flags = File.GetAttributes(current);if ((flags & FileAttributes.ReparsePoint) != 0 || (flags & FileAttributes.Directory) == 0) throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_DEB6656F2D0A"));
                    }
                }
                return new DirectoryLease(handles);
            }
            catch { foreach (var handle in handles) handle.Dispose();throw; }
        }
    }
    private static class Native
    {
        internal static SafeFileHandle Open(string path, bool directory)
        {
            var nativePath = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
            // Each ancestor is held without delete sharing, and OPEN_REPARSE_POINT prevents following the leaf.
            var handle = CreateFileW(nativePath, directory ? 0x80u : 0x80000000u, directory ? 3u : 1u, IntPtr.Zero, 3,
                0x00200000u | (directory ? 0x02000000u : 0x40000000u | 0x08000000u), IntPtr.Zero);
            if (!handle.IsInvalid) return handle;var error = Marshal.GetLastWin32Error();handle.Dispose();
            throw error switch { 2 => new FileNotFoundException(global::Orbit.Core.Localization.L.Get("Text_93CF0C841FC3"), path), 3 => new DirectoryNotFoundException(global::Orbit.Core.Localization.L.Get("Text_E09B25C4616B")), 5 => new UnauthorizedAccessException(global::Orbit.Core.Localization.L.Get("Text_0C46ADF6F477")), _ => new IOException(global::Orbit.Core.Localization.L.Get("Text_6642245B70B1"), new Win32Exception(error)) };
        }
        internal static FileInfoNative Info(SafeFileHandle handle)
        { if (!GetFileInformationByHandle(handle, out var info)) throw new IOException(global::Orbit.Core.Localization.L.Get("Text_9FC4E5A6B587"), new Win32Exception(Marshal.GetLastWin32Error()));return info; }
        internal static void Validate(FileInfoNative info, bool directory)
        {
            if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || ((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory)
                throw new ChecksumProcessingException(global::Orbit.Core.Localization.L.Get("Text_245B35E02C69"));
            // All lexical components are opened with OPEN_REPARSE_POINT and retained without
            // delete sharing. Together with strict relative paths this prevents redirecting a
            // target outside the selected root, without requiring directory-enumeration rights.
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileInfoNative
        {
            internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfoNative info);
    }
}
