using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Orbit.Core;
using Orbit.Core.Downloads;

namespace Orbit.App.History;

internal static partial class HistoryValidation
{
    private const int MaximumPathCharacters = 4096;
    private const int MaximumRecordCharacters = 262144;
    public const int MaximumSourcePaths = 1000;
    public const int MaximumZipSelectedEntries = 1000;
    public const int MaximumArchiveSelectedEntries = 1000;
    private const int MaximumZipSelectionCharacters = 131072;
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "CLOCK$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
    private static readonly Dictionary<string, string[]> FileFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JPEG"] = new[] { ".jpg", ".jpeg" }, ["JPG"] = new[] { ".jpg", ".jpeg" },
        ["PNG"] = new[] { ".png" }, ["WEBP"] = new[] { ".webp" }, ["AVIF"] = new[] { ".avif" },
        ["BMP"] = new[] { ".bmp" }, ["BMP2"] = new[] { ".bmp" }, ["BMP3"] = new[] { ".bmp" },
        ["TIFF"] = new[] { ".tif", ".tiff" }, ["MP4"] = new[] { ".mp4" }, ["WEBM"] = new[] { ".webm" },
        ["MP3"] = new[] { ".mp3" }, ["WAV"] = new[] { ".wav" }, ["FLAC"] = new[] { ".flac" },
        ["M4A"] = new[] { ".m4a" }, ["OPUS"] = new[] { ".opus" }, ["OGG"] = new[] { ".ogg" },
        ["PDF"] = new[] { ".pdf" }, ["ZIP"] = new[] { ".zip" }, ["7Z"] = new[] { ".7z" }, ["TXT"] = new[] { ".txt" }, ["GIF"] = new[] { ".gif" }
    };

    public static bool TryNormalize(HistoryEntry? entry, bool startup, out HistoryEntry normalized)
    {
        normalized = null!;
        if(entry?.Download is not null)return TryDownload(entry,startup,out normalized);
        if (entry is null || entry.Id == Guid.Empty || !Enum.IsDefined(entry.Status)
            || entry.CreatedUtc.Year is < 2000 or > 9998 || entry.SourcePaths is null
            || entry.SourcePaths.Count is < 1 or > MaximumSourcePaths || entry.Options is null)
            return false;
        if (entry.FinishedUtc is { } finish && (finish.Year is < 2000 or > 9998 || finish < entry.CreatedUtc)) return false;
        var paths = new List<string>(entry.SourcePaths.Count);
        var characters = 0;
        foreach (var source in entry.SourcePaths)
        {
            if (!TryPath(source, out var path)) return false;
            characters += path.Length;
            if (characters > MaximumRecordCharacters) return false;
            paths.Add(path);
        }
        if (!TryOptions(entry.Options, out var options)) return false;
        if(options.Action==ImageAction.AudioTags&&paths.Any(path=>AudioTagFormat(path) is null))return false;
        if(options.Action==ImageAction.ImageWatermark&&paths.Any(path=>WatermarkFormat(path) is null))return false;
        characters += options.ZipSelectedEntries.Sum(name => name.Length);
        if (characters > MaximumRecordCharacters) return false;
        var many = options.Action is ImageAction.ArchiveZip or ImageAction.ArchiveTar or ImageAction.Archive7z or ImageAction.FileHash or ImageAction.PdfMerge or ImageAction.ImagesToPdf or ImageAction.ContactSheet;
        if ((!many && paths.Count != 1) || (options.Action == ImageAction.PdfMerge && paths.Count < 2)) return false;
        if(options.Action is ImageAction.ImagesToPdf or ImageAction.ContactSheet && paths.Count>100)return false;
        ImageJobResult? result = null;
        if (entry.Result is not null)
        {
            if (!TryResult(entry.Result, options, paths, out result)) return false;
        }
        if (entry.Status == HistoryStatus.Succeeded && result is null) return false;
        var status = startup && entry.Status is HistoryStatus.Queued or HistoryStatus.Running ? HistoryStatus.Interrupted : entry.Status;
        DateTimeOffset? finished = entry.FinishedUtc?.ToUniversalTime();
        if (status is HistoryStatus.Queued or HistoryStatus.Running) finished = null;
        else if (finished is null) finished = entry.CreatedUtc > DateTimeOffset.UtcNow ? entry.CreatedUtc.ToUniversalTime() : DateTimeOffset.UtcNow;
        var caption = entry.ErrorCaption;
        if (caption is not null)
        {
            if(options.Action==ImageAction.SevenZipExtractSelected&&options.SevenZipPasswordProtected)
                caption=global::Orbit.Core.Localization.L.Get("Text_998F79F7D7FD");
            if(options.Action==ImageAction.ImageWatermark&&options.WatermarkMode=="Logo")
                caption=global::Orbit.Core.Localization.L.Get("Text_D7BCCDA69997");
            caption = new string(caption.Where(character => !char.IsControl(character) || character is '\n' or '\r' or '\t').Take(2048).ToArray()).Trim();
            if (caption.Length == 0) caption = null;
        }
        normalized = entry with { CreatedUtc = entry.CreatedUtc.ToUniversalTime(), FinishedUtc = finished, SourcePaths = paths.AsReadOnly(),
            Options = options, Result = result, ErrorCaption = caption, Status = status };
        return true;
    }

    private static bool TryOptions(ImageJobOptions value, out ImageJobOptions normalized)
    {
        normalized = value;
        var archiveSelection=value.Action is ImageAction.SevenZipExtractSelected or ImageAction.TarExtractSelected;
        if (!Enum.IsDefined(value.Action) || value.Action is ImageAction.Rename or ImageAction.MoreTools or ImageAction.FileInfo or ImageAction.DownloadAudio or ImageAction.DownloadVideo
            || value.Quality is < 1 or > 100 || value.Width is <= 0 or > 100000 || value.Height is <= 0 or > 100000
            || value.CropX is < 0 or > 100000 || value.CropY is < 0 or > 100000
            || value.CropWidth is <= 0 or > 100000 || value.CropHeight is <= 0 or > 100000
            || value.ResizePercent is{} percent&&(!double.IsFinite(percent)||percent is <1 or >400)
            || value.ResizeFilter is not("Auto" or "Lanczos" or "Mitchell" or "Point")
            || value.JpegBackgroundColor is null||value.JpegBackgroundColor.Length!=7||value.JpegBackgroundColor[0]!='#'
            || value.JpegBackgroundColor.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF")
            || value.VideoCrf is < 0 or > 51 || value.AudioBitrateKbps is < 32 or > 320
            || value.PdfDpi is < 36 or > 600 || value.OcrDpi is < 120 or > 300
            || value.RotationDegrees is not (0 or 90 or 180 or 270)
            || value.ImagePdfPageSize is not ("A4" or "A4Landscape" or "FitImage")
            || !double.IsFinite(value.ImagePdfMarginMillimeters) || value.ImagePdfMarginMillimeters is <0 or >40
            || value.ContactSheetColumns is <1 or >6 || value.ContactSheetThumbnailSize is not (160 or 320 or 640) || value.ContactSheetGap is <0 or >32
            || !double.IsFinite(value.GifStartSeconds) || value.GifStartSeconds is <0 or >31536000
            || !double.IsFinite(value.GifDurationSeconds) || value.GifDurationSeconds is <=0 or >30
            || value.GifWidth is <160 or >1280 || value.GifFramesPerSecond is <1 or >30
            || value.VideoExportWidth is <2 or >3840 || value.VideoExportFramesPerSecond is <1 or >60
            || !ValidAudioExport(value.AudioExportFormat,value.AudioExportBitrateKbps)
            || value.AudioExportVorbisQuality is <0 or >10
            || !ValidAudioTag(value.AudioTagTitle)||!ValidAudioTag(value.AudioTagArtist)||!ValidAudioTag(value.AudioTagAlbum)
            || value.AudioTrackStreamIndex is <0 or >65535
            || value.AudioTrackSourceSha256 is{} trackHash&&(trackHash.Length!=64||trackHash.Any(character=>!char.IsAsciiHexDigit(character)))
            || value.AudioTrackFormat is not("MP3" or "WAV" or "FLAC")
            || value.AudioTrackBitrateKbps is not(64 or 96 or 128 or 160 or 192 or 256 or 320)
            || value.TarArchiveFormat is not("TAR" or "TAR.GZ")
            || value.TarCompression is not("Fastest" or "Optimal" or "SmallestSize")
            || value.SevenZipCompression is not("Fastest" or "Optimal" or "SmallestSize")
            || value.SevenZipEncryptHeaders&&!value.SevenZipPasswordProtected
            || value.ArchiveSelectedCount is <0 or >MaximumArchiveSelectedEntries
            || value.WatermarkMode is not("Text" or "Logo")||!ValidWatermarkText(value.WatermarkText)
            || value.WatermarkFont is not("SegoeUI" or "Arial")||value.WatermarkFontSizePercent is <1 or >15
            || value.WatermarkOpacityPercent is <1 or >100||value.WatermarkMarginPercent is <0 or >20||value.WatermarkLogoWidthPercent is <1 or >100
            || value.WatermarkPosition is not("TopLeft" or "Top" or "TopRight" or "Left" or "Center" or "Right" or "BottomLeft" or "Bottom" or "BottomRight")
            || value.WatermarkColor is null||value.WatermarkColor.Length!=7||value.WatermarkColor[0]!='#'
            || value.WatermarkColor.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF")
            || !double.IsFinite(value.FramesStartSeconds)||value.FramesStartSeconds is <0 or >86400
            || !double.IsFinite(value.FramesDurationSeconds)||value.FramesDurationSeconds is <=0 or >30
            || value.FramesPerSecond is <1 or >10||Math.Ceiling(value.FramesDurationSeconds*value.FramesPerSecond)>300
            || value.FramesWidth is <160 or >1920
            || !double.IsFinite(value.AudioVolumePercent) || value.AudioVolumePercent is <0 or >200
            || !double.IsFinite(value.AudioFadeInSeconds) || value.AudioFadeInSeconds is <0 or >86400
            || !double.IsFinite(value.AudioFadeOutSeconds) || value.AudioFadeOutSeconds is <0 or >86400
            || value.AudioFadeInSeconds+value.AudioFadeOutSeconds>86400
            || !double.IsFinite(value.NormalizeTargetLufs)||value.NormalizeTargetLufs is <-24 or >-9
            || !double.IsFinite(value.NormalizeTruePeakDb)||value.NormalizeTruePeakDb is <-3 or >-0.1
            || !double.IsFinite(value.NormalizeLoudnessRange)||value.NormalizeLoudnessRange is <1 or >20
            || value.PdfRotationDegrees is not (90 or 180 or 270)
            || value.PdfSplitPagesPerFile is <1 or >1000
            || !double.IsFinite(value.TrimStartSeconds) || value.TrimStartSeconds < 0 || value.TrimStartSeconds > 31536000
            || value.TrimEndSeconds is { } end && (!double.IsFinite(end) || end <= value.TrimStartSeconds || end > 31536000)) return false;
        if (value.Action == ImageAction.Resize && (value.ResizePercent is not null
            ?value.Width is not null||value.Height is not null||!value.MaintainAspectRatio
            :value.Width is null&&value.Height is null||!value.MaintainAspectRatio&&(value.Width is null||value.Height is null))) return false;
        if (value.Action == ImageAction.Crop && (value.CropWidth is null || value.CropHeight is null)) return false;
        if (value.Action is ImageAction.VideoTrim or ImageAction.AudioTrim && value.TrimEndSeconds is null) return false;
        if(value.Action==ImageAction.AudioTrackExtract&&(value.AudioTrackStreamIndex is null||value.AudioTrackSourceSha256 is null))return false;
        if(value.Action==ImageAction.AudioTags&&!value.AudioTagSetTitle&&!value.AudioTagSetArtist&&!value.AudioTagSetAlbum)return false;
        if(value.Action==ImageAction.ImageWatermark&&value.WatermarkMode=="Text"&&string.IsNullOrWhiteSpace(value.WatermarkText))return false;
        if(archiveSelection&&value.ArchiveSelectedCount<1)return false;
        if(value.ChainSteps is null||value.ChainSteps.Count>4)return false;
        IReadOnlyList<ImageJobOptions> chain=Array.Empty<ImageJobOptions>();
        if(value.Action==ImageAction.ImageChain){
            if(value.ChainSteps.Count<2)return false;
            var steps=new List<ImageJobOptions>();
            foreach(var step in value.ChainSteps.Take(5)){
                if(steps.Count>=4||step is null||step.Action is not(ImageAction.ImageTransform or ImageAction.Resize or ImageAction.RemoveImageMetadata
                    or ImageAction.Jpg or ImageAction.WebP or ImageAction.Avif or ImageAction.Compress)
                    ||step.ChainSteps is null||step.ChainSteps.Count!=0||step.OutputDirectory is not null||!TryOptions(step,out var safeStep))return false;
                steps.Add(safeStep);
            }
            if(steps.Count!=value.ChainSteps.Count)return false;
            chain=steps.AsReadOnly();
        }else if(value.ChainSteps.Count!=0)return false;
        if (!TryZipSelection(archiveSelection?Array.Empty<string>():value.ZipSelectedEntries, out var zipEntries)) return false;
        var zipHash = archiveSelection?null:value.ZipSourceSha256;
        if (zipHash is not null && (zipHash.Length != 64 || zipHash.Any(character => !char.IsAsciiHexDigit(character)))) return false;
        if (value.Action == ImageAction.ZipExtractSelected && (zipEntries.Count == 0 || zipHash is null)) return false;
        string? directory = null;
        if (!string.IsNullOrWhiteSpace(value.OutputDirectory) && !TryPath(value.OutputDirectory, out directory)) return false;
        var compression = string.IsNullOrWhiteSpace(value.CompressionFormat) ? null : value.CompressionFormat.Trim().TrimStart('.').ToLowerInvariant();
        if (compression is not (null or "jpg" or "jpeg" or "png" or "webp" or "avif")) return false;
        var language = value.OcrLanguages?.Trim().ToLowerInvariant();
        if (language is not ("eng" or "rus" or "rus+eng" or "eng+rus")) return false;
        var range = value.PdfPageSelection?.Trim();
        if (range is null || range.Length is < 1 or > 8192 || !Regex.IsMatch(range, @"^\s*[1-9][0-9]*(?:\s*-\s*[1-9][0-9]*)?(?:\s*,\s*[1-9][0-9]*(?:\s*-\s*[1-9][0-9]*)?)*\s*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)) return false;
        long selected = 0;
        foreach (var token in range.Split(','))
        {
            var ends = token.Split('-');
            if (!int.TryParse(ends[0].Trim(), out var first) || first is < 1 or > 1000) return false;
            var last = first;
            if (ends.Length == 2 && (!int.TryParse(ends[1].Trim(), out last) || last is < 1 or > 1000)) return false;
            selected += Math.Abs(last - first) + 1;
            if (selected > 1000) return false;
        }
        var watermark=value.Action==ImageAction.ImageWatermark;
        var textWatermark=watermark&&value.WatermarkMode=="Text";
        normalized = value with { OutputDirectory = directory, CompressionFormat = compression, OcrLanguages = language, PdfPageSelection = range,
            Quality=watermark?85:value.Quality,
            WatermarkMode=watermark?value.WatermarkMode:"Text",WatermarkText=textWatermark?value.WatermarkText:"",
            WatermarkFont=textWatermark?value.WatermarkFont:"SegoeUI",WatermarkColor=textWatermark?value.WatermarkColor.ToUpperInvariant():"#FFFFFF",
            WatermarkFontSizePercent=textWatermark?value.WatermarkFontSizePercent:5,
            WatermarkOpacityPercent=watermark?value.WatermarkOpacityPercent:50,WatermarkPosition=watermark?value.WatermarkPosition:"BottomRight",
            WatermarkMarginPercent=watermark?value.WatermarkMarginPercent:3,WatermarkLogoPath=null,WatermarkLogoSha256=null,
            WatermarkLogoWidthPercent=watermark&&value.WatermarkMode=="Logo"?value.WatermarkLogoWidthPercent:20,
            SevenZipCompression=value.Action==ImageAction.Archive7z?value.SevenZipCompression:"Optimal",
            SevenZipPasswordProtected=value.Action is ImageAction.Archive7z or ImageAction.Extract7z or ImageAction.SevenZipList or ImageAction.SevenZipExtractSelected&&value.SevenZipPasswordProtected,
            SevenZipEncryptHeaders=value.Action==ImageAction.Archive7z&&value.SevenZipEncryptHeaders,
            ArchiveSelectedEntries=Array.Empty<string>(),ArchiveSourceSha256=null,ArchiveSelectedCount=archiveSelection?value.ArchiveSelectedCount:0,
            ZipSelectedEntries = zipEntries, ZipSourceSha256 = zipHash?.ToLowerInvariant(),ChainSteps=chain,JpegBackgroundColor=value.JpegBackgroundColor.ToUpperInvariant(),
            AudioTrackSourceSha256=value.AudioTrackSourceSha256?.ToLowerInvariant(),
            AudioExportVorbisQuality=value.Action==ImageAction.AudioExport&&value.AudioExportFormat=="VORBIS"?value.AudioExportVorbisQuality:5,
            AudioExportBitrateKbps=value.Action==ImageAction.AudioExport&&value.AudioExportFormat=="VORBIS"?192:value.AudioExportBitrateKbps,
            AudioTagSetTitle=value.Action==ImageAction.AudioTags&&value.AudioTagSetTitle,AudioTagTitle=value.Action==ImageAction.AudioTags&&value.AudioTagSetTitle?value.AudioTagTitle:"",
            AudioTagSetArtist=value.Action==ImageAction.AudioTags&&value.AudioTagSetArtist,AudioTagArtist=value.Action==ImageAction.AudioTags&&value.AudioTagSetArtist?value.AudioTagArtist:"",
            AudioTagSetAlbum=value.Action==ImageAction.AudioTags&&value.AudioTagSetAlbum,AudioTagAlbum=value.Action==ImageAction.AudioTags&&value.AudioTagSetAlbum?value.AudioTagAlbum:"" };
        return true;
    }

    private static bool TryZipSelection(IReadOnlyList<string>? names, out IReadOnlyList<string> normalized)
    {
        normalized = Array.Empty<string>();
        if (names is null || names.Count is <0 or >MaximumZipSelectedEntries) return false;
        if (names.Count == 0) return true;
        var result = new List<string>(names.Count);
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        var characters = 0;
        foreach (var name in names)
        {
            if (result.Count >= MaximumZipSelectedEntries || string.IsNullOrEmpty(name) || name.Length > MaximumPathCharacters
                || name[0] == '/' || name.Contains('\\') || name.Any(character => char.IsControl(character) || character is ':' or '"' or '<' or '>' or '|' or '*' or '?')) return false;
            characters += name.Length;
            if (characters > MaximumZipSelectionCharacters) return false;
            var directory = name.EndsWith('/');
            var relative = directory ? name[..^1] : name;
            if (relative.Length == 0) return false;
            foreach (var component in relative.Split('/'))
            {
                if (component.Length is <1 or >255 || component is "." or ".." || component.EndsWith(' ') || component.EndsWith('.')) return false;
                var stem = component.Split('.')[0].TrimEnd(' ');
                if (ReservedNames.Contains(stem) || stem.Length == 4
                    && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && stem[3] is '\u00b9' or '\u00b2' or '\u00b3') return false;
            }
            string key;
            try { key = relative.Normalize(NormalizationForm.FormC); }
            catch (ArgumentException) { return false; }
            if (!selected.Add(key)) return false;
            if (!directory) files.Add(key);
            // Keep exact ZIP spelling for lookup. NFC/case folding is only a destination-collision key.
            result.Add(name);
        }
        // Prefix lookup avoids allocating/storing every ancestor of a potentially4096-character name.
        var ordered = selected.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var file in files)
        {
            var prefix = file + "/";
            var index = Array.BinarySearch(ordered, prefix, StringComparer.OrdinalIgnoreCase);
            if (index < 0) index = ~index;
            if (index < ordered.Length && ordered[index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        }
        normalized = result.AsReadOnly();return true;
    }

    private static bool ValidAudioExport(string? format,int bitrate)=>format switch{
        "M4A"=>bitrate is 64 or 96 or 128 or 160 or 192 or 256 or 320,
        "OPUS"=>bitrate is 32 or 64 or 96 or 128 or 160 or 192 or 256,
        "VORBIS"=>bitrate is 64 or 96 or 128 or 160 or 192 or 256 or 320,_=>false};
    private static string? AudioTagFormat(string path)=>Path.GetExtension(path).ToLowerInvariant() switch{".mp3"=>"MP3",".flac"=>"FLAC",".m4a"=>"M4A",_=>null};
    private static bool ValidAudioTag(string? value)
    {
        if(value is null||value.Length>512)return false;
        for(int index=0;index<value.Length;index++){
            char character=value[index];if(char.IsControl(character)||character is '\u2028' or '\u2029')return false;
            if(char.IsHighSurrogate(character)){if(++index>=value.Length||!char.IsLowSurrogate(value[index]))return false;}
            else if(char.IsLowSurrogate(character))return false;
        }
        return true;
    }
    private static bool ValidWatermarkText(string? value)=>value is not null&&value.Length<=200&&ValidAudioTag(value);
    private static string? WatermarkFormat(string path)=>Path.GetExtension(path).ToLowerInvariant() switch{
        ".jpg" or ".jpeg"=>"JPEG",".png"=>"PNG",".webp"=>"WEBP",".avif"=>"AVIF",".bmp"=>"BMP",".tif" or ".tiff"=>"TIFF",_=>null};

    private static bool TryResult(ImageJobResult value, ImageJobOptions options, IReadOnlyList<string> sources, out ImageJobResult normalized)
    {
        normalized = null!;var action=options.Action;
        if (!TryPath(value.OutputPath, out var path) || sources.Contains(path, StringComparer.OrdinalIgnoreCase)
            || value.Bytes < 0 || value.SourceBytes < 0 || value.Width is < 0 or > 1000000 || value.Height is < 0 or > 1000000
            || string.IsNullOrWhiteSpace(value.Format)) return false;
        var format = value.Format.ToUpperInvariant();
        if(action==ImageAction.ImageWatermark){
            var expected=WatermarkFormat(sources[0]);
            if(expected is null||format!=expected&&!(expected=="BMP"&&format is "BMP2" or "BMP3")
                ||!string.Equals(Path.GetExtension(path),Path.GetExtension(sources[0]),StringComparison.OrdinalIgnoreCase)
                ||value.Bytes is <=0 or >256L*1024*1024||value.SourceBytes is <=0 or >256L*1024*1024
                ||value.Width is <1 or >16000||value.Height is <1 or >16000||(long)value.Width*value.Height>40_000_000)return false;
            var stem=Path.GetFileNameWithoutExtension(sources[0]);
            if(stem.Length>180){stem=stem[..180];if(char.IsHighSurrogate(stem[^1]))stem=stem[..^1];}
            if(!Regex.IsMatch(Path.GetFileNameWithoutExtension(path),"^"+Regex.Escape(stem+"_watermarked")+@"(?: \([1-9][0-9]{0,4}\))?$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant|RegexOptions.NonBacktracking))return false;
        }
        else if(action==ImageAction.ArchiveTar){
            if(format!=options.TarArchiveFormat||format=="TAR"&&!path.EndsWith(".tar",StringComparison.OrdinalIgnoreCase)
                ||format=="TAR.GZ"&&!path.EndsWith(".tar.gz",StringComparison.OrdinalIgnoreCase))return false;
        }
        else if (action is ImageAction.PdfPng or ImageAction.PdfJpg or ImageAction.ExtractZip or ImageAction.ExtractTar or ImageAction.Extract7z or ImageAction.SevenZipExtractSelected or ImageAction.TarExtractSelected or ImageAction.PdfSplit or ImageAction.ZipExtractSelected or ImageAction.PdfEmbeddedImages or ImageAction.VideoFrames)
        {
            var expectedFormat = action == ImageAction.PdfPng ? "PNG" : action == ImageAction.PdfJpg ? "JPG" : "FOLDER";
            var suffix = action switch { ImageAction.PdfPng => "_PNG", ImageAction.PdfJpg => "_JPG", ImageAction.PdfSplit => "_split",
                ImageAction.ZipExtractSelected or ImageAction.SevenZipExtractSelected or ImageAction.TarExtractSelected => "_selected", ImageAction.PdfEmbeddedImages => "_images", ImageAction.VideoFrames => "_frames", _ => "_extracted" };
            var stem = Path.GetFileNameWithoutExtension(sources[0]);
            if(action is ImageAction.ExtractTar or ImageAction.TarExtractSelected){
                var fileName=Path.GetFileName(sources[0]);
                stem=fileName.EndsWith(".tar.gz",StringComparison.OrdinalIgnoreCase)?fileName[..^7]
                    :fileName.EndsWith(".tgz",StringComparison.OrdinalIgnoreCase)||fileName.EndsWith(".tar",StringComparison.OrdinalIgnoreCase)?fileName[..^4]:stem;
            }
            if (action is ImageAction.ZipExtractSelected or ImageAction.SevenZipExtractSelected or ImageAction.TarExtractSelected or ImageAction.PdfEmbeddedImages or ImageAction.VideoFrames or ImageAction.ExtractTar or ImageAction.Extract7z && stem.Length > 180)
            {
                stem = stem[..180];
                if (char.IsHighSurrogate(stem[^1])) stem = stem[..^1];
            }
            var name = stem + suffix;
            if (format != expectedFormat || !Regex.IsMatch(Path.GetFileName(path), "^" + Regex.Escape(name) + @"(?: \([1-9][0-9]{0,4}\))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)) return false;
        }
        else
        {
            if (!FileFormats.TryGetValue(format, out var extensions) || !extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return false;
            var expected = action switch
            {
                ImageAction.Jpg => "JPEG", ImageAction.WebP => "WEBP", ImageAction.Avif => "AVIF", ImageAction.ImageTransform => "PNG",
                ImageAction.VideoMp4 or ImageAction.VideoCompress or ImageAction.VideoTrim or ImageAction.VideoExport => "MP4",
                ImageAction.VideoGif => "GIF",
                ImageAction.VideoWebm => "WEBM", ImageAction.VideoFrame => "PNG",
                ImageAction.ExtractMp3 or ImageAction.AudioMp3 or ImageAction.AudioCompress => "MP3",
                ImageAction.AudioExport => options.AudioExportFormat=="VORBIS"?"OGG":options.AudioExportFormat,
                ImageAction.AudioTags=>AudioTagFormat(sources[0]),
                ImageAction.AudioTrackExtract => options.AudioTrackFormat,
                ImageAction.AudioWav or ImageAction.AudioTrim or ImageAction.AudioAdjust or ImageAction.AudioNormalize => "WAV", ImageAction.AudioFlac => "FLAC", ImageAction.ArchiveZip => "ZIP",
                ImageAction.Archive7z => "7Z",
                ImageAction.PdfMerge or ImageAction.PdfExtractPages or ImageAction.ImagesToPdf or ImageAction.PdfRotate or ImageAction.PdfOptimize => "PDF",
                ImageAction.ContactSheet or ImageAction.AudioCover => "PNG",
                ImageAction.PdfText or ImageAction.OcrText or ImageAction.ArchiveList or ImageAction.TarList or ImageAction.SevenZipList or ImageAction.FileHash => "TXT",
                _ => null
            };
            if (expected is not null && format != expected) return false;
            if (expected is null && format is not ("JPEG" or "PNG" or "WEBP" or "AVIF" or "BMP" or "BMP2" or "BMP3" or "TIFF")) return false;
        }
        normalized = value with { OutputPath = path, Format = format };
        return true;
    }

    private static bool TryPath(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumPathCharacters || value.Any(character => char.IsControl(character) || character is '"' or '<' or '>' or '|' or '*' or '?')) return false;
        var path = value.Replace('/', '\\');
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal)) return false;
        var drive = path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
        if ((drive ? path[2..] : path).Contains(':')) return false;
        // Validate the spelling before GetFullPath can collapse Win32 trailing dots/spaces.
        foreach (var component in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or ".." || component.Length == 2 && component[1] == ':') continue;
            if (component.Length > 255 || component.EndsWith(' ') || component.EndsWith('.') || ReservedNames.Contains(component.Split('.')[0])) return false;
        }
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            foreach (var component in normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                if (component.Length == 2 && component[1] == ':') continue;
                if (component.Length > 255 || component.EndsWith(' ') || component.EndsWith('.') || ReservedNames.Contains(component.Split('.')[0])) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or SecurityException) { return false; }
    }
}
