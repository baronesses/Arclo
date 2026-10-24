using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Orbit.Core.Documents;
using Orbit.Core.Ocr;
using Orbit.Core.ContactSheets;
using Orbit.Core.Metadata;
using Orbit.Core.Media;
using Orbit.Core.Chains;
using Orbit.Core.Watermarks;

namespace Orbit.Core;

public enum ToolkitKind { Image, Video, Audio, Pdf, Zip, Other, Mixed, Folder, Tar, SevenZip }

public sealed record ToolkitMetadata(string Path,string Format,int Width,int Height,long Bytes,
    ToolkitKind Kind,double DurationSeconds=0,int Pages=0,bool HasAudio=false,int Entries=0,bool ArchiveLocked=false)
{
    public string DurationDisplay=>DurationLabel(DurationSeconds);
    public string Description => Kind switch
    {
        ToolkitKind.Image=>$"{Format} · {Width} × {Height} px",
        ToolkitKind.Video=>$"{Format} · {Width} × {Height} · {DurationLabel(DurationSeconds)}"+(HasAudio?global::Orbit.Core.Localization.L.Get("Text_85F55433FF38"):global::Orbit.Core.Localization.L.Get("Text_3A4B88423F33")),
        ToolkitKind.Audio=>$"{Format} · {DurationLabel(DurationSeconds)}",
        ToolkitKind.Pdf=>global::Orbit.Core.Localization.L.Format("Text_93CB89CEB301",(object?)(Pages)),
        ToolkitKind.Zip=>global::Orbit.Core.Localization.L.Format("Text_F6E7326F4523",(object?)(Entries)),
        ToolkitKind.Tar=>global::Orbit.Core.Localization.L.Format("Text_0A63E014CFAE",(object?)(Format),(object?)(Entries)),
        ToolkitKind.SevenZip=>ArchiveLocked?global::Orbit.Core.Localization.L.Get("Text_E6BBB14807BA"):global::Orbit.Core.Localization.L.Format("Text_79D9BDA84691",(object?)(Entries)),
        ToolkitKind.Folder=>global::Orbit.Core.Localization.L.Get("Text_AE0120FE91C5"),
        _=>Format+global::Orbit.Core.Localization.L.Get("Text_38C37C3453F6")
    };
    private static string DurationLabel(double seconds)
    {
        var time=TimeSpan.FromSeconds(Math.Max(0,seconds));
        return seconds>=3600?FormattableString.Invariant($"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"):time.ToString(@"m\:ss",CultureInfo.InvariantCulture);
    }
}

/// <summary>Routes a local file to its real image, media, PDF or ZIP decoder.</summary>
public sealed class ToolkitEngine
{
    private readonly ImageMagickEngine _images=new();
    private readonly MediaEngine _media=new();
    private readonly DocumentEngine _documents=new();
    private readonly PdfAssemblyEngine _pdfAssembly=new();
    private readonly ArchiveEngine _archives=new();
    private readonly OcrEngine _ocr=new();
    private readonly ImagePdfEngine _imagePdf=new();
    private readonly ContactSheetEngine _contactSheets=new();
    private readonly ImageMetadataCleaner _metadataCleaner=new();
    private readonly VideoGifEngine _videoGif=new();
    private readonly VideoExportEngine _videoExport=new();
    private readonly AudioAdjustEngine _audioAdjust=new();
    private readonly PdfPageToolsEngine _pdfPageTools=new();
    private readonly ZipBrowserEngine _zipBrowser=new();
    private readonly AudioNormalizeEngine _audioNormalize=new();
    private readonly ImageChainEngine _imageChains=new();
    private readonly PdfExtractEngine _pdfExtract=new();
    private readonly PdfOptimizeEngine _pdfOptimize=new();
    private readonly AudioExportEngine _audioExport=new();
    private readonly VideoFramesEngine _videoFrames=new();
    private readonly MediaTracksEngine _mediaTracks=new();
    private readonly MediaCoverEngine _mediaCover=new();
    private readonly TarArchiveEngine _tarArchives=new();
    private readonly AudioTagsEngine _audioTags=new();
    private readonly SevenZipArchiveEngine _sevenZip=new();
    private readonly ImageWatermarkEngine _watermarks=new();
    public bool PdfTextAvailable=>_pdfExtract.TextExtractionAvailable;
    public bool PdfImagesAvailable=>_pdfExtract.ImagesExtractionAvailable;
    public bool PdfOptimizeAvailable=>_pdfOptimize.Available;
    private static readonly HashSet<string> ImageExtensions=new(StringComparer.OrdinalIgnoreCase){".png",".jpg",".jpeg",".webp",".avif",".bmp",".tif",".tiff",".gif",".ico",".svg"};
    private static readonly HashSet<string> MediaExtensions=new(StringComparer.OrdinalIgnoreCase){".mp4",".mov",".mkv",".webm",".avi",".m4v",".mp3",".wav",".flac",".aac",".m4a",".ogg",".opus",".wma",".aiff",".aif"};

    public async Task<ToolkitMetadata> InspectAsync(string path,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();path=Path.GetFullPath(path);
        if(Directory.Exists(path)) {
            path=Path.TrimEndingDirectorySeparator(path);
            if(string.Equals(path,Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!),StringComparison.OrdinalIgnoreCase))throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_A91645666099"));
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_42B942CC82FB"));
            return new(path,global::Orbit.Core.Localization.L.Get("Text_73A0F69D548D"),0,0,0,ToolkitKind.Folder);
        }
        if(!File.Exists(path))throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_9AB39B13C07B"));
        var ext=Path.GetExtension(path);
        if(ext.Equals(".7z",StringComparison.OrdinalIgnoreCase)){
            try{
                var listing=await _sevenZip.InspectAsync(path,ct).ConfigureAwait(false);
                return new(path,"7Z",0,0,new FileInfo(path).Length,ToolkitKind.SevenZip,Entries:listing.Entries.Count);
            }catch(SevenZipPasswordRequiredException){
                return new(path,"7Z",0,0,new FileInfo(path).Length,ToolkitKind.SevenZip,ArchiveLocked:true);
            }
        }
        if(IsTarPath(path)){
            var listing=await _tarArchives.InspectAsync(path,ct).ConfigureAwait(false);
            return new(path,listing.Format,0,0,new FileInfo(path).Length,ToolkitKind.Tar,Entries:listing.Entries.Count);
        }
        if(ImageExtensions.Contains(ext)) {
            var m=await _images.InspectAsync(path,ct).ConfigureAwait(false);
            return new(m.Path,m.Format,m.Width,m.Height,m.Bytes,ToolkitKind.Image);
        }
        if(MediaExtensions.Contains(ext)) {
            var m=await _media.InspectAsync(path,ct).ConfigureAwait(false);
            return new(m.Path,m.Format,m.Width,m.Height,m.Bytes,m.Kind=="Video"?ToolkitKind.Video:ToolkitKind.Audio,m.DurationSeconds,HasAudio:m.HasAudio);
        }
        if(ext.Equals(".pdf",StringComparison.OrdinalIgnoreCase)) {
            var m=await _documents.InspectPdfAsync(path,ct).ConfigureAwait(false);
            return new(m.Path,"PDF",0,0,m.Bytes,ToolkitKind.Pdf,Pages:m.Pages);
        }
        if(ext.Equals(".zip",StringComparison.OrdinalIgnoreCase)) {
            var m=await _archives.InspectZipAsync(path,ct).ConfigureAwait(false);
            return new(m.Path,"ZIP",0,0,m.Bytes,ToolkitKind.Zip,Entries:m.Entries);
        }
        return new(path,string.IsNullOrEmpty(ext)?global::Orbit.Core.Localization.L.Get("Text_BE75284575E0"):ext.TrimStart('.').ToUpperInvariant(),0,0,new FileInfo(path).Length,ToolkitKind.Other);
    }
    public async Task<byte[]?> CreatePreviewAsync(string path,int maxSize=640,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();if(Directory.Exists(path))return null;
        var ext=Path.GetExtension(path);
        if(ImageExtensions.Contains(ext))return await _images.CreatePreviewAsync(path,maxSize,ct).ConfigureAwait(false);
        if(ext.Equals(".pdf",StringComparison.OrdinalIgnoreCase))return await _documents.PdfPreviewAsync(path,maxSize,ct).ConfigureAwait(false);
        if(MediaExtensions.Contains(ext)) {
            var m=await _media.InspectAsync(path,ct).ConfigureAwait(false);
            if(m.Kind=="Video")return await _media.CreatePreviewAsync(path,maxSize,ct).ConfigureAwait(false);
        }
        return null;
    }
    public static WatermarkOptions WatermarkParameters(ImageJobOptions options)
    {
        if(!Enum.TryParse<WatermarkMode>(options.WatermarkMode,false,out var mode)||!Enum.IsDefined(mode)
            ||!Enum.TryParse<WatermarkPosition>(options.WatermarkPosition,false,out var position)||!Enum.IsDefined(position))
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_CF62323928FD"));
        return new WatermarkOptions{Mode=mode,Text=options.WatermarkText,FontFamily=options.WatermarkFont,Color=options.WatermarkColor,
            RelativeSizePercent=options.WatermarkFontSizePercent,OpacityPercent=options.WatermarkOpacityPercent,Position=position,MarginPercent=options.WatermarkMarginPercent,
            LogoPath=options.WatermarkLogoPath,LogoSha256=options.WatermarkLogoSha256,LogoWidthPercent=options.WatermarkLogoWidthPercent,OutputDirectory=options.OutputDirectory};
    }
    private static void RequireArchiveSelection(ImageJobOptions options)
    {
        if(options.ArchiveSelectedEntries is null||options.ArchiveSelectedEntries.Count is <1 or >1000||options.ArchiveSelectedCount!=options.ArchiveSelectedEntries.Count
            ||options.ArchiveSourceSha256 is not {Length:64}||options.ArchiveSourceSha256.Any(c=>!char.IsAsciiHexDigit(c)))
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_F7C680AED3D1"));
    }
    public async Task<ImageJobResult> ExecuteWithSevenZipPasswordAsync(IReadOnlyList<string> paths,ImageJobOptions options,SevenZipPassword password,IProgress<double>? progress=null,CancellationToken ct=default)
    {
        if(paths.Count==0)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_AAB6E61E4AAA"));
        if(!options.SevenZipPasswordProtected)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_337B274568D5"));
        DocumentResult result;
        if(options.Action==ImageAction.Archive7z){
            result=await _sevenZip.CreateWithPasswordAsync(paths,new SevenZipCreateOptions{Compression=options.SevenZipCompression,OutputDirectory=options.OutputDirectory},password,options.SevenZipEncryptHeaders,progress,ct).ConfigureAwait(false);
        }else if(options.Action==ImageAction.SevenZipExtractSelected){
            if(paths.Count!=1||options.SevenZipEncryptHeaders)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_993FDE8317BA"));
            RequireArchiveSelection(options);
            result=await _sevenZip.ExtractSelectedAsync(paths[0],options.ArchiveSelectedEntries,options.OutputDirectory,options.ArchiveSourceSha256!,password,progress,ct).ConfigureAwait(false);
        }else if(options.Action is ImageAction.Extract7z or ImageAction.SevenZipList){
            if(paths.Count!=1||options.SevenZipEncryptHeaders)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_993FDE8317BA"));
            result=options.Action==ImageAction.Extract7z
                ?await _sevenZip.ExtractAsync(paths[0],options.OutputDirectory,password,progress,ct).ConfigureAwait(false)
                :await _sevenZip.WriteListAsync(paths[0],options.OutputDirectory,password,ct).ConfigureAwait(false);
        }else throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_145C7150030A"));
        return new(result.OutputPath,result.Bytes,0,0,result.Format,result.SourceBytes);
    }
    public async Task<ImageJobResult> ExecuteAsync(IReadOnlyList<string> paths,ImageJobOptions options,IProgress<double>? progress=null,CancellationToken ct=default)
    {
        if(paths.Count==0)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_AAB6E61E4AAA"));
        if(options.SevenZipPasswordProtected||options.SevenZipEncryptHeaders)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_DEF123C6770D"));
        if(options.Action==ImageAction.ImageWatermark){
            if(paths.Count!=1)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_C24549C99D0D"));
            return await _watermarks.ApplyAsync(paths[0],WatermarkParameters(options),progress,ct).ConfigureAwait(false);
        }
        if(options.Action is ImageAction.SevenZipExtractSelected or ImageAction.TarExtractSelected){
            if(paths.Count!=1)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_556F533BC350"));
            RequireArchiveSelection(options);
            var selected=options.Action==ImageAction.SevenZipExtractSelected
                ?await _sevenZip.ExtractSelectedAsync(paths[0],options.ArchiveSelectedEntries,options.OutputDirectory,options.ArchiveSourceSha256!,progress,ct).ConfigureAwait(false)
                :await _tarArchives.ExtractSelectedAsync(paths[0],options.ArchiveSelectedEntries,options.OutputDirectory,options.ArchiveSourceSha256!,progress,ct).ConfigureAwait(false);
            return new(selected.OutputPath,selected.Bytes,0,0,selected.Format,selected.SourceBytes);
        }
        if(options.Action==ImageAction.AudioTags){
            if(paths.Count!=1)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_30188A03BBFC"));
            var tagged=await _audioTags.UpdateAsync(paths[0],new AudioTagOptions{
                SetTitle=options.AudioTagSetTitle,Title=options.AudioTagTitle,SetArtist=options.AudioTagSetArtist,Artist=options.AudioTagArtist,
                SetAlbum=options.AudioTagSetAlbum,Album=options.AudioTagAlbum,OutputDirectory=options.OutputDirectory},progress,ct).ConfigureAwait(false);
            return new(tagged.OutputPath,tagged.Bytes,tagged.Width,tagged.Height,tagged.Format,tagged.SourceBytes);
        }
        if(options.Action==ImageAction.Archive7z){
            var archive=await _sevenZip.CreateAsync(paths,new SevenZipCreateOptions{Compression=options.SevenZipCompression,OutputDirectory=options.OutputDirectory},progress,ct).ConfigureAwait(false);
            return new(archive.OutputPath,archive.Bytes,0,0,archive.Format,archive.SourceBytes);
        }
        if(options.Action is ImageAction.Extract7z or ImageAction.SevenZipList){
            if(paths.Count!=1)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_993FDE8317BA"));
            var archive=options.Action==ImageAction.Extract7z
                ?await _sevenZip.ExtractAsync(paths[0],options.OutputDirectory,progress,ct).ConfigureAwait(false)
                :await _sevenZip.WriteListAsync(paths[0],options.OutputDirectory,ct).ConfigureAwait(false);
            return new(archive.OutputPath,archive.Bytes,0,0,archive.Format,archive.SourceBytes);
        }
        if(options.Action==ImageAction.ArchiveTar){
            var archive=await _tarArchives.CreateAsync(paths,new TarCreateOptions{Format=options.TarArchiveFormat,
                Compression=options.TarCompression,OutputDirectory=options.OutputDirectory},progress,ct).ConfigureAwait(false);
            return new(archive.OutputPath,archive.Bytes,0,0,archive.Format,archive.SourceBytes);
        }
        if(options.Action is ImageAction.ExtractTar or ImageAction.TarList){
            if(paths.Count!=1)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_0ABB2DEA1B19"));
            var archive=options.Action==ImageAction.ExtractTar
                ?await _tarArchives.ExtractAsync(paths[0],options.OutputDirectory,progress,ct).ConfigureAwait(false)
                :await _tarArchives.WriteListAsync(paths[0],options.OutputDirectory,ct).ConfigureAwait(false);
            return new(archive.OutputPath,archive.Bytes,0,0,archive.Format,archive.SourceBytes);
        }
        if(options.Action is ImageAction.AudioTrackExtract or ImageAction.AudioCover){
            if(paths.Count!=1)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_BD14B2CA4F2D"));
            var audio=options.Action==ImageAction.AudioCover
                ?await _mediaCover.ExtractAsync(paths[0],new MediaCoverOptions{OutputDirectory=options.OutputDirectory},progress,ct).ConfigureAwait(false)
                :await _mediaTracks.ExtractAudioAsync(paths[0],new AudioTrackExtractOptions{
                    StreamIndex=options.AudioTrackStreamIndex??throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_F6888A2B038D")),
                    SourceSha256=options.AudioTrackSourceSha256??throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_E30E07E3B976")),
                    Format=options.AudioTrackFormat,BitrateKbps=options.AudioTrackBitrateKbps,OutputDirectory=options.OutputDirectory},progress,ct).ConfigureAwait(false);
            return new(audio.OutputPath,audio.Bytes,audio.Width,audio.Height,audio.Format,audio.SourceBytes);
        }
        if(options.Action is ImageAction.AudioExport or ImageAction.VideoFrames){
            if(paths.Count!=1)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_1950B76AE9CE"));
            var exported=options.Action==ImageAction.AudioExport
                ?await _audioExport.ExportAsync(paths[0],new AudioExportOptions{OutputDirectory=options.OutputDirectory,
                    Format=options.AudioExportFormat,BitrateKbps=options.AudioExportBitrateKbps,VorbisQuality=options.AudioExportVorbisQuality},progress,ct).ConfigureAwait(false)
                :await _videoFrames.ExtractAsync(paths[0],new VideoFramesOptions{OutputDirectory=options.OutputDirectory,
                    StartSeconds=options.FramesStartSeconds,DurationSeconds=options.FramesDurationSeconds,
                    FramesPerSecond=options.FramesPerSecond,Width=options.FramesWidth},progress,ct).ConfigureAwait(false);
            return new(exported.OutputPath,exported.Bytes,exported.Width,exported.Height,exported.Format,exported.SourceBytes);
        }
        if(options.Action is ImageAction.PdfText or ImageAction.PdfEmbeddedImages or ImageAction.PdfOptimize){
            if(paths.Count!=1)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_2DBD182C35DA"));
            var pdf=options.Action switch{
                ImageAction.PdfText=>await _pdfExtract.ExtractTextAsync(paths[0],new PdfTextOptions{OutputDirectory=options.OutputDirectory,PreserveLayout=options.PdfTextPreserveLayout},ct).ConfigureAwait(false),
                ImageAction.PdfEmbeddedImages=>await _pdfExtract.ExtractImagesAsync(paths[0],new PdfEmbeddedImagesOptions{OutputDirectory=options.OutputDirectory},ct).ConfigureAwait(false),
                _=>await _pdfOptimize.OptimizeAsync(paths[0],new PdfOptimizeOptions{OutputDirectory=options.OutputDirectory,Linearize=options.PdfOptimizeLinearize},ct).ConfigureAwait(false)
            };
            return new(pdf.OutputPath,pdf.Bytes,0,0,pdf.Format,pdf.SourceBytes);
        }
        if(options.Action==ImageAction.ImageChain){
            if(paths.Count!=1)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_FDE78EC50A51"));
            return await _imageChains.ExecuteAsync(paths[0],options.ChainSteps,options.OutputDirectory,progress,ct).ConfigureAwait(false);
        }
        if(options.Action==ImageAction.AudioNormalize){
            if(paths.Count!=1)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_F85D67023E59"));
            var normalized=await _audioNormalize.NormalizeAsync(paths[0],new AudioNormalizeOptions{OutputDirectory=options.OutputDirectory,
                TargetLufs=options.NormalizeTargetLufs,TruePeakDb=options.NormalizeTruePeakDb,LoudnessRange=options.NormalizeLoudnessRange},progress,ct).ConfigureAwait(false);
            return new(normalized.OutputPath,normalized.Bytes,normalized.Width,normalized.Height,normalized.Format,normalized.SourceBytes);
        }
        if(options.Action is ImageAction.PdfRotate or ImageAction.PdfSplit or ImageAction.ZipExtractSelected){
            if(paths.Count!=1)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_99AEE1E9C075"));
            var document=options.Action switch{
                ImageAction.PdfRotate=>await _pdfPageTools.RotateAsync(paths[0],new PdfRotateOptions{OutputDirectory=options.OutputDirectory,
                    RotationDegrees=options.PdfRotationDegrees,PageSelection=options.PdfPageSelection},ct).ConfigureAwait(false),
                ImageAction.PdfSplit=>await _pdfPageTools.SplitAsync(paths[0],new PdfSplitOptions{OutputDirectory=options.OutputDirectory,
                    PagesPerFile=options.PdfSplitPagesPerFile},ct).ConfigureAwait(false),
                _=>await _zipBrowser.ExtractSelectedAsync(paths[0],options.ZipSelectedEntries,options.OutputDirectory,
                    options.ZipSourceSha256??throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_2511264EC204")),ct).ConfigureAwait(false)
            };
            return new(document.OutputPath,document.Bytes,0,0,document.Format,document.SourceBytes);
        }
        if(options.Action==ImageAction.ImagesToPdf){
            var pageSize=options.ImagePdfPageSize switch{
                "A4"=>ImagePdfPageSize.A4,"A4Landscape"=>ImagePdfPageSize.A4Landscape,"FitImage"=>ImagePdfPageSize.FitImage,
                _=>throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_44AED6BAAB1B"))
            };
            return await _imagePdf.CreateAsync(paths,new ImagePdfOptions{OutputDirectory=options.OutputDirectory,PageSize=pageSize,
                MarginMillimeters=options.ImagePdfMarginMillimeters,JpegQuality=options.Quality},ct).ConfigureAwait(false);
        }
        if(options.Action==ImageAction.ContactSheet)return await _contactSheets.CreateAsync(paths,new ContactSheetOptions{
            OutputDirectory=options.OutputDirectory,Columns=options.ContactSheetColumns,ThumbnailSize=options.ContactSheetThumbnailSize,
            Gap=options.ContactSheetGap,WhiteBackground=options.ContactSheetWhiteBackground},ct).ConfigureAwait(false);
        if(options.Action==ImageAction.PdfMerge){var pdfResult=await _pdfAssembly.MergeAsync(paths,options.OutputDirectory,ct).ConfigureAwait(false);return new(pdfResult.OutputPath,pdfResult.Bytes,0,0,pdfResult.Format,pdfResult.SourceBytes);}
        if(options.Action==ImageAction.PdfExtractPages){if(paths.Count!=1)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_FBC5A3502881"));var pdfResult=await _pdfAssembly.ExtractPagesAsync(paths[0],options.PdfPageSelection,options.OutputDirectory,ct).ConfigureAwait(false);return new(pdfResult.OutputPath,pdfResult.Bytes,0,0,pdfResult.Format,pdfResult.SourceBytes);}
        if(options.Action==ImageAction.ArchiveZip) {
            var r=await _archives.CreateZipAsync(paths,options.OutputDirectory,ct).ConfigureAwait(false);
            return new(r.OutputPath,r.Bytes,0,0,r.Format,r.SourceBytes);
        }
        if(options.Action==ImageAction.FileHash&&paths.Any(Directory.Exists))return await WriteFolderHashesAsync(paths,options,ct).ConfigureAwait(false);
        if(options.Action==ImageAction.FileHash)return await WriteReportAsync(paths,options,"sha256",async(path,cancellation)=>{
            await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,1048576,true);
            var hash=await SHA256.HashDataAsync(stream,cancellation).ConfigureAwait(false);return Convert.ToHexString(hash).ToLowerInvariant()+"  "+Path.GetFileName(path);
        },ct).ConfigureAwait(false);
        if(options.Action==ImageAction.ArchiveList)return await WriteReportAsync(paths,options,"contents",async(path,cancellation)=>{
            await _archives.InspectZipAsync(path,cancellation).ConfigureAwait(false);
            using var zip=ZipFile.OpenRead(path);var text=new StringBuilder(Path.GetFileName(path)+Environment.NewLine);
            foreach(var e in zip.Entries){cancellation.ThrowIfCancellationRequested();text.Append(e.FullName).Append('\t').Append(e.Length.ToString(CultureInfo.InvariantCulture)).AppendLine(" bytes");}
            return text.ToString();
        },ct).ConfigureAwait(false);
        if(paths.Count!=1)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_24ED20BA7567"));
        var source=paths[0];
        if(options.Action is ImageAction.VideoGif or ImageAction.VideoExport or ImageAction.AudioAdjust){
            var processedMedia=options.Action switch{
                ImageAction.VideoGif=>await _videoGif.CreateAsync(source,new VideoGifOptions{OutputDirectory=options.OutputDirectory,
                    StartSeconds=options.GifStartSeconds,DurationSeconds=options.GifDurationSeconds,Width=options.GifWidth,FramesPerSecond=options.GifFramesPerSecond},progress,ct).ConfigureAwait(false),
                ImageAction.VideoExport=>await _videoExport.ExportAsync(source,new VideoExportOptions{OutputDirectory=options.OutputDirectory,
                    Width=options.VideoExportWidth,NoUpscale=options.VideoExportNoUpscale,RemoveAudio=options.VideoExportRemoveAudio,VideoCrf=options.VideoCrf,
                    FramesPerSecond=options.VideoExportFramesPerSecond},progress,ct).ConfigureAwait(false),
                _=>await _audioAdjust.AdjustAsync(source,new AudioAdjustOptions{OutputDirectory=options.OutputDirectory,
                    VolumePercent=options.AudioVolumePercent,FadeInSeconds=options.AudioFadeInSeconds,FadeOutSeconds=options.AudioFadeOutSeconds},progress,ct).ConfigureAwait(false)
            };
            return new(processedMedia.OutputPath,processedMedia.Bytes,processedMedia.Width,processedMedia.Height,processedMedia.Format,processedMedia.SourceBytes);
        }
        if(options.Action==ImageAction.RemoveImageMetadata)return await _metadataCleaner.CleanAsync(source,new ImageMetadataCleanOptions{OutputDirectory=options.OutputDirectory},ct).ConfigureAwait(false);
        if(options.Action==ImageAction.OcrText){
            var metadata=await InspectAsync(source,ct).ConfigureAwait(false);
            var ocrOptions=new OcrOptions{Language=options.OcrLanguages,Dpi=options.OcrDpi,OutputDirectory=options.OutputDirectory};
            var r=metadata.Kind switch{
                ToolkitKind.Image=>await _ocr.ExtractImageAsync(source,ocrOptions,ct).ConfigureAwait(false),
                ToolkitKind.Pdf=>await _ocr.ExtractPdfAsync(source,ocrOptions,ct).ConfigureAwait(false),
                _=>throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_76213936C06B"))
            };
            return new(r.OutputPath,r.Bytes,0,0,r.Format,r.SourceBytes);
        }
        if(options.Action==ImageAction.ImageTransform)return await _images.TransformAsync(source,new ImageTransformOptions{
            OutputDirectory=options.OutputDirectory,RotationDegrees=options.RotationDegrees,FlipHorizontal=options.FlipHorizontal,FlipVertical=options.FlipVertical
        },ct).ConfigureAwait(false);
        if(options.Action is ImageAction.Jpg or ImageAction.WebP or ImageAction.Avif or ImageAction.Png or ImageAction.Bmp or ImageAction.Tiff or ImageAction.Gif or ImageAction.Ico or ImageAction.Svg or ImageAction.Crop or ImageAction.Resize or ImageAction.Compress)
            return await _images.ExecuteAsync(source,options,progress,ct).ConfigureAwait(false);
        if(options.Action==ImageAction.ExtractZip) {
            var r=await _archives.ExtractZipAsync(source,options.OutputDirectory,ct).ConfigureAwait(false);
            return new(r.OutputPath,r.Bytes,0,0,r.Format,r.SourceBytes);
        }
        if(options.Action is ImageAction.PdfPng or ImageAction.PdfJpg) {
            var op=options.Action==ImageAction.PdfPng?PdfOperation.PngPages:PdfOperation.JpgPages;
            var r=await _documents.ExecutePdfAsync(source,op,new PdfOptions{OutputDirectory=options.OutputDirectory,Dpi=options.PdfDpi},ct).ConfigureAwait(false);
            return new(r.OutputPath,r.Bytes,0,0,r.Format,r.SourceBytes);
        }
        var mediaOp=options.Action switch {
            ImageAction.VideoMp4=>MediaOperation.VideoMp4,ImageAction.VideoWebm=>MediaOperation.VideoWebm,
            ImageAction.ExtractMp3=>MediaOperation.ExtractMp3,ImageAction.VideoFrame=>MediaOperation.VideoFrame,
            ImageAction.VideoCompress=>MediaOperation.VideoCompress,ImageAction.AudioMp3=>MediaOperation.AudioMp3,
            ImageAction.AudioWav=>MediaOperation.AudioWav,ImageAction.AudioFlac=>MediaOperation.AudioFlac,
            ImageAction.AudioCompress=>MediaOperation.AudioCompress,ImageAction.VideoTrim=>MediaOperation.VideoTrim,ImageAction.AudioTrim=>MediaOperation.AudioTrim,_=>throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_7AC37CAAD5C3"))
        };
        var result=await _media.ExecuteAsync(source,mediaOp,new MediaOptions{OutputDirectory=options.OutputDirectory,VideoCrf=options.VideoCrf,AudioBitrateKbps=options.AudioBitrateKbps,TrimStartSeconds=options.TrimStartSeconds,TrimEndSeconds=options.TrimEndSeconds},progress,ct).ConfigureAwait(false);
        return new(result.OutputPath,result.Bytes,result.Width,result.Height,result.Format,result.SourceBytes);
    }
    private static async Task<ImageJobResult> WriteFolderHashesAsync(IReadOnlyList<string> paths,ImageJobOptions options,CancellationToken ct)
    {
        var plan=await Task.Run(()=>ArchiveSourcePlanner.Create(paths,ct),ct).ConfigureAwait(false);
        var first=plan.Roots[0];
        var parent=DocumentFiles.OutputDirectory(first,options.OutputDirectory);
        var temporary=Path.Combine(parent,".orbit-"+Guid.NewGuid().ToString("N")+".tmp");
        try {
            await using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true))
            await using(var writer=new StreamWriter(output,new UTF8Encoding(false))) {
                await writer.WriteLineAsync("# Arclo SHA-256".AsMemory(),ct).ConfigureAwait(false);
                var buffer=new byte[81920];
                foreach(var item in plan.Items) {
                    ct.ThrowIfCancellationRequested();
                    ArchiveSourcePlanner.Recheck(item,ct);
                    if(item.IsDirectory){await writer.WriteLineAsync(("# folder: "+item.EntryName).AsMemory(),ct).ConfigureAwait(false);continue;}
                    await using var input=new FileStream(item.Path,FileMode.Open,FileAccess.Read,FileShare.Read,81920,true);
                    ArchiveSourcePlanner.Recheck(item,ct);
                    if((File.GetAttributes(input.SafeFileHandle)&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_8E065F68436C"));
                    if(input.Length!=item.Bytes)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_01480DC9E8E5"));
                    using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long copied=0;int count;
                    while((count=await input.ReadAsync(buffer,ct).ConfigureAwait(false))>0) {
                        copied+=count;
                        if(copied>item.Bytes)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_5CDE68EDC74F"));
                        hash.AppendData(buffer.AsSpan(0,count));
                    }
                    if(copied!=item.Bytes)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_140FA7F5B10E"));
                    await writer.WriteLineAsync((Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()+"  "+item.EntryName).AsMemory(),ct).ConfigureAwait(false);
                }
            }
            ct.ThrowIfCancellationRequested();
            bool folder=plan.Items.Any(item=>item.IsDirectory&&string.Equals(item.Path,first,StringComparison.OrdinalIgnoreCase));
            var stem=folder?Path.GetFileName(first):Path.GetFileNameWithoutExtension(first);
            var target=DocumentFiles.CommitFile(temporary,parent,stem+"_sha256.txt",ct);
            return new(target,new FileInfo(target).Length,0,0,"TXT",plan.TotalBytes);
        }catch(UnauthorizedAccessException ex){throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_BAE04B8B47C6"),ex);}
        catch(IOException ex){throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_F34411D44C74"),ex);}
        finally{try{if(File.Exists(temporary))File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}}
    }
    public static bool IsTarPath(string path)=>path.EndsWith(".tar",StringComparison.OrdinalIgnoreCase)||path.EndsWith(".tar.gz",StringComparison.OrdinalIgnoreCase)||path.EndsWith(".tgz",StringComparison.OrdinalIgnoreCase);
    private static async Task<ImageJobResult> WriteReportAsync(IReadOnlyList<string> paths,ImageJobOptions options,string suffix,Func<string,CancellationToken,Task<string>> line,CancellationToken ct)
    {
        var directory=options.OutputDirectory??Path.GetDirectoryName(Path.GetFullPath(paths[0]))!;
        if(!Directory.Exists(directory))throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_F5D6DF7FEC9A"));
        var temporary=Path.Combine(directory,".orbit-"+Guid.NewGuid().ToString("N")+".tmp");
        try {
            long total=0;
            await using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true))
            await using(var writer=new StreamWriter(stream,new UTF8Encoding(false)))
                foreach(var path in paths){ct.ThrowIfCancellationRequested();total+=new FileInfo(path).Length;await writer.WriteLineAsync((await line(path,ct).ConfigureAwait(false)).AsMemory(),ct).ConfigureAwait(false);}
            ct.ThrowIfCancellationRequested();
            var stem=Path.GetFileNameWithoutExtension(paths[0])+"_"+suffix;
            for(int i=0;i<10000;i++) {
                var target=Path.Combine(directory,stem+(i==0?"":$" ({i})")+".txt");
                try {File.Move(temporary,target,false);return new(target,new FileInfo(target).Length,0,0,"TXT",total);}catch(IOException)when(File.Exists(target)){ }
            }
            throw new IOException(global::Orbit.Core.Localization.L.Get("Text_4CDB5115B606"));
        } finally {try{if(File.Exists(temporary))File.Delete(temporary);}catch(IOException){} }
    }
}
