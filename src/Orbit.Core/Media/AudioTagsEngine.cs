using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Orbit.Core.Media;

/// <summary>New verified stream-copy audio files with literal title/artist/album edits.
/// Selected audio packets, decoded PCM and every supported attached picture are compared before publication.</summary>
public sealed class AudioTagsEngine
{
    public const long MaximumSourceBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumOutputBytes = MaximumSourceBytes;
    public const int MaximumProbeBytes = 4 * 1024 * 1024;
    public const int MaximumCoverBytes = 32 * 1024 * 1024;
    public const double MaximumDurationSeconds = 86400;
    private const string Prefix = ".orbit-audio-tags-";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly MediaEngine _media;
    private readonly TimeSpan _timeout;
    internal Action<string>? NativeStarted { get; set; }
    public AudioTagsEngine(string? ffmpegExecutable = null, TimeSpan? operationTimeout = null)
    {
        _media = new MediaEngine(ffmpegExecutable); _timeout = operationTimeout ?? TimeSpan.FromHours(2);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }
    public string ExecutablePath => _media.ExecutablePath;
    public string ProbeExecutablePath => _media.ProbeExecutablePath;

    public async Task<AudioTagMetadata> InspectAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); using var deadline = Deadline(ct);
        try
        {
            var source = NormalizePath(path); await using var lease = await OpenInputAsync(source, deadline.Token).ConfigureAwait(false);
            await CheckRawTagsAsync(lease, Path.GetExtension(source), deadline.Token).ConfigureAwait(false);
            var info = await ProbeAsync(source, deadline.Token).ConfigureAwait(false);
            return new AudioTagMetadata(source, info.Format, info.Title, info.Artist, info.Album);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw TimeoutError(); }
        catch (Exception exception) when (Expected(exception)) { throw Explain(exception); }
    }

    public async Task<MediaResult> UpdateAsync(string path, AudioTagOptions options, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options); ct.ThrowIfCancellationRequested();
        if (!options.SetTitle && !options.SetArtist && !options.SetAlbum) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_E657468D2321"));
        if (options.SetTitle) ValidateValue(options.Title); if (options.SetArtist) ValidateValue(options.Artist); if (options.SetAlbum) ValidateValue(options.Album);
        using var deadline = Deadline(ct); var token = deadline.Token; string? temporary = null, parent = null;
        try
        {
            var source = NormalizePath(path); progress?.Report(0); await using var lease = await OpenInputAsync(source, token).ConfigureAwait(false);
            await CheckRawTagsAsync(lease, Path.GetExtension(source), token).ConfigureAwait(false);
            var before = await ProbeAsync(source, token).ConfigureAwait(false);
            var desired = (Title: options.SetTitle ? options.Title : before.Title, Artist: options.SetArtist ? options.Artist : before.Artist, Album: options.SetAlbum ? options.Album : before.Album);
            var audioProof = await ProveAudioAsync(source, before, token).ConfigureAwait(false); progress?.Report(.2);
            var pictureProofs = new List<string>();
            foreach (var cover in before.Covers) pictureProofs.Add(await ProveCoverAsync(source, cover, token).ConfigureAwait(false));
            progress?.Report(.3);
            parent = Path.TrimEndingDirectorySeparator(string.IsNullOrWhiteSpace(options.OutputDirectory) ? Path.GetDirectoryName(source)! : Path.GetFullPath(options.OutputDirectory));
            if (!await Task.Run(() => Directory.Exists(parent), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_F5D6DF7FEC9A"));
            temporary = Path.Combine(parent, Prefix + Guid.NewGuid().ToString("N")); var working = temporary; var directory = parent;
            var creating = Task.Run(() => Directory.CreateDirectory(working), CancellationToken.None);
            try { await creating.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
            catch { temporary = null; _ = creating.ContinueWith(async task => { if (task.IsCompletedSuccessfully) await CleanupAsync(working, directory).ConfigureAwait(false); else _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap(); throw; }
            var extension = before.Format.ToLowerInvariant(); var prepared = Path.Combine(temporary, "result." + extension); var args = Input(source);
            args.AddRange(["-map", "0:" + before.AudioIndex.ToString(CultureInfo.InvariantCulture)]);
            foreach (var cover in before.Covers) args.AddRange(["-map", "0:" + cover.Index.ToString(CultureInfo.InvariantCulture)]);
            args.AddRange(["-c", "copy", "-map_metadata", "0", "-map_chapters", "-1"]);
            foreach (var pair in new[] { ("title", desired.Title), ("artist", desired.Artist), ("album", desired.Album) })
                args.AddRange(["-metadata", pair.Item1 + "=" + pair.Item2, "-metadata:s:a:0", pair.Item1 + "="]);
            for (var index = 0; index < before.Covers.Count; index++) args.AddRange(["-disposition:v:" + index.ToString(CultureInfo.InvariantCulture), "attached_pic"]);
            if (before.Format == "MP3") args.AddRange(["-id3v2_version", "4", "-write_id3v1", "0", "-write_xing", "1"]);
            else if (before.Format == "M4A") args.AddRange(["-movflags", "+faststart"]);
            args.AddRange(["-fs", (MaximumOutputBytes + 1).ToString(CultureInfo.InvariantCulture), "-progress", "pipe:1", "-stats_period", "0.1", "-f", before.Format == "M4A" ? "ipod" : extension, prepared]);
            var duration = audioProof.Samples / (double)before.Rate; double last = .3; Recheck(source, token);
            await RunAsync(ExecutablePath, args, token, line =>
            {
                if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) || !long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds)) return;
                var value = .3 + .3 * Math.Clamp(microseconds / 1_000_000d / duration, 0, 1); if (value > last) { last = value; progress?.Report(value); }
            }).ConfigureAwait(false);
            var bytes = new FileInfo(prepared).Length;
            if (bytes is <= 0 or > MaximumOutputBytes) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_760ACD77F6D3"));
            progress?.Report(.65); await using var outputLease = await OpenInputAsync(prepared, token).ConfigureAwait(false);
            await CheckRawTagsAsync(outputLease, Path.GetExtension(prepared), token).ConfigureAwait(false);
            var after = await ProbeAsync(prepared, token).ConfigureAwait(false);
            if (after.Format != before.Format || after.Codec != before.Codec || after.Rate != before.Rate || after.Channels != before.Channels || after.Covers.Count != before.Covers.Count
                || after.Title != desired.Title || after.Artist != desired.Artist || after.Album != desired.Album)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_F79C4A5E6F5B"));
            var actualProof = await ProveAudioAsync(prepared, after, token).ConfigureAwait(false);
            if (actualProof != audioProof) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_D3652CF573E7"));
            progress?.Report(.85);
            for (var index = 0; index < before.Covers.Count; index++)
            {
                var left = before.Covers[index]; var right = after.Covers[index];
                if (left.Codec != right.Codec || left.Width != right.Width || left.Height != right.Height || pictureProofs[index] != await ProveCoverAsync(prepared, right, token).ConfigureAwait(false))
                    throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_71936CB47968"));
            }
            // Release only the newly prepared file before its atomic rename; source stays read-locked.
            await outputLease.DisposeAsync().ConfigureAwait(false); token.ThrowIfCancellationRequested();
            var published = Commit(prepared, parent, source, extension, token); progress?.Report(1);
            return new MediaResult(published, bytes, 0, 0, before.Format, lease.Length);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw TimeoutError(); }
        catch (Exception exception) when (Expected(exception)) { throw Explain(exception); }
        finally { await CleanupAsync(temporary, parent).ConfigureAwait(false); }
    }

    private async Task<AudioInfo> ProbeAsync(string path, CancellationToken token)
    {
        var text = await RunAsync(ProbeExecutablePath, ["-v", "error", "-threads", "2", "-max_alloc", "536870912", "-max_pixels", "40000000", "-skip_frame", "all", "-format_whitelist", "mp3,flac,mov", "-protocol_whitelist", "file,pipe", "-show_entries", "format=format_name,duration:format_tags:stream=index,codec_type,codec_name,sample_rate,channels,width,height,duration:stream_disposition=attached_pic:stream_tags:chapter=id", "-of", "json", "-i", path], token).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(text); var root = json.RootElement; var format = root.GetProperty("format"); var streams = root.GetProperty("streams");
            if (streams.GetArrayLength() is < 1 or > 9 || root.TryGetProperty("chapters", out var chapters) && chapters.GetArrayLength() != 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_1EB396657521"));
            JsonElement? audio = null; var covers = new List<Cover>(); var indices = new HashSet<int>();
            foreach (var stream in streams.EnumerateArray())
            {
                var index = Integer(stream, "index", -1); if (index is < 0 or > 65535 || !indices.Add(index)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_B4DB412CDA95"));
                if (Text(stream, "codec_type") == "audio") { if (audio.HasValue) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_82E0A9A47BC6")); audio = stream; }
                else
                {
                    if (Text(stream, "codec_type") != "video" || Text(stream, "codec_name") is not ("mjpeg" or "png") || !stream.TryGetProperty("disposition", out var disposition) || Integer(disposition, "attached_pic") != 1)
                        throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_E84BF2299B66"));
                    var width = Integer(stream, "width"); var height = Integer(stream, "height");
                    if (width is < 1 or > 16000 || height is < 1 or > 16000 || (long)width * height > 40_000_000) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_F676EA11B675"));
                    covers.Add(new Cover(index, Text(stream, "codec_name"), width, height));
                }
            }
            if (audio is not { } a) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_55E680E7ED15"));
            var extension = Path.GetExtension(path).ToLowerInvariant(); var container = Text(format, "format_name").Split(','); var codec = Text(a, "codec_name");
            var resultFormat = extension switch
            {
                ".mp3" when container.Contains("mp3") && codec == "mp3" => "MP3",
                ".flac" when container.Contains("flac") && codec == "flac" => "FLAC",
                ".m4a" when container.Contains("mov") && codec is "aac" or "alac" => "M4A",
                _ => throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_A17B3D47D221"))
            };
            var channels = Integer(a, "channels"); var rate = Integer(a, "sample_rate"); var duration = Math.Max(Numeric(format, "duration"), Numeric(a, "duration"));
            if (channels is < 1 or > 8 || rate is < 1 or > 192000 || !double.IsFinite(duration) || duration <= 0 || duration > MaximumDurationSeconds + .1)
                throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_58E0E598BAF8"));
            var formatTags = Tags(format); var audioTags = Tags(a);
            string Resolve(string key)
            {
                formatTags.TryGetValue(key, out var left); audioTags.TryGetValue(key, out var right);
                if (left is not null && right is not null && left != right) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_D16758CA57CC") + key + global::Orbit.Core.Localization.L.Get("Text_771AA7D3E10E"));
                return left ?? right ?? "";
            }
            return new AudioInfo(resultFormat, codec, Integer(a, "index"), rate, channels, Resolve("title"), Resolve("artist"), Resolve("album"), covers.AsReadOnly());
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_33AC37CCDEC7"), exception); }
    }
    private static Dictionary<string, string> Tags(JsonElement owner)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!owner.TryGetProperty("tags", out var tags)) return result;
        foreach (var item in tags.EnumerateObject())
        {
            if (!new[] { "title", "artist", "album" }.Contains(item.Name, StringComparer.OrdinalIgnoreCase)) continue;
            var value = item.Value.GetString() ?? "";
            if (!result.TryAdd(item.Name, value)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_159E0F4687C3"));
            if (value.Length > 8192 || value.Any(char.IsControl) || !WellFormed(value)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_383045D4CB2F"));
        }
        return result;
    }
    private async Task<AudioProof> ProveAudioAsync(string path, AudioInfo info, CancellationToken token)
    {
        var packetHash = await PacketHashAsync(path, info.AudioIndex, token).ConfigureAwait(false); string pcmHash = ""; long samples = 0;
        var args = Input(path); args.AddRange(["-map", "0:" + info.AudioIndex.ToString(CultureInfo.InvariantCulture), "-vn", "-sn", "-dn", "-af", "astats=metadata=1:reset=0:measure_perchannel=none:measure_overall=Number_of_samples,ametadata=print:key=lavfi.astats.Overall.Number_of_samples:file=-:direct=1", "-t", "86401", "-c:a", "pcm_s32le", "-f", "hash", "-hash", "sha256", "-"]);
        await RunAsync(ExecutablePath, args, token, line =>
        {
            const string prefix = "lavfi.astats.Overall.Number_of_samples=";
            if (line.StartsWith(prefix, StringComparison.Ordinal) && double.TryParse(line.AsSpan(prefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var count) && double.IsFinite(count) && count >= 0 && count <= long.MaxValue && count == Math.Truncate(count)) samples = Math.Max(samples, (long)count);
            if (line.StartsWith("SHA256=", StringComparison.Ordinal)) pcmHash = line[7..].Trim();
        }).ConfigureAwait(false);
        if (samples <= 0 || samples / (double)info.Rate > MaximumDurationSeconds || !HashValid(pcmHash)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_1C70C47D6520"));
        return new AudioProof(packetHash, pcmHash.ToUpperInvariant(), samples);
    }
    private async Task<string> PacketHashAsync(string path, int index, CancellationToken token)
    {
        var args = Input(path); args.AddRange(["-map", "0:" + index.ToString(CultureInfo.InvariantCulture), "-c", "copy", "-f", "hash", "-hash", "sha256", "-"]);
        var output = await RunAsync(ExecutablePath, args, token).ConfigureAwait(false); var hash = output.Split('\n').SingleOrDefault(line => line.StartsWith("SHA256=", StringComparison.Ordinal))?[7..].Trim();
        if (!HashValid(hash)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_FCCC09A9C3CB")); return hash!.ToUpperInvariant();
    }
    private async Task<string> ProveCoverAsync(string path, Cover cover, CancellationToken token)
    {
        var args = Input(path); args.AddRange(["-map", "0:" + cover.Index.ToString(CultureInfo.InvariantCulture), "-an", "-sn", "-dn", "-frames:v", "1", "-f", "null", "-"]);
        await RunAsync(ExecutablePath, args, token).ConfigureAwait(false); return await PacketHashAsync(path, cover.Index, token).ConfigureAwait(false);
    }
    private static bool HashValid(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static void ValidateValue(string? value) { if (value is null || value.Length > 512 || value.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029') || !WellFormed(value)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_8F0001239C31")); }
    private static bool WellFormed(string value) { for (var i = 0; i < value.Length; i++) { if (char.IsHighSurrogate(value[i])) { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false; } else if (char.IsLowSurrogate(value[i])) return false; } return true; }
    private static List<string> Input(string path)
    {
        var args = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-n", "-nostats", "-xerror", "-threads", "2", "-filter_threads", "2", "-max_alloc", "536870912", "-max_pixels", "40000000", "-format_whitelist", "mp3,flac,mov", "-protocol_whitelist", "file,pipe" };
        // Stream-copy inputs have no decoder: FFmpeg6 rejects skip_frame there.
        // Exact maps plus max_pixels/max_alloc still bound any demuxer cover preflight.
        args.AddRange(["-i", path]); return args;
    }
    private sealed record AudioInfo(string Format, string Codec, int AudioIndex, int Rate, int Channels, string Title, string Artist, string Album, IReadOnlyList<Cover> Covers);
    private sealed record Cover(int Index, string Codec, int Width, int Height);
    private sealed record AudioProof(string PacketHash, string PcmHash, long Samples);

    private static async Task CheckRawTagsAsync(FileStream file, string extension, CancellationToken token)
    {
        // Demuxers may merge repeated ID3/Vorbis/ilst fields. Reject their raw duplicates
        // before ffprobe's dictionary can conceal ambiguous edits; never retain cover data.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var pictureCount = 0;
        void Picture(long bytes) { if (++pictureCount > 8 || bytes > MaximumCoverBytes) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_B2BA139DD5A6")); }
        void Tag(string key) { if (key is "title" or "artist" or "album" && !seen.Add(key)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_FF7A7B78BE54")); }
        async Task<byte[]> Read(long position, int count)
        {
            token.ThrowIfCancellationRequested(); if (count < 0 || position < 0 || position > file.Length - count) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_942E7361A10E"));
            file.Position = position; var bytes = new byte[count]; await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false); return bytes;
        }
        if (extension.Equals(".flac", StringComparison.OrdinalIgnoreCase))
        {
            long position = 4, metadata = 0; var blocks = 0; bool last;
            do
            {
                if (++blocks > 10000) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_1AFA5DCCA7C0"));
                var header = await Read(position, 4).ConfigureAwait(false); last = (header[0] & 128) != 0; var type = header[0] & 127; var size = header[1] << 16 | header[2] << 8 | header[3]; position += 4;
                if ((metadata += size) > 256L * 1024 * 1024 || position > file.Length - size) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_49A3A1E0AE68"));
                if (type == 6) Picture(size);
                if (type == 4)
                {
                    if (size > MaximumProbeBytes) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_0A27D4192AB7"));
                    var body = await Read(position, size).ConfigureAwait(false); var offset = 0;
                    uint Number() { if (offset > body.Length - 4) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_68396C488E60")); var value = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset, 4)); offset += 4; return value; }
                    var vendor = Number(); if (vendor > body.Length - offset) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_4999A2C2E4EB")); offset += (int)vendor; var count = Number();
                    if (count > 100000) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_C458BAD89E1A"));
                    for (var index = 0; index < count; index++)
                    {
                        token.ThrowIfCancellationRequested(); var length = Number(); if (length > body.Length - offset) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_E1E0BCC31AD4"));
                        var comment = Utf8.GetString(body, offset, (int)length); var equal = comment.IndexOf('='); if (equal < 1) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_EBE6473C3757")); Tag(comment[..equal].ToLowerInvariant()); offset += (int)length;
                    }
                    if (offset != body.Length) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_FAA241E2C55C"));
                }
                position += size;
            } while (!last);
        }
        else if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var header = await Read(0, (int)Math.Min(10, file.Length)).ConfigureAwait(false);
            if (header.Length >= 10 && header.AsSpan(0, 3).SequenceEqual("ID3"u8))
            {
                var version = header[3]; if (version is < 2 or > 4 || version < 4 && (header[5] & 128) != 0 || version == 2 && (header[5] & 64) != 0 || version == 4 && (header[5] & 16) != 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_06F08A647C24"));
                var size = SyncSafe(header.AsSpan(6, 4)); if (size > 256 * 1024 * 1024 || size > file.Length - 10) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_EF6FB20FA20E"));
                long position = 10, end = 10L + size; var count = 0;
                if (version >= 3 && (header[5] & 64) != 0) { var extra = await Read(position, 4).ConfigureAwait(false); var length = version == 4 ? SyncSafe(extra) : BinaryPrimitives.ReadUInt32BigEndian(extra) + 4L; if (length < 4 || length > end - position) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_783B33178160")); position += length; }
                var width = version == 2 ? 6 : 10;
                while (position < end)
                {
                    token.ThrowIfCancellationRequested(); if (++count > 100000 || end - position < width) break;
                    var frame = await Read(position, width).ConfigureAwait(false); if (frame[0] == 0) break; var id = Encoding.ASCII.GetString(frame, 0, version == 2 ? 3 : 4);
                    if (id.Any(character => !char.IsAsciiLetterUpper(character) && !char.IsAsciiDigit(character))) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_67846BC2C882"));
                    long length = version == 2 ? frame[3] << 16 | frame[4] << 8 | frame[5] : version == 4 ? SyncSafe(frame.AsSpan(4, 4)) : BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(4, 4)); position += width;
                    if (length < 0 || length > end - position) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_E34B6889634F"));
                    if (id is "APIC" or "PIC") Picture(length);
                    Tag(id switch { "TIT2" or "TT2" => "title", "TPE1" or "TP1" => "artist", "TALB" or "TAL" => "album", _ => "" });
                    if (id is "TXXX" or "TXX")
                    {
                        if (length > 65536) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_F28FDDE5EACB"));
                        var data = await Read(position, (int)length).ConfigureAwait(false); if (data.Length > 1)
                        {
                            var encoding = data[0] switch { 0 => Encoding.Latin1, 1 => Encoding.Unicode, 2 => Encoding.BigEndianUnicode, 3 => Utf8, _ => throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_BEF235066330")) };
                            var description = encoding.GetString(data, 1, data.Length - 1).TrimStart('\ufeff'); var zero = description.IndexOf('\0'); if (zero >= 0) Tag(description[..zero].ToLowerInvariant());
                        }
                    }
                    position += length;
                }
                if (count > 100000) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_DDE04669D9D6"));
            }
        }
        else
        {
            var atoms = 0;
            async Task Boxes(long start, long end, int depth, bool ilst = false)
            {
                if (depth > 8) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_8DECA44ABAE6"));
                for (var position = start; position < end;)
                {
                    token.ThrowIfCancellationRequested(); if (++atoms > 100000 || end - position < 8) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_122C529EAAE1"));
                    var box = await Read(position, 8).ConfigureAwait(false); long size = BinaryPrimitives.ReadUInt32BigEndian(box); var width = 8; var type = Encoding.Latin1.GetString(box, 4, 4);
                    if (size == 1) { var extended = await Read(position + 8, 8).ConfigureAwait(false); var large = BinaryPrimitives.ReadUInt64BigEndian(extended); if (large > long.MaxValue) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_4D7CD94F8DCD")); size = (long)large; width = 16; }
                    if (size == 0) size = end - position;
                    if (size < width || size > end - position) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_FDEEF62119A6"));
                    if (ilst)
                    {
                        var key = type switch { "©nam" => "title", "©ART" => "artist", "©alb" => "album", _ => "" }; Tag(key);
                        if (key.Length != 0 || type == "covr")
                        {
                            if (key.Length != 0 && size > MaximumProbeBytes) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_9D28722F43AF"));
                            long child = position + width; var values = 0;
                            while (child < position + size) { token.ThrowIfCancellationRequested(); if (++atoms > 100000 || position + size - child < 8) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_57FC8EC2A201")); var data = await Read(child, 8).ConfigureAwait(false); var dataSize = BinaryPrimitives.ReadUInt32BigEndian(data); if (dataSize < 8 || dataSize > position + size - child) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_A141DBAB0AAF")); if (data.AsSpan(4, 4).SequenceEqual("data"u8)) { if (type == "covr") Picture(dataSize); else if (++values > 1) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_DFD4702F370B")); } child += dataSize; }
                        }
                    }
                    else if (type is "moov" or "udta" or "meta" or "ilst") await Boxes(position + width + (type == "meta" ? 4 : 0), position + size, depth + 1, type == "ilst").ConfigureAwait(false);
                    position += size;
                }
            }
            await Boxes(0, file.Length, 0).ConfigureAwait(false);
        }
        file.Position = 0;
    }
    private static int SyncSafe(ReadOnlySpan<byte> data) { var result = 0; foreach (var value in data) { if (value > 127) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_5E1C4FCF25EC")); result = checked(result * 128 + value); } return result; }

    private async Task<string> RunAsync(string executable, IReadOnlyList<string> args, CancellationToken token, Action<string>? callback = null)
    {
        token.ThrowIfCancellationRequested(); var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg); using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_79569A156F6C")); }
        catch (System.ComponentModel.Win32Exception exception) { throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_64D94FFD1025"), exception); }
        var stdout = callback is null ? BytesAsync(process.StandardOutput.BaseStream, MaximumProbeBytes, token) : LinesAsync(process.StandardOutput, callback, token);
        var stderr = BytesAsync(process.StandardError.BaseStream, 128 * 1024, token);
        try
        {
            NativeStarted?.Invoke(string.Join(' ', args)); await process.WaitForExitAsync(token).ConfigureAwait(false); await Task.WhenAll(stdout, stderr).WaitAsync(token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false); var error = await stderr.ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (output.Length > MaximumProbeBytes || error.Length > 128 * 1024) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_5D447632FB25"));
            var details = Encoding.UTF8.GetString(error);
            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(details)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_95003A78BE7A") + details[..Math.Min(details.Length, 700)].Trim());
            return new UTF8Encoding(false, true).GetString(output);
        }
        finally
        {
            if (!process.HasExited) { try { process.Kill(true); } catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception exception) when (exception is TimeoutException or InvalidOperationException) { }
            var drains = Task.WhenAll(stdout, stderr); try { await drains.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException) { process.StandardOutput.Dispose(); process.StandardError.Dispose(); Observe(drains); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
    }
    private static async Task<byte[]> BytesAsync(Stream input, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0) if (output.Length <= maximum) output.Write(buffer, 0, (int)Math.Min(count, maximum + 1 - output.Length)); return output.ToArray();
    }
    private static async Task<byte[]> LinesAsync(StreamReader input, Action<string> callback, CancellationToken token)
    {
        var line = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
            for (var i = 0; i < count; i++) if (buffer[i] == '\n') { callback(line.ToString().TrimEnd('\r')); line.Clear(); } else if (line.Length < 4096) line.Append(buffer[i]);
        if (line.Length != 0) callback(line.ToString()); return [];
    }
    private static async Task CleanupAsync(string? temporary, string? parent)
    {
        if (temporary is null || parent is null || !string.Equals(Path.GetDirectoryName(temporary), Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(temporary).StartsWith(".orbit-audio-tags-", StringComparison.Ordinal)) return;
        var deleting = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 11; attempt++)
            {
                try { if (Directory.Exists(temporary) && (File.GetAttributes(temporary) & FileAttributes.ReparsePoint) == 0) Directory.Delete(temporary, true); return; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                if (attempt < 10) await Task.Delay(Math.Min(300, 25 << Math.Min(attempt, 4))).ConfigureAwait(false);
            }
        }, CancellationToken.None);
        try { await deleting.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (TimeoutException) { Observe(deleting); }
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); var full = Path.GetFullPath(path).Replace('/', Path.DirectorySeparatorChar);
        if (!new[] { ".mp3", ".flac", ".m4a" }.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase)) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_34492564726B"));
        if (full.Length > 4096 || full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)
            || full.IndexOf(':', full.Length > 1 && full[1] == ':' ? 2 : 0) >= 0)
            throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_0C9CDEF5F3FA"));
        return full;
    }

    private static bool Signature(byte[] header, int count, string extension)
    {
        bool Starts(string value) => header.AsSpan(0, count).StartsWith(Encoding.ASCII.GetBytes(value));
        return extension.ToLowerInvariant() switch
        {
            ".flac" => Starts("fLaC"),
            ".mp3" => Starts("ID3") || count >= 2 && header[0] == 255 && (header[1] & 0xe0) == 0xe0,
            ".m4a" => count >= 8 && new[] { "ftyp", "moov", "mdat", "wide", "free", "skip" }.Any(atom => header.AsSpan(4, 4).SequenceEqual(Encoding.ASCII.GetBytes(atom))),
            _ => false
        };
    }

    private static async Task<FileStream> OpenInputAsync(string path, CancellationToken token)
    {
        var opening = Task.Run(() =>
        {
            Recheck(path, token); var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            try
            {
                Recheck(path, token);
                if (input.Length is <= 0 or > MaximumSourceBytes || (File.GetAttributes(input.SafeFileHandle) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_1992F76E811F"));
                return input;
            }
            catch { input.Dispose(); throw; }
        }, CancellationToken.None);
        FileStream stream;
        try { stream = await opening.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
        catch { _ = opening.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); else _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); throw; }
        try
        {
            var header = new byte[32]; var count = await stream.ReadAsync(header, token).ConfigureAwait(false);
            if (!Signature(header, count, Path.GetExtension(path))) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_3FED491ED045"));
            stream.Position = 0; return stream;
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static void Recheck(string path, CancellationToken token)
    {
        var depth = 0;
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            token.ThrowIfCancellationRequested(); if (++depth > 256) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_77BCA2D5D580")); var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || depth == 1 && (attributes & FileAttributes.Directory) != 0) throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_4E48521B798B"));
        }
    }


    private static string Commit(string prepared, string directory, string source, string extension, CancellationToken token)
    {
        var stem = Path.GetFileNameWithoutExtension(source); if (stem.Length > 180) { stem = stem[..180]; if (char.IsHighSurrogate(stem[^1])) stem = stem[..^1]; } stem += "_tags";
        for (var index = 0; index < 10000; index++)
        {
            token.ThrowIfCancellationRequested(); var target = Path.Combine(directory, stem + (index == 0 ? "" : "_" + index.ToString(CultureInfo.InvariantCulture)) + "." + extension);
            if (target.Equals(source, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Move(prepared, target, false); return target; } catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { }
        }
        throw new MediaProcessingException(global::Orbit.Core.Localization.L.Get("Text_AF4A7BB0F593"));
    }
    private CancellationTokenSource Deadline(CancellationToken token) { var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(_timeout); return deadline; }
    private static bool Expected(Exception exception) => exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or TimeoutException or System.Security.SecurityException or InvalidDataException or OverflowException;
    private static MediaProcessingException Explain(Exception exception) => new(global::Orbit.Core.Localization.L.Get("Text_D84F68306D8B"), exception);
    private static MediaProcessingException TimeoutError() => new(global::Orbit.Core.Localization.L.Get("Text_1E71D1E58245"));
    private static void Observe(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private static string Text(JsonElement item, string key) => item.TryGetProperty(key, out var value) ? value.ToString() : "";
    private static int Integer(JsonElement item, string key, int fallback = 0) => int.TryParse(Text(item, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    private static double Numeric(JsonElement item, string key) => double.TryParse(Text(item, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;
}
