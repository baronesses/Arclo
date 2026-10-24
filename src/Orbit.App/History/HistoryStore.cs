using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Orbit.Core;

namespace Orbit.App.History;

/// <summary>Bounded local history metadata. No source/output path is opened, deleted or executed.</summary>
public sealed class HistoryStore
{
    public const int MaximumEntries = 100;
    public const int MaximumHistoryBytes = 2 * 1024 * 1024;
    private const int MaximumInputEntries = 10000;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public HistoryStore(string? path = null)
    {
        FilePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arclo", "history.json");
    }

    public string FilePath { get; }

    public async Task<HistoryLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] bytes;
            await using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous))
            {
                if (stream.Length is <= 0 or > MaximumHistoryBytes) return Empty(global::Orbit.Core.Localization.L.Get("Text_E8F1CCD309DD"));
                bytes = new byte[(int)stream.Length];
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            // Parse the private byte snapshot after closing the handle. On Windows an open reader
            // can transiently deny replacement even when its sharing mode includes FileShare.Delete.
            return await Task.Run(() => Parse(bytes, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException) { return Empty(); }
        catch (DirectoryNotFoundException) { return Empty(); }
        catch (Exception exception) when (IsExpectedFailure(exception))
        { return Empty(global::Orbit.Core.Localization.L.Get("Text_860BC614FBC5")); }
        finally { _ioGate.Release(); }
    }

    /// <summary>The caller snapshots UI state before calling. Writes on one store are serialized in call order.</summary>
    private static ImageJobOptions SnapshotOptions(ImageJobOptions options,bool child=false)
    {
        var steps=options.ChainSteps;
        var archiveSelection=options.Action is ImageAction.SevenZipExtractSelected or ImageAction.TarExtractSelected;
        return options with{
            // Never enumerate or retain private member names in an asynchronous history snapshot.
            ArchiveSelectedEntries=Array.Empty<string>(),ArchiveSourceSha256=null,
            // Assets are per-invocation. No logo path/fingerprint enters a deferred history snapshot.
            WatermarkLogoPath=null,WatermarkLogoSha256=null,
            ZipSourceSha256=archiveSelection?null:options.ZipSourceSha256,
            ZipSelectedEntries=archiveSelection?Array.Empty<string>():options.ZipSelectedEntries is null||options.ZipSelectedEntries.Count>HistoryValidation.MaximumZipSelectedEntries?null!
                :options.ZipSelectedEntries.Count==0?Array.Empty<string>():options.ZipSelectedEntries.Take(HistoryValidation.MaximumZipSelectedEntries+1).ToArray(),
            ChainSteps=steps is null||steps.Count>4||child&&steps.Count!=0?null!
                :steps.Count==0?Array.Empty<ImageJobOptions>():steps.Take(5).Select(step=>step is null?null!:SnapshotOptions(step,true)).ToArray()
        };
    }
    public async Task<string?> SaveAsync(IEnumerable<HistoryEntry> entries, CancellationToken cancellationToken = default)
    {
        HistoryEntry[] snapshot;
        try
        {
            ArgumentNullException.ThrowIfNull(entries);
            snapshot = entries.Take(MaximumInputEntries + 1).Select(entry => entry is null ? null! : entry with
            { SourcePaths = entry.SourcePaths is null || entry.SourcePaths.Count > HistoryValidation.MaximumSourcePaths ? null! : entry.SourcePaths.ToArray(),
              Options = entry.Options is null ? null! : SnapshotOptions(entry.Options) }).ToArray();
            if (snapshot.Length > MaximumInputEntries) return global::Orbit.Core.Localization.L.Get("Text_EE0E76ACFE0A");
        }
        catch (Exception exception) when (IsExpectedFailure(exception)) { return global::Orbit.Core.Localization.L.Get("Text_05CDFDAF5FF8"); }
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var bytes = await Task.Run(() => Serialize(snapshot, cancellationToken), cancellationToken).ConfigureAwait(false);
            var target = Path.GetFullPath(FilePath);
            var directory = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, ".history-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await ReplacePreparedFileAsync(temporary, target, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return exception switch
            {
                UnauthorizedAccessException or SecurityException => global::Orbit.Core.Localization.L.Get("Text_562FEBC7FC63"),
                ArgumentException or JsonException or NotSupportedException => global::Orbit.Core.Localization.L.Get("Text_CFEA057183D9"),
                _ => global::Orbit.Core.Localization.L.Get("Text_FBAE8BF81918")
            };
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch (Exception exception) when (IsExpectedFailure(exception)) { }
            }
            _ioGate.Release();
        }
    }

    /// <summary>Atomically clears only this JSON metadata file. Sources and results are never accessed.</summary>
    public Task<string?> ClearAsync(CancellationToken cancellationToken = default) => SaveAsync(Array.Empty<HistoryEntry>(), cancellationToken);

    private static async Task ReplacePreparedFileAsync(string temporary, string target, CancellationToken cancellationToken)
    {
        // Retry the same complete, flushed file. Never truncate/delete the previous history first.
        // Total retry delay is bounded at 2175ms and remains cancellable while a reader/AV holds it.
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Move(temporary, target, overwrite: true); return; }
            catch (Exception exception) when (attempt < 10 && IsTransientReplacementFailure(exception))
            { await Task.Delay(Math.Min(300, 25 << Math.Min(attempt, 4)), cancellationToken).ConfigureAwait(false); }
        }
    }

    private static bool IsTransientReplacementFailure(Exception exception) => exception is UnauthorizedAccessException
        || exception is IOException && (exception.HResult & 0xffff) is 5 or 32 or 33 or 303;

    private static HistoryLoadResult Parse(byte[] bytes, CancellationToken cancellationToken)
    {
        var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        using var document = JsonDocument.Parse(bytes.AsMemory(offset), new JsonDocumentOptions { MaxDepth = 32 });
        if (!TryProperty(document.RootElement, "version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number is not (1 or 2)
            || !TryProperty(document.RootElement, "entries", out var entries) || entries.ValueKind != JsonValueKind.Array
            || entries.GetArrayLength() > MaximumInputEntries) return Empty(global::Orbit.Core.Localization.L.Get("Text_B4260595691A"));
        var valid = new List<HistoryEntry>();
        var rejected = false;
        foreach (var value in entries.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!HasRequiredFields(value) || !HistoryValidation.TryNormalize(value.Deserialize<HistoryEntry>(JsonOptions), startup: true, out var entry))
                { rejected = true; continue; }
                valid.Add(entry);
            }
            catch (JsonException) { rejected = true; }
        }
        var bounded = valid.OrderByDescending(entry => entry.CreatedUtc).DistinctBy(entry => entry.Id).Take(MaximumEntries).ToArray();
        return new HistoryLoadResult(Array.AsReadOnly(bounded), rejected ? global::Orbit.Core.Localization.L.Get("Text_05E1187E071C") : null);
    }

    private static byte[] Serialize(HistoryEntry[] snapshot, CancellationToken cancellationToken)
    {
        var valid = new List<HistoryEntry>();
        foreach (var entry in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HistoryValidation.TryNormalize(entry, startup: false, out var normalized)) throw new ArgumentException(global::Orbit.Core.Localization.L.Get("Text_186D3479887A"));
            valid.Add(normalized);
        }
        var bounded = valid.OrderByDescending(item => item.CreatedUtc).DistinctBy(item => item.Id).Take(MaximumEntries).ToArray();
        // Older readers ignore unknown option fields. Version 2 makes them reject protected
        // jobs instead of replaying Archive7z with the old plaintext default.
        var version = bounded.Any(entry => entry.Options.SevenZipPasswordProtected) ? 2 : 1;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteNumber("version", version); writer.WriteStartArray("entries"); writer.Flush();
            foreach (var entry in bounded)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
                if (stream.Length + record.Length + 256 > MaximumHistoryBytes) break;
                writer.WriteRawValue(record, skipInputValidation: true); writer.Flush();
            }
            writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush();
        }
        return stream.ToArray();
    }

    private static bool HasRequiredFields(JsonElement entry)
    {
        if (!TryProperty(entry, "id", out _) || !TryProperty(entry, "createdUtc", out _) || !TryProperty(entry, "sourcePaths", out _)
            || !TryProperty(entry, "status", out var status) || !KnownEnumName<HistoryStatus>(status)
            || !TryProperty(entry, "options", out var options) || !TryProperty(options, "action", out var action) || !KnownEnumName<ImageAction>(action)) return false;
        if (string.Equals(action.GetString(), nameof(ImageAction.ImageChain), StringComparison.OrdinalIgnoreCase))
        {
            if (!TryProperty(options, "chainSteps", out var steps) || steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() is < 2 or > 4) return false;
            foreach (var step in steps.EnumerateArray())
            {
                if (!TryProperty(step, "action", out var stepAction) || !KnownEnumName<ImageAction>(stepAction)) return false;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in step.EnumerateObject()) if (!names.Add(property.Name)) return false;
            }
        }
        return true;
    }

    private static bool KnownEnumName<T>(JsonElement value) where T : struct, Enum => value.ValueKind == JsonValueKind.String
        && Enum.GetNames<T>().Contains(value.GetString(), StringComparer.OrdinalIgnoreCase);

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object) return false;
        var found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found) return false;
            value = property.Value; found = true;
        }
        return found;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, MaxDepth = 32, WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private static HistoryLoadResult Empty(string? error = null) => new(Array.Empty<HistoryEntry>(), error);
    private static bool IsExpectedFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or JsonException
        or ArgumentException or NotSupportedException or SecurityException;
}
