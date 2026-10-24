using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Orbit.App.Personalization;

public sealed record FavoritePresetLoadResult(IReadOnlyList<string> Ids, string? Error, bool ChangesBlocked);
public sealed record FavoritePresetSaveResult(bool Success, IReadOnlyList<string> Ids, string? Error, bool ChangesBlocked);

/// <summary>Ordered favorite IDs only. Loading never executes a preset, opens its inputs or writes a repair.</summary>
public sealed class FavoritePresetStore
{
    public const int MaximumFavorites = 16;
    public const int MaximumIdLength = 128;
    public const int MaximumFileBytes = 16 * 1024;
    private const int MaximumInputIds = 256;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<string> _ids = Array.Empty<string>();
    private bool _blocked;
    private string? _warning;

    /// <summary>Construction performs no I/O. Inject a workspace path for diagnostics.</summary>
    public FavoritePresetStore(string? path = null) => FilePath = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arclo", "favorites.json");

    public string FilePath { get; }

    public async Task<FavoritePresetLoadResult> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await LoadCoreAsync(ct).ConfigureAwait(false); return new(_ids, _warning, _blocked); }
        finally { _gate.Release(); }
    }

    /// <summary>Replaces IDs in caller order. Duplicate IDs are removed ordinally, preserving the first.
    /// A malformed/unreadable persisted file blocks changes until the user explicitly calls ResetAsync.</summary>
    public async Task<FavoritePresetSaveResult> SaveAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Snapshot before the first await: the UI may mutate its collection while this save is queued.
        IReadOnlyList<string>? normalized = null;
        try { normalized = Normalize(ids, ct); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-read before every write: a different process/editor may have corrupted the file since Load.
            await LoadCoreAsync(ct).ConfigureAwait(false);
            if (_blocked) return Failure(_warning ?? global::Orbit.Core.Localization.L.Get("Text_60188813D1D0"));
            if (normalized is null) return Failure(global::Orbit.Core.Localization.L.Get("Text_F79B7E6DAF96"));
            return await SaveCoreAsync(normalized, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Explicit recovery: atomically writes an empty favorite-ID list. Presets and outputs are never touched.</summary>
    public async Task<FavoritePresetSaveResult> ResetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await SaveCoreAsync(Array.Empty<string>(), ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task LoadCoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); if (_blocked) return;
        try
        {
            var reading = Task.Run(async () =>
            {
                for (var attempt = 0; ; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                            4096, FileOptions.Asynchronous);
                        ct.ThrowIfCancellationRequested();
                        if (stream.Length is <= 0 or > MaximumFileBytes) throw new JsonException("Invalid favorite file size.");
                        var bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
                        if (stream.Position != stream.Length) throw new JsonException("Favorite file changed while reading.");
                        return bytes;
                    }
                    catch (Exception exception) when (attempt < 4 && Transient(exception))
                    { await Task.Delay(25 << attempt, ct).ConfigureAwait(false); }
                }
            }, CancellationToken.None);
            byte[] snapshot;
            try { snapshot = await reading.WaitAsync(ct).ConfigureAwait(false); }
            catch
            {
                _ = reading.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); throw;
            }
            // The read handle is closed before parsing or a later atomic rename.
            _ids = Parse(snapshot, ct); _warning = null;
        }
        catch (FileNotFoundException) { _ids = Array.Empty<string>(); _warning = null; }
        catch (DirectoryNotFoundException) { _ids = Array.Empty<string>(); _warning = null; }
        catch (Exception exception) when (Expected(exception))
        {
            _blocked = true;
            _warning = exception is JsonException or ArgumentException
                ? global::Orbit.Core.Localization.L.Get("Text_E13A4EEFFFD1")
                : global::Orbit.Core.Localization.L.Get("Text_B8AA4A236257");
        }
    }

    private async Task<FavoritePresetSaveResult> SaveCoreAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        string? temporary = null;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, ids }, new JsonSerializerOptions { WriteIndented = true });
            if (bytes.Length > MaximumFileBytes) return Failure(global::Orbit.Core.Localization.L.Get("Text_931FF6B5F807"));
            var target = Path.GetFullPath(FilePath); var parent = Path.GetDirectoryName(target)!;
            temporary = Path.Combine(parent, ".orbit-favorites-" + Guid.NewGuid().ToString("N") + ".tmp");
            var prepared = temporary;
            await Task.Run(async () =>
            {
                ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(parent);
                await using var stream = new FileStream(prepared, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false); await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }, CancellationToken.None).ConfigureAwait(false);
            for (var attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try { File.Move(temporary, target, overwrite: true); break; }
                catch (Exception exception) when (attempt < 10 && Transient(exception))
                { await Task.Delay(Math.Min(300, 25 << Math.Min(attempt, 4)), ct).ConfigureAwait(false); }
            }
            _ids = ids; _warning = null; _blocked = false;
            return new(true, _ids, null, false);
        }
        catch (Exception exception) when (Expected(exception))
        { return Failure(global::Orbit.Core.Localization.L.Get("Text_AEBD60F26AC0")); }
        finally
        {
            if (temporary is not null)
                for (var attempt = 0; attempt < 11; attempt++)
                {
                    try { File.Delete(temporary); break; }
                    catch (Exception exception) when (Expected(exception))
                    { if (attempt < 10) await Task.Delay(Math.Min(300, 25 << Math.Min(attempt, 4))).ConfigureAwait(false); }
                }
        }
    }

    private FavoritePresetSaveResult Failure(string error) => new(false, _ids, error, _blocked);

    private static IReadOnlyList<string> Parse(byte[] bytes, CancellationToken ct)
    {
        var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        using var json = JsonDocument.Parse(bytes.AsMemory(offset), new JsonDocumentOptions { MaxDepth = 4 });
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); JsonElement ids = default; var versionSeen = false;
        foreach (var field in json.RootElement.EnumerateObject())
        {
            ct.ThrowIfCancellationRequested(); if (!seen.Add(field.Name)) throw new JsonException();
            if (field.Name.Equals("version", StringComparison.OrdinalIgnoreCase))
            {
                if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out var version) || version != 1) throw new JsonException();
                versionSeen = true;
            }
            else if (field.Name.Equals("ids", StringComparison.OrdinalIgnoreCase)) ids = field.Value;
            else throw new JsonException(); // The file contains identifiers only, never recipes or paths.
        }
        if (!versionSeen || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() > MaximumFavorites) throw new JsonException();
        var values = new List<string>(); var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in ids.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested(); if (item.ValueKind != JsonValueKind.String) throw new JsonException();
            string? text;
            try { text = item.GetString(); } catch (InvalidOperationException exception) { throw new JsonException("Invalid favorite ID encoding.", exception); }
            var value = NormalizeId(text); if (!distinct.Add(value)) throw new JsonException(); values.Add(value);
        }
        return Array.AsReadOnly(values.ToArray());
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids); var values = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal); var count = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested(); if (++count > MaximumInputIds) throw new ArgumentException();
            var value = NormalizeId(id); if (!seen.Add(value)) continue; values.Add(value);
            if (values.Count > MaximumFavorites) throw new ArgumentException();
        }
        return Array.AsReadOnly(values.ToArray());
    }

    private static string NormalizeId(string? id)
    {
        if (id is null || id.Length > 256) throw new ArgumentException();
        for (var index = 0; index < id.Length; index++)
        {
            var character = id[index]; var category = char.GetUnicodeCategory(character);
            if (char.IsControl(character) || category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                throw new ArgumentException();
            if (!char.IsSurrogate(character)) continue;
            if (!char.IsHighSurrogate(character) || index + 1 >= id.Length || !char.IsLowSurrogate(id[++index])) throw new ArgumentException();
        }
        var result = id.Trim(); if (result.Length is < 1 or > MaximumIdLength) throw new ArgumentException(); return result;
    }

    private static bool Transient(Exception exception) => exception is UnauthorizedAccessException
        || exception is IOException && (exception.HResult & 0xffff) is 5 or 32 or 33 or 303;
    private static bool Expected(Exception exception) => exception is IOException or UnauthorizedAccessException or JsonException
        or ArgumentException or NotSupportedException or SecurityException;
}
