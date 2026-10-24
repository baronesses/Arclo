using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Orbit.App.Platform;

public sealed record SafeFileSaveAsResult(string OutputPath, long Bytes);
public sealed class SafeFileSaveAsException(string message) : Exception(message);

/// <summary>Copies exact file bytes into an adjacent private file, verifies them and publishes without overwrite.</summary>
public static class SafeFileSaveAs
{
    public const long MaximumBytes = 8L * 1024 * 1024 * 1024;
    public static Task<SafeFileSaveAsResult> SaveAsync(string source, string destination, CancellationToken ct = default)
        => Task.Run(() => SaveCoreAsync(source, destination, ct), ct);

    private static async Task<SafeFileSaveAsResult> SaveCoreAsync(string source, string destination, CancellationToken token)
    {
        string? temporary = null;
        try
        {
            token.ThrowIfCancellationRequested();source = Normalize(source);destination = Normalize(destination);
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) throw new SafeFileSaveAsException(global::Orbit.Core.Localization.L.Get("Text_AF30CAB80E85"));
            var parent = Path.GetDirectoryName(destination)!;
            await Task.Run(() =>
            {
                RejectLinks(source, false);RejectLinks(parent, true);
                if (File.Exists(destination) || Directory.Exists(destination)) throw new SafeFileSaveAsException(global::Orbit.Core.Localization.L.Get("Text_A993EDBFE2AF"));
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            await using var input = await OpenSourceAsync(source, token).ConfigureAwait(false);
            var before = Snapshot(input);
            if (before.Bytes > MaximumBytes) throw new SafeFileSaveAsException(global::Orbit.Core.Localization.L.Get("Text_1B127357F062"));
            temporary = Path.Combine(parent, ".orbit-saveas-" + Guid.NewGuid().ToString("N") + ".tmp");
            var ownTemporary = temporary;
            var creating = Task.Run(() => new FileStream(ownTemporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan), CancellationToken.None);
            FileStream prepared;
            try { prepared = await creating.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
            catch
            {
                temporary = null;
                _ = creating.ContinueWith(async task =>
                {
                    if (task.IsCompletedSuccessfully) { await task.Result.DisposeAsync().ConfigureAwait(false);await CleanupAsync(ownTemporary).ConfigureAwait(false); }
                    else _ = task.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();throw;
            }
            long copied = 0;
            await using (prepared.ConfigureAwait(false))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer = new byte[65536];
                while (true)
                {
                    int count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);if (count == 0) break;
                    copied = checked(copied + count);
                    if (copied > before.Bytes || copied > MaximumBytes) throw new SafeFileSaveAsException(global::Orbit.Core.Localization.L.Get("Text_8F11E084D516"));
                    hash.AppendData(buffer, 0, count);await prepared.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                }
                if (copied != before.Bytes || prepared.Length != copied) throw new SafeFileSaveAsException(global::Orbit.Core.Localization.L.Get("Text_953C8029E4B0"));
                await prepared.FlushAsync(token).ConfigureAwait(false);prepared.Position = 0;
                var writtenHash = await SHA256.HashDataAsync(prepared, token).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), writtenHash)) throw new SafeFileSaveAsException(global::Orbit.Core.Localization.L.Get("Text_36D7D0C51ADF"));
                if (before != Snapshot(input)) throw new SafeFileSaveAsException(global::Orbit.Core.Localization.L.Get("Text_8F11E084D516"));
            }
            await Task.Run(() => { RejectLinks(source, false);RejectLinks(parent, true); }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            await using (var current = await OpenSourceAsync(source, token).ConfigureAwait(false))
                if (before != Snapshot(current)) throw new SafeFileSaveAsException(global::Orbit.Core.Localization.L.Get("Text_5B32E0DC9DD6"));
            token.ThrowIfCancellationRequested();File.Move(temporary, destination, false);temporary = null;
            return new(destination, copied);
        }
        catch (OperationCanceledException) { throw; }
        catch (SafeFileSaveAsException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or TimeoutException or System.ComponentModel.Win32Exception)
        { throw new SafeFileSaveAsException(global::Orbit.Core.Localization.L.Get("Text_03CECE0368BB")); }
        finally { if (temporary is not null) await CleanupAsync(temporary).ConfigureAwait(false); }
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl)) throw new SafeFileSaveAsException(global::Orbit.Core.Localization.L.Get("Text_A8A47AD4B6F4"));
        if (path[(Path.GetPathRoot(path)?.Length ?? 0)..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.EndsWith('.') || part.EndsWith(' ')))
            throw new SafeFileSaveAsException(global::Orbit.Core.Localization.L.Get("Text_49C38EA7C85C"));
        path = Path.GetFullPath(path);
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.Skip(Path.GetPathRoot(path)?.Length ?? 0).Contains(':'))
            throw new SafeFileSaveAsException(global::Orbit.Core.Localization.L.Get("Text_E712F84BBF63"));
        foreach (var part in path[(Path.GetPathRoot(path)?.Length ?? 0)..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.EndsWith('.') || part.EndsWith(' ')) throw new SafeFileSaveAsException(global::Orbit.Core.Localization.L.Get("Text_49C38EA7C85C"));
            var name = part.Split('.')[0];
            if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new SafeFileSaveAsException(global::Orbit.Core.Localization.L.Get("Text_2106DEC3C800"));
        }
        return path;
    }
    private static void RejectLinks(string path, bool directory)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) != 0) != directory)
            throw new SafeFileSaveAsException(global::Orbit.Core.Localization.L.Get("Text_A2396F67D278"));
        for (var parent = new DirectoryInfo(directory ? path : Path.GetDirectoryName(path)!); parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new SafeFileSaveAsException(global::Orbit.Core.Localization.L.Get("Text_1646F783D832"));
    }
    private static async Task<FileStream> OpenSourceAsync(string path, CancellationToken token)
    {
        var opening = Task.Run(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan), CancellationToken.None);
        try { return await opening.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
        catch { _ = opening.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose();else _ = t.Exception; }, TaskScheduler.Default);throw; }
    }
    private readonly record struct Stamp(long Bytes, uint Volume, ulong Id, long Creation, long LastWrite, long Change);
    private static Stamp Snapshot(FileStream stream)
    {
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info) || !GetFileInformationByHandleEx(stream.SafeFileHandle, 0, out var basic, (uint)Marshal.SizeOf<BasicInfo>()))
            throw new SafeFileSaveAsException(global::Orbit.Core.Localization.L.Get("Text_5D5FBB0920B7"));
        if ((info.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw new SafeFileSaveAsException(global::Orbit.Core.Localization.L.Get("Text_6D8E0E0E0729"));
        return new(((long)info.SizeHigh << 32) | info.SizeLow, info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow,
            basic.Creation, basic.LastWrite, basic.Change);
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfoNative { public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo { public long Creation, LastAccess, LastWrite, Change;public uint Attributes; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfoNative information);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out BasicInfo information, uint size);
    private static async Task CleanupAsync(string path)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var deleting = Task.Run(() => File.Delete(path), CancellationToken.None);
            try { await deleting.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { await Task.Delay(100).ConfigureAwait(false); }
            catch (TimeoutException) { _ = deleting.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);return; }
        }
    }
}
