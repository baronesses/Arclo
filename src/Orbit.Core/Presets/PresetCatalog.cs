using System.Globalization;
using System.Text;

namespace Orbit.Core.Presets;

public static class PresetCatalog
{
    public const int MaximumCustomPresets = 32;
    public const int MaximumNameLength = 64;
    // Selected archive extraction is deliberately excluded: every invocation needs a fresh
    // catalog fingerprint and selection, including after a successful password-protected job.
    private static readonly HashSet<ImageAction> ReusableActions = [
        ImageAction.Jpg, ImageAction.Png, ImageAction.Bmp, ImageAction.Tiff, ImageAction.Gif, ImageAction.Ico, ImageAction.Svg,
        ImageAction.WebP, ImageAction.Avif, ImageAction.Resize, ImageAction.Compress, ImageAction.ImageTransform, ImageAction.ImageWatermark,
        ImageAction.VideoMp4, ImageAction.VideoWebm, ImageAction.ExtractMp3, ImageAction.VideoFrame, ImageAction.VideoCompress,
        ImageAction.AudioMp3, ImageAction.AudioWav, ImageAction.AudioFlac, ImageAction.AudioCompress, ImageAction.AudioExport, ImageAction.AudioCover, ImageAction.AudioTags,
        ImageAction.PdfPng, ImageAction.PdfJpg, ImageAction.PdfText, ImageAction.PdfEmbeddedImages, ImageAction.PdfOptimize, ImageAction.OcrText,
        ImageAction.ArchiveZip, ImageAction.ExtractZip, ImageAction.ArchiveList, ImageAction.FileHash,
        ImageAction.ArchiveTar, ImageAction.ExtractTar, ImageAction.TarList,
        ImageAction.Archive7z, ImageAction.Extract7z, ImageAction.SevenZipList,
        ImageAction.ImagesToPdf, ImageAction.ContactSheet, ImageAction.RemoveImageMetadata,
        ImageAction.VideoExport, ImageAction.AudioAdjust, ImageAction.AudioNormalize, ImageAction.ImageChain
    ];

    public static IReadOnlyList<ProcessingPreset> BuiltIns => Array.AsReadOnly(new[] {
        BuiltIn("builtin-webp-82", global::Orbit.Core.Localization.L.Get("Text_3E38DE2D5907"), new() { Action=ImageAction.WebP, Quality=82 }),
        BuiltIn("builtin-jpeg-85", global::Orbit.Core.Localization.L.Get("Text_D82DF28326C4"), new() { Action=ImageAction.Jpg, Quality=85 }),
        BuiltIn("builtin-resize-1600", global::Orbit.Core.Localization.L.Get("Text_F44232DE34A4"), new() { Action=ImageAction.Resize, Width=1600, MaintainAspectRatio=true, NoUpscale=true }),
        BuiltIn("builtin-audio-mp3-192", global::Orbit.Core.Localization.L.Get("Text_D1E47FAC0955"), new() { Action=ImageAction.AudioMp3, AudioBitrateKbps=192 }),
        BuiltIn("builtin-video-mp4-24", "MP4 · CRF 24", new() { Action=ImageAction.VideoMp4, VideoCrf=24 }),
        BuiltIn("builtin-ocr-rus-eng", global::Orbit.Core.Localization.L.Get("Text_7E80A8B16D91"), new() { Action=ImageAction.OcrText, OcrLanguages="rus+eng", OcrDpi=200 }),
        BuiltIn("builtin-pdf-png-150", "PDF → PNG · 150 DPI", new() { Action=ImageAction.PdfPng, PdfDpi=150 }),
        BuiltIn("builtin-zip", global::Orbit.Core.Localization.L.Get("Text_3A88C6AA7772"), new() { Action=ImageAction.ArchiveZip })
    });

    public static ProcessingPreset CreateCustom(string name, ImageJobOptions options, string? id = null)
    {
        var normalized = SanitizeOptions(options);
        return new ProcessingPreset(ValidateCustomId(id ?? "custom-"+Guid.NewGuid().ToString("N")), NormalizeName(name), DescribeOptions(normalized), normalized, false);
    }

    /// <summary>Rebuilds a deserialized custom recipe from its allowed fields rather than trusting persisted metadata.</summary>
    public static ProcessingPreset ValidateCustom(ProcessingPreset preset)
    {
        if (preset is null || preset.IsBuiltIn) throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_8FA8598DE1BF"));
        return CreateCustom(preset.Name, preset.Options, ValidateCustomId(preset.Id));
    }

    public static bool IsReusableAction(ImageAction action) => ReusableActions.Contains(action);
    public static bool CanSave(ImageJobOptions options)
    { try { _=SanitizeOptions(options); return true; } catch (PresetValidationException) { return false; } }

    /// <summary>Copies only active reusable parameters. Source-specific fields and OutputDirectory always reset.</summary>
    public static ImageJobOptions SanitizeOptions(ImageJobOptions options)
    {
        if (options is null || !ReusableActions.Contains(options.Action))
            throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_F79FDB953D46"));
        if(options.SevenZipEncryptHeaders&&!options.SevenZipPasswordProtected)
            throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_66AE4A238AF6"));
        if(options.Action is ImageAction.Archive7z or ImageAction.Extract7z or ImageAction.SevenZipList&&options.SevenZipPasswordProtected)
            throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_3BE5872CC893"));
        var result = new ImageJobOptions { Action=options.Action };
        switch (options.Action)
        {
            case ImageAction.ImageWatermark:
                if(options.WatermarkMode!="Text")throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_5B78B3DEBA1D"));
                if(options.WatermarkFont is not("SegoeUI" or "Arial")||options.WatermarkFontSizePercent is <1 or >15
                    ||options.WatermarkOpacityPercent is <1 or >100||options.WatermarkMarginPercent is <0 or >20
                    ||!WatermarkPosition(options.WatermarkPosition))
                    throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_955DE0FC267C"));
                var text=WatermarkText(options.WatermarkText);
                if(options.WatermarkColor is null||options.WatermarkColor.Length!=7||options.WatermarkColor[0]!='#'
                    ||options.WatermarkColor.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF"))
                    throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_96F36141CFDF"));
                return result with{WatermarkText=text,WatermarkFont=options.WatermarkFont,WatermarkColor=options.WatermarkColor.ToUpperInvariant(),
                    WatermarkFontSizePercent=options.WatermarkFontSizePercent,WatermarkOpacityPercent=options.WatermarkOpacityPercent,
                    WatermarkPosition=options.WatermarkPosition,WatermarkMarginPercent=options.WatermarkMarginPercent};
            case ImageAction.Archive7z:
                if(options.SevenZipCompression is not("Fastest" or "Optimal" or "SmallestSize"))
                    throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_75DBD50EF137"));
                return result with{SevenZipCompression=options.SevenZipCompression};
            case ImageAction.AudioTags:
                if(!options.AudioTagSetTitle&&!options.AudioTagSetArtist&&!options.AudioTagSetAlbum)
                    throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_D9E3DD57D1BE"));
                return result with{AudioTagSetTitle=options.AudioTagSetTitle,AudioTagTitle=options.AudioTagSetTitle?TagValue(options.AudioTagTitle):"",
                    AudioTagSetArtist=options.AudioTagSetArtist,AudioTagArtist=options.AudioTagSetArtist?TagValue(options.AudioTagArtist):"",
                    AudioTagSetAlbum=options.AudioTagSetAlbum,AudioTagAlbum=options.AudioTagSetAlbum?TagValue(options.AudioTagAlbum):""};
            case ImageAction.ArchiveTar:
                if(options.TarArchiveFormat is not("TAR" or "TAR.GZ"))throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_A25A5A14B539"));
                if(options.TarArchiveFormat=="TAR.GZ"&&options.TarCompression is not("Fastest" or "Optimal" or "SmallestSize"))
                    throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_75DBD50EF137"));
                return result with{TarArchiveFormat=options.TarArchiveFormat,TarCompression=options.TarArchiveFormat=="TAR"?"Optimal":options.TarCompression};
            case ImageAction.ImageChain:
                try{return result with{ChainSteps=Chains.ImageChainPolicy.SanitizeSteps(options.ChainSteps)};}
                catch(Chains.ImageChainValidationException ex){throw new PresetValidationException(ex.Message);}
            case ImageAction.AudioNormalize:
                if(!double.IsFinite(options.NormalizeTargetLufs)||options.NormalizeTargetLufs is <-24 or >-9
                    ||!double.IsFinite(options.NormalizeTruePeakDb)||options.NormalizeTruePeakDb is <-3 or >-0.1
                    ||!double.IsFinite(options.NormalizeLoudnessRange)||options.NormalizeLoudnessRange is <1 or >20)
                    throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_5802FFED3AA6"));
                return result with{NormalizeTargetLufs=options.NormalizeTargetLufs,NormalizeTruePeakDb=options.NormalizeTruePeakDb,
                    NormalizeLoudnessRange=options.NormalizeLoudnessRange};
            case ImageAction.VideoExport:
                if(options.VideoExportWidth is <2 or >3840 || options.VideoCrf is <0 or >51 || options.VideoExportFramesPerSecond is <1 or >60)
                    throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_61EEE259C5CF"));
                return result with{VideoExportWidth=options.VideoExportWidth,VideoExportNoUpscale=options.VideoExportNoUpscale,
                    VideoExportRemoveAudio=options.VideoExportRemoveAudio,VideoCrf=options.VideoCrf,VideoExportFramesPerSecond=options.VideoExportFramesPerSecond};
            case ImageAction.AudioExport:
                bool valid=options.AudioExportFormat switch{
                    "M4A"=>options.AudioExportBitrateKbps is 64 or 96 or 128 or 160 or 192 or 256 or 320,
                    "OPUS"=>options.AudioExportBitrateKbps is 32 or 64 or 96 or 128 or 160 or 192 or 256,
                    "VORBIS"=>options.AudioExportVorbisQuality is >=0 and <=10,_=>false};
                if(!valid)throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_ACEF9A9AD561"));
                return result with{AudioExportFormat=options.AudioExportFormat,AudioExportBitrateKbps=options.AudioExportFormat=="VORBIS"?192:options.AudioExportBitrateKbps,
                    AudioExportVorbisQuality=options.AudioExportFormat=="VORBIS"?options.AudioExportVorbisQuality:5};
            case ImageAction.AudioAdjust:
                if(!double.IsFinite(options.AudioVolumePercent)||options.AudioVolumePercent is <0 or >200
                    ||!double.IsFinite(options.AudioFadeInSeconds)||options.AudioFadeInSeconds is <0 or >86400
                    ||!double.IsFinite(options.AudioFadeOutSeconds)||options.AudioFadeOutSeconds is <0 or >86400
                    ||options.AudioFadeInSeconds+options.AudioFadeOutSeconds>86400)
                    throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_A3279304EA3E"));
                return result with{AudioVolumePercent=options.AudioVolumePercent,AudioFadeInSeconds=options.AudioFadeInSeconds,AudioFadeOutSeconds=options.AudioFadeOutSeconds};
            case ImageAction.ImagesToPdf:
                if(options.Quality is <1 or >100 || options.ImagePdfPageSize is not ("A4" or "A4Landscape" or "FitImage")
                    || !double.IsFinite(options.ImagePdfMarginMillimeters) || options.ImagePdfMarginMillimeters is <0 or >40)
                    throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_1478BACD0F0D"));
                return result with{Quality=options.Quality,ImagePdfPageSize=options.ImagePdfPageSize,
                    ImagePdfMarginMillimeters=options.ImagePdfPageSize=="FitImage"?0:options.ImagePdfMarginMillimeters};
            case ImageAction.ContactSheet:
                if(options.ContactSheetColumns is <1 or >6 || options.ContactSheetThumbnailSize is not (160 or 320 or 640) || options.ContactSheetGap is <0 or >32)
                    throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_B82793C34ACE"));
                return result with{ContactSheetColumns=options.ContactSheetColumns,ContactSheetThumbnailSize=options.ContactSheetThumbnailSize,
                    ContactSheetGap=options.ContactSheetGap,ContactSheetWhiteBackground=options.ContactSheetWhiteBackground};
            case ImageAction.Png: case ImageAction.Tiff: case ImageAction.Gif: case ImageAction.Ico: case ImageAction.Svg:
                return result;
            case ImageAction.Bmp:
                return result with { JpegBackgroundColor = JpegColor(options.JpegBackgroundColor) };
            case ImageAction.Jpg: case ImageAction.WebP: case ImageAction.Avif: case ImageAction.Compress:
                if (options.Quality is <1 or >100) throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_FCC590BA541A"));
                var compression=options.Action==ImageAction.Compress?CompressionFormat(options.CompressionFormat):null;
                return result with { Quality=options.Quality, CompressionFormat=compression,
                    JpegBackgroundColor=options.Action==ImageAction.Jpg||options.Action==ImageAction.Compress&&compression is null or "jpg"
                        ?JpegColor(options.JpegBackgroundColor):"#FFFFFF" };
            case ImageAction.Resize:
                if (options.Quality is <1 or >100) throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_FCC590BA541A"));
                if(options.ResizeFilter is not("Auto" or "Lanczos" or "Mitchell" or "Point"))throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_052F402A8C7B"));
                if(options.ResizePercent is{} percent){
                    if(!double.IsFinite(percent)||percent is <1 or >400||options.Width is not null||options.Height is not null||!options.MaintainAspectRatio)
                        throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_ADA16D08B513"));
                }else if(options.Width is null&&options.Height is null||options.Width is <=0 or >100000||options.Height is <=0 or >100000)
                    throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_A7F9A6010AF0"));
                if (!options.MaintainAspectRatio && (options.Width is null || options.Height is null))
                    throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_D79BBD2C4B98"));
                return result with { Quality=options.Quality, Width=options.Width, Height=options.Height, MaintainAspectRatio=options.MaintainAspectRatio, NoUpscale=options.NoUpscale,
                    ResizePercent=options.ResizePercent,ResizeFilter=options.ResizeFilter };
            case ImageAction.ImageTransform:
                if (options.RotationDegrees is not (0 or 90 or 180 or 270)) throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_A73D911798AE"));
                return result with { RotationDegrees=options.RotationDegrees, FlipHorizontal=options.FlipHorizontal, FlipVertical=options.FlipVertical };
            case ImageAction.VideoMp4: case ImageAction.VideoWebm: case ImageAction.VideoCompress:
                if (options.VideoCrf is <0 or >51) throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_E9B5E2CCEF00"));
                CheckBitrate(options.AudioBitrateKbps);
                return result with { VideoCrf=options.VideoCrf, AudioBitrateKbps=options.AudioBitrateKbps };
            case ImageAction.AudioMp3: case ImageAction.AudioWav: case ImageAction.AudioFlac: case ImageAction.AudioCompress: case ImageAction.ExtractMp3:
                CheckBitrate(options.AudioBitrateKbps); return result with { AudioBitrateKbps=options.AudioBitrateKbps };
            case ImageAction.PdfPng: case ImageAction.PdfJpg:
                if (options.PdfDpi is <36 or >600) throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_7B1EE272C461"));
                return result with { PdfDpi=options.PdfDpi };
            case ImageAction.PdfText:
                return result with { PdfTextPreserveLayout=options.PdfTextPreserveLayout };
            case ImageAction.PdfOptimize:
                return result with { PdfOptimizeLinearize=options.PdfOptimizeLinearize };
            case ImageAction.OcrText:
                if (options.OcrDpi is <120 or >300) throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_18A8E941E498"));
                var tokens=(options.OcrLanguages??"").Trim().ToLowerInvariant().Split('+');
                if (tokens.Length is <1 or >2 || tokens.Any(t=>t is not ("rus" or "eng")) || tokens.Distinct().Count()!=tokens.Length)
                    throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_2CA9C7FB071D"));
                return result with { OcrLanguages=tokens.Length==2?"rus+eng":tokens[0], OcrDpi=options.OcrDpi };
            default: return result;
        }
    }

    public static bool CanApply(ProcessingPreset preset, IReadOnlyList<ToolkitMetadata> selection)
        => CanApply(preset, selection, out _);

    public static bool CanApply(ProcessingPreset preset, IReadOnlyList<ToolkitMetadata> selection, out string? reason)
    {
        reason=null;
        if (preset is null || selection is null || selection.Count==0) { reason=global::Orbit.Core.Localization.L.Get("Text_5E03F6CD2CE1"); return false; }
        ImageJobOptions options;
        try { options=SanitizeOptions(preset.Options); }
        catch (PresetValidationException ex) { reason=ex.Message; return false; }
        if (selection.Any(m=>m is null || !Enum.IsDefined(m.Kind) || m.Kind==ToolkitKind.Mixed)) { reason=global::Orbit.Core.Localization.L.Get("Text_063377AA16CC"); return false; }
        bool allImages=selection.All(m=>m.Kind==ToolkitKind.Image), allVideos=selection.All(m=>m.Kind==ToolkitKind.Video);
        bool applicable=options.Action switch {
            ImageAction.Jpg or ImageAction.Png or ImageAction.Bmp or ImageAction.Tiff or ImageAction.Gif or ImageAction.Ico or ImageAction.Svg or ImageAction.WebP or ImageAction.Avif or ImageAction.Resize or ImageAction.Compress or ImageAction.ImageTransform
                or ImageAction.ImagesToPdf or ImageAction.ContactSheet or ImageAction.RemoveImageMetadata or ImageAction.ImageChain or ImageAction.ImageWatermark => allImages,
            ImageAction.VideoMp4 or ImageAction.VideoWebm or ImageAction.VideoFrame or ImageAction.VideoCompress or ImageAction.VideoExport => allVideos,
            ImageAction.ExtractMp3 => allVideos && selection.All(m=>m.HasAudio),
            ImageAction.AudioMp3 or ImageAction.AudioWav or ImageAction.AudioFlac or ImageAction.AudioCompress or ImageAction.AudioAdjust or ImageAction.AudioNormalize or ImageAction.AudioExport or ImageAction.AudioCover => selection.All(m=>m.Kind==ToolkitKind.Audio && m.HasAudio),
            ImageAction.AudioTags=>selection.All(m=>m.Kind==ToolkitKind.Audio&&m.HasAudio&&Path.GetExtension(m.Path)?.ToLowerInvariant() is ".mp3" or ".flac" or ".m4a"),
            ImageAction.PdfPng or ImageAction.PdfJpg or ImageAction.PdfText or ImageAction.PdfEmbeddedImages or ImageAction.PdfOptimize => selection.All(m=>m.Kind==ToolkitKind.Pdf),
            ImageAction.OcrText => selection.All(m=>m.Kind is ToolkitKind.Image or ToolkitKind.Pdf),
            ImageAction.ExtractZip or ImageAction.ArchiveList => selection.All(m=>m.Kind==ToolkitKind.Zip),
            ImageAction.ExtractTar or ImageAction.TarList => selection.All(m=>m.Kind==ToolkitKind.Tar),
            ImageAction.Extract7z or ImageAction.SevenZipList => selection.All(m=>m.Kind==ToolkitKind.SevenZip),
            ImageAction.ArchiveZip or ImageAction.ArchiveTar or ImageAction.Archive7z or ImageAction.FileHash => true,
            _ => false
        };
        if (!applicable) { reason=options.Action==ImageAction.ExtractMp3?global::Orbit.Core.Localization.L.Get("Text_68F7A1E9AC07"):global::Orbit.Core.Localization.L.Get("Text_28CB184C6F86"); return false; }
        if(options.Action is ImageAction.Extract7z or ImageAction.SevenZipList&&selection.Any(m=>m.ArchiveLocked))
        {reason=global::Orbit.Core.Localization.L.Get("Text_2E9BF9A8A90F");return false;}
        if(options.Action is ImageAction.ExtractTar or ImageAction.TarList&&selection.Any(m=>m.Bytes is <=0 or >3L*1024*1024*1024||m.Entries is <0 or >10000))
        {reason=global::Orbit.Core.Localization.L.Get("Text_363A1CEAF1FB");return false;}
        if(options.Action is ImageAction.Extract7z or ImageAction.SevenZipList&&selection.Any(m=>m.Bytes is <=0 or >3L*1024*1024*1024||m.Entries is <0 or >10000))
        {reason=global::Orbit.Core.Localization.L.Get("Text_4BB554B260D1");return false;}
        if(options.Action is ImageAction.AudioExport or ImageAction.AudioCover or ImageAction.AudioTags&&selection.Any(m=>m.Bytes is <=0 or >2L*1024*1024*1024||!double.IsFinite(m.DurationSeconds)||m.DurationSeconds is <=0 or >86400))
        {reason=global::Orbit.Core.Localization.L.Get("Text_7C860C8B44E8");return false;}
        if(options.Action is ImageAction.PdfText or ImageAction.PdfEmbeddedImages or ImageAction.PdfOptimize
            &&selection.Any(m=>m.Bytes is <=0 or >512L*1024*1024||m.Pages is <1 or >1000))
        {reason=global::Orbit.Core.Localization.L.Get("Text_888319D3A35D");return false;}
        if(options.Action==ImageAction.ImageChain&&selection.Any(m=>m.Bytes>256L*1024*1024||m.Width<=0||m.Height<=0||m.Width>16000||m.Height>16000||(long)m.Width*m.Height>40_000_000))
        {reason=global::Orbit.Core.Localization.L.Get("Text_3A069115BA3F");return false;}
        if(options.Action==ImageAction.ImageChain&&!Chains.ImageChainPolicy.CanApply(new("chain-00000000000000000000000000000000",global::Orbit.Core.Localization.L.Get("Text_3716C0D2E45A"),options.ChainSteps),selection,out reason))return false;
        if(options.Action==ImageAction.ImageWatermark&&selection.Any(m=>m.Bytes is <=0 or >256L*1024*1024||m.Width<=0||m.Height<=0
            ||m.Width>16000||m.Height>16000||(long)m.Width*m.Height>40_000_000
            ||Path.GetExtension(m.Path).ToLowerInvariant() is not(".png" or ".jpg" or ".jpeg" or ".webp" or ".avif" or ".bmp" or ".tif" or ".tiff")))
        {reason=global::Orbit.Core.Localization.L.Get("Text_37062971CE07");return false;}
        if(options.Action==ImageAction.AudioNormalize&&selection.Any(m=>!double.IsFinite(m.DurationSeconds)||m.DurationSeconds<3||m.DurationSeconds>86400||m.Bytes>2L*1024*1024*1024))
        {reason=global::Orbit.Core.Localization.L.Get("Text_E393DBE05A2E");return false;}
        if(options.Action==ImageAction.AudioAdjust&&selection.Any(m=>!double.IsFinite(m.DurationSeconds)||m.DurationSeconds<=0
            ||m.DurationSeconds>86400||m.Bytes>2L*1024*1024*1024||options.AudioFadeInSeconds+options.AudioFadeOutSeconds>m.DurationSeconds))
        {reason=global::Orbit.Core.Localization.L.Get("Text_B84A0699D542");return false;}
        if(options.Action is ImageAction.ImagesToPdf or ImageAction.ContactSheet && selection.Count>100)
        {reason=global::Orbit.Core.Localization.L.Get("Text_0CA8439A7B4B");return false;}
        if(options.Action==ImageAction.ContactSheet){
            int rows=(selection.Count+options.ContactSheetColumns-1)/options.ContactSheetColumns;
            long width=(long)options.ContactSheetColumns*options.ContactSheetThumbnailSize+(options.ContactSheetColumns+1)*options.ContactSheetGap;
            long height=(long)rows*options.ContactSheetThumbnailSize+(rows+1)*options.ContactSheetGap;
            if(width>16000||height>16000||width*height>40_000_000){reason=global::Orbit.Core.Localization.L.Get("Text_31B596CF46DC");return false;}
        }
        if (options.Action==ImageAction.OcrText && selection.Any(m=>m.Bytes>Ocr.OcrEngine.MaximumSourceBytes || m.Kind==ToolkitKind.Pdf && m.Pages>Ocr.OcrEngine.MaximumPdfPages
            || m.Kind==ToolkitKind.Image && ((long)m.Width*m.Height>Ocr.OcrEngine.MaximumPagePixels || m.Width>16000 || m.Height>16000)))
        { reason=global::Orbit.Core.Localization.L.Get("Text_1F51CCC1DA9E"); return false; }
        return true;
    }

    public static string DescribeOptions(ImageJobOptions options)
    {
        var safe=SanitizeOptions(options);
        return safe.Action switch {
            ImageAction.ImageWatermark=>global::Orbit.Core.Localization.L.Format("Text_C7754B23E252",(object?)(DescribeTag(safe.WatermarkText)),(object?)(safe.WatermarkFont),(object?)(safe.WatermarkFontSizePercent),(object?)(safe.WatermarkColor),(object?)(safe.WatermarkOpacityPercent),(object?)(safe.WatermarkPosition),(object?)(safe.WatermarkMarginPercent)),
            ImageAction.ImageChain=>Chains.ImageChainPolicy.DescribeSteps(safe.ChainSteps),
            ImageAction.AudioNormalize=>global::Orbit.Core.Localization.L.Format("Text_0269708BC27D",(object?)(safe.NormalizeTargetLufs.ToString("0.##",CultureInfo.InvariantCulture)),(object?)(safe.NormalizeTruePeakDb.ToString("0.##",CultureInfo.InvariantCulture)),(object?)(safe.NormalizeLoudnessRange.ToString("0.##",CultureInfo.InvariantCulture))),
            ImageAction.VideoExport=>global::Orbit.Core.Localization.L.Format("Text_DB8427339350",(object?)(safe.VideoExportWidth?.ToString(CultureInfo.InvariantCulture)??global::Orbit.Core.Localization.L.Get("Text_E544A078467B")),(object?)(safe.VideoCrf))
                +(safe.VideoExportNoUpscale?global::Orbit.Core.Localization.L.Get("Text_C2C0020DF0E7"):"")+(safe.VideoExportRemoveAudio?global::Orbit.Core.Localization.L.Get("Text_3A4B88423F33"):global::Orbit.Core.Localization.L.Get("Text_7D7E521E6A8A"))
                +(safe.VideoExportFramesPerSecond is{} fps?$" · {fps} FPS":global::Orbit.Core.Localization.L.Get("Text_F79A5920B290")),
            ImageAction.AudioExport=>safe.AudioExportFormat=="VORBIS"?global::Orbit.Core.Localization.L.Format("Text_670D9C81AC06",(object?)(safe.AudioExportVorbisQuality))
                :(safe.AudioExportFormat=="M4A"?"M4A · AAC":global::Orbit.Core.Localization.L.Get("Text_F3C5211D17CF"))+global::Orbit.Core.Localization.L.Format("Text_14727EBE1884",(object?)(safe.AudioExportBitrateKbps)),
            ImageAction.AudioTags=>global::Orbit.Core.Localization.L.Get("Text_0C1D72A49020")+string.Join(" · ",new[]{(safe.AudioTagSetTitle,global::Orbit.Core.Localization.L.Get("Text_0918B4BA9268"),safe.AudioTagTitle),
                (safe.AudioTagSetArtist,global::Orbit.Core.Localization.L.Get("Text_037174D58809"),safe.AudioTagArtist),(safe.AudioTagSetAlbum,global::Orbit.Core.Localization.L.Get("Text_0928B8D4C742"),safe.AudioTagAlbum)}
                .Where(tag=>tag.Item1).Select(tag=>tag.Item2+": "+DescribeTag(tag.Item3))),
            ImageAction.AudioCover=>global::Orbit.Core.Localization.L.Get("Text_9F07C9D3CF3A"),
            ImageAction.AudioAdjust=>global::Orbit.Core.Localization.L.Format("Text_962F67BB2E6C",(object?)(safe.AudioVolumePercent.ToString("0.##",CultureInfo.InvariantCulture)),(object?)(safe.AudioFadeInSeconds.ToString("0.###",CultureInfo.InvariantCulture)),(object?)(safe.AudioFadeOutSeconds.ToString("0.###",CultureInfo.InvariantCulture))),
            ImageAction.ImagesToPdf=>global::Orbit.Core.Localization.L.Format("Text_BDF47EBEBDDA",(object?)(safe.ImagePdfPageSize),(object?)(safe.ImagePdfMarginMillimeters.ToString(CultureInfo.InvariantCulture)),(object?)(safe.Quality)),
            ImageAction.ContactSheet=>global::Orbit.Core.Localization.L.Format("Text_5F946075F2EB",(object?)(safe.ContactSheetColumns),(object?)(safe.ContactSheetThumbnailSize),(object?)(safe.ContactSheetGap))+(safe.ContactSheetWhiteBackground?global::Orbit.Core.Localization.L.Get("Text_D208157BE240"):global::Orbit.Core.Localization.L.Get("Text_9EE8E9F07D31")),
            ImageAction.RemoveImageMetadata=>global::Orbit.Core.Localization.L.Get("Text_65FB42065711"),
            ImageAction.Jpg => global::Orbit.Core.Localization.L.Format("Text_5A153245DCDF",(object?)(safe.Quality),(object?)(safe.JpegBackgroundColor)),
            ImageAction.Png => global::Orbit.Core.Localization.L.Get("Text_EA6B67A17269"),
            ImageAction.Bmp => global::Orbit.Core.Localization.L.Format("Text_7B489E1D7096",(object?)(safe.JpegBackgroundColor)),
            ImageAction.Tiff => global::Orbit.Core.Localization.L.Get("Text_B42DC751392A"),
            ImageAction.Gif => global::Orbit.Core.Localization.L.Get("Text_A553BEC4137D"),
            ImageAction.Ico => global::Orbit.Core.Localization.L.Get("Text_21CAD9952407"),
            ImageAction.Svg => global::Orbit.Core.Localization.L.Get("Text_9F813F2FBB96"),
            ImageAction.WebP => global::Orbit.Core.Localization.L.Format("Text_53D923092E20",(object?)(safe.Quality)), ImageAction.Avif => global::Orbit.Core.Localization.L.Format("Text_985DAA92D571",(object?)(safe.Quality)),
            ImageAction.Compress => global::Orbit.Core.Localization.L.Format("Text_7029E138D89C",(object?)(safe.CompressionFormat?.ToUpperInvariant()??global::Orbit.Core.Localization.L.Get("Text_F2328E4EBEB2")),(object?)(safe.Quality))
                +(safe.CompressionFormat is null or "jpg"?global::Orbit.Core.Localization.L.Format("Text_40E40996BE06",(object?)(safe.JpegBackgroundColor)):""),
            ImageAction.Resize => (safe.ResizePercent is{} percent?$"{percent.ToString("0.###",CultureInfo.InvariantCulture)}%":$"{(safe.Width?.ToString(CultureInfo.InvariantCulture)??global::Orbit.Core.Localization.L.Get("Text_4AF423E9B1A0"))} × {(safe.Height?.ToString(CultureInfo.InvariantCulture)??global::Orbit.Core.Localization.L.Get("Text_4AF423E9B1A0"))} px")
                +" · "+(safe.MaintainAspectRatio?global::Orbit.Core.Localization.L.Get("Text_2DD9FD6B69C9"):global::Orbit.Core.Localization.L.Get("Text_68009B1A6A27"))+(safe.NoUpscale?global::Orbit.Core.Localization.L.Get("Text_C2C0020DF0E7"):"")+global::Orbit.Core.Localization.L.Get("Text_7E13477CFA3B")+safe.ResizeFilter,
            ImageAction.ImageTransform => global::Orbit.Core.Localization.L.Format("Text_E89BF001E30A",(object?)(safe.RotationDegrees))+(safe.FlipHorizontal?global::Orbit.Core.Localization.L.Get("Text_5EE87F1788BB"):"")+(safe.FlipVertical?global::Orbit.Core.Localization.L.Get("Text_874028C21DF6"):"")+" · PNG",
            ImageAction.VideoMp4 => global::Orbit.Core.Localization.L.Format("Text_FE55D92AD4F0",(object?)(safe.VideoCrf),(object?)(safe.AudioBitrateKbps)),
            ImageAction.VideoWebm => global::Orbit.Core.Localization.L.Format("Text_15CA9ADE9B9C",(object?)(safe.VideoCrf),(object?)(safe.AudioBitrateKbps)),
            ImageAction.VideoCompress => global::Orbit.Core.Localization.L.Get("Text_C121C7C52023"),
            ImageAction.AudioMp3 or ImageAction.ExtractMp3 => global::Orbit.Core.Localization.L.Format("Text_BF2737888B5E",(object?)(safe.AudioBitrateKbps)),
            ImageAction.AudioWav => global::Orbit.Core.Localization.L.Get("Text_7C30F5AC76F7"), ImageAction.AudioFlac => global::Orbit.Core.Localization.L.Get("Text_47E947929CD3"), ImageAction.AudioCompress => global::Orbit.Core.Localization.L.Get("Text_BF41DF69F881"),
            ImageAction.PdfPng => global::Orbit.Core.Localization.L.Format("Text_F38F6ACB7803",(object?)(safe.PdfDpi)), ImageAction.PdfJpg => global::Orbit.Core.Localization.L.Format("Text_BF090DF8C1F5",(object?)(safe.PdfDpi)),
            ImageAction.PdfText => global::Orbit.Core.Localization.L.Get("Text_33160019DB7F")+(safe.PdfTextPreserveLayout?global::Orbit.Core.Localization.L.Get("Text_19D2A9079963"):global::Orbit.Core.Localization.L.Get("Text_A2A6657C2E1A")),
            ImageAction.PdfEmbeddedImages => global::Orbit.Core.Localization.L.Get("Text_CBA7E9411327"),
            ImageAction.PdfOptimize => global::Orbit.Core.Localization.L.Get("Text_3117AC6010EF")+(safe.PdfOptimizeLinearize?global::Orbit.Core.Localization.L.Get("Text_A753CDD2994D"):"")+global::Orbit.Core.Localization.L.Get("Text_9DAF444C5C8F"),
            ImageAction.OcrText => $"OCR · {safe.OcrLanguages} · {safe.OcrDpi} DPI · TXT",
            ImageAction.ArchiveZip => global::Orbit.Core.Localization.L.Get("Text_82089FCD2695"),
            ImageAction.ArchiveTar => global::Orbit.Core.Localization.L.Get("Text_83ACE9EA4E8A")+safe.TarArchiveFormat+global::Orbit.Core.Localization.L.Get("Text_781BBC984EE6")+(safe.TarArchiveFormat=="TAR.GZ"?global::Orbit.Core.Localization.L.Get("Text_143433C71497")+safe.TarCompression:global::Orbit.Core.Localization.L.Get("Text_7517BD585F51")),
            ImageAction.ExtractTar => global::Orbit.Core.Localization.L.Get("Text_14CE3A689811"),
            ImageAction.TarList => global::Orbit.Core.Localization.L.Get("Text_81B9277EF970"),
            ImageAction.Archive7z => global::Orbit.Core.Localization.L.Get("Text_11D9900979B8")+safe.SevenZipCompression,
            ImageAction.Extract7z => global::Orbit.Core.Localization.L.Get("Text_0EB317501676"),
            ImageAction.SevenZipList => global::Orbit.Core.Localization.L.Get("Text_F6C9B1BFE452"),
            ImageAction.ExtractZip => global::Orbit.Core.Localization.L.Get("Text_8BB61756FE99"), ImageAction.ArchiveList => global::Orbit.Core.Localization.L.Get("Text_6F0080778913"), ImageAction.FileHash => global::Orbit.Core.Localization.L.Get("Text_CFC5CF63C252"),
            _ => throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_88E65983A3B9"))
        };
    }

    private static ProcessingPreset BuiltIn(string id,string name,ImageJobOptions options)
        => new(id,name,DescribeOptions(options),SanitizeOptions(options),true);
    private static string NormalizeName(string name)
    {
        if (name is null) throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_9AF69E17247D"));
        try { name=name.Trim().Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_20E81219A113")); }
        if (name.Length is <1 or >MaximumNameLength || name.Any(char.IsControl)) throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_C79CB8BA5A2A"));
        return name;
    }
    private static string ValidateCustomId(string id)
    {
        if (id is null || !id.StartsWith("custom-",StringComparison.Ordinal) || id.Length!=39 || !Guid.TryParseExact(id[7..],"N",out _))
            throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_AC79EB785282"));
        return id.ToLowerInvariant();
    }
    private static string? CompressionFormat(string? format)
        => string.IsNullOrWhiteSpace(format)?null:format.Trim().TrimStart('.').ToLowerInvariant() switch {
            "jpg" or "jpeg"=>"jpg", "png"=>"png", "webp"=>"webp", "avif"=>"avif",
            _=>throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_239619850BFE"))
        };
    private static string JpegColor(string? color)
    {
        if(color is null||color.Length!=7||color[0]!='#'||color.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF"))
            throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_DC6BF586BBA2"));
        return color.ToUpperInvariant();
    }
    private static void CheckBitrate(int bitrate)
    { if (bitrate is <32 or >320) throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_7142AB489D65")); }
    private static string TagValue(string? value)
    {
        if(value is null||value.Length>512)throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_0D96978CB789"));
        for(int index=0;index<value.Length;index++){
            char character=value[index];if(char.IsControl(character)||character is '\u2028' or '\u2029')throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_03A000F5042C"));
            if(char.IsHighSurrogate(character)){if(++index>=value.Length||!char.IsLowSurrogate(value[index]))throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_E8BE4AA223F1"));}
            else if(char.IsLowSurrogate(character))throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_E8BE4AA223F1"));
        }
        return value;
    }
    private static bool WatermarkPosition(string? value)=>value is "TopLeft" or "Top" or "TopRight" or "Left" or "Center" or "Right" or "BottomLeft" or "Bottom" or "BottomRight";
    private static string WatermarkText(string? value)
    {
        if(string.IsNullOrWhiteSpace(value)||value.Length>200)throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_9884D933DE8F"));
        for(int index=0;index<value.Length;index++){
            char character=value[index];if(char.IsControl(character)||character is '\u2028' or '\u2029')throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_D01A8122F094"));
            if(char.IsHighSurrogate(character)){if(++index>=value.Length||!char.IsLowSurrogate(value[index]))throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_322E1A5BA70D"));}
            else if(char.IsLowSurrogate(character))throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_322E1A5BA70D"));
        }
        return value;
    }
    private static string DescribeTag(string value)
    {
        if(value.Length==0)return global::Orbit.Core.Localization.L.Get("Text_5ABEDC2B3A68");
        if(value.Length>64){value=value[..64];if(char.IsHighSurrogate(value[^1]))value=value[..^1];value+="…";}
        return "«"+value+"»";
    }
}
