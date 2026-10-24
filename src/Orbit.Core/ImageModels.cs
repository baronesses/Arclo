namespace Orbit.Core;

public enum ImageAction
{
    Jpg, WebP, Avif, Crop, Resize, Compress,
    VideoMp4, VideoWebm, ExtractMp3, VideoFrame, VideoCompress,
    AudioMp3, AudioWav, AudioFlac, AudioCompress,
    PdfPng, PdfJpg, PdfText, ArchiveZip, ExtractZip, ArchiveList, FileHash, FileInfo,
    PdfMerge, PdfExtractPages, Rename, MoreTools,
    OcrText, VideoTrim, AudioTrim, ImageTransform,
    ImagesToPdf, ContactSheet, RemoveImageMetadata,
    VideoGif, VideoExport, AudioAdjust,
    PdfRotate, PdfSplit, ZipExtractSelected,
    AudioNormalize, ImageChain,
    PdfEmbeddedImages, PdfOptimize,
    AudioExport, VideoFrames,
    AudioTrackExtract, AudioCover,
    ArchiveTar, ExtractTar, TarList,
    AudioTags,
    Archive7z, Extract7z, SevenZipList,
    SevenZipExtractSelected, TarExtractSelected,
    ImageWatermark,
    DownloadAudio, DownloadVideo,
    // Append new actions so persisted numeric values from earlier versions stay stable.
    Png, Bmp, Tiff, Gif, Ico, Svg
}

public sealed record ImageJobOptions
{
    public ImageAction Action { get; init; }
    public int Quality { get; init; } = 85;
    public int? Width { get; init; }
    public int? Height { get; init; }
    public bool MaintainAspectRatio { get; init; } = true;
    public bool NoUpscale { get; init; } = true;
    public int CropX { get; init; }
    public int CropY { get; init; }
    public int? CropWidth { get; init; }
    public int? CropHeight { get; init; }
    public string? OutputDirectory { get; init; }
    /// <summary>For Compress: jpg/jpeg, webp, avif, png. Null preserves the source format.</summary>
    public string? CompressionFormat { get; init; }
    public int VideoCrf { get; init; } = 24;
    public int AudioBitrateKbps { get; init; } = 192;
    public int PdfDpi { get; init; } = 150;
    public string PdfPageSelection { get; init; } = "1";
    public string OcrLanguages { get; init; } = "rus+eng";
    public int OcrDpi { get; init; } = 200;
    public double TrimStartSeconds { get; init; }
    public double? TrimEndSeconds { get; init; }
    public int RotationDegrees { get; init; }
    public bool FlipHorizontal { get; init; }
    public bool FlipVertical { get; init; }
    public string ImagePdfPageSize { get; init; } = "A4";
    public double ImagePdfMarginMillimeters { get; init; } = 10;
    public int ContactSheetColumns { get; init; } = 3;
    public int ContactSheetThumbnailSize { get; init; } = 320;
    public int ContactSheetGap { get; init; } = 12;
    public bool ContactSheetWhiteBackground { get; init; } = true;
    public double GifStartSeconds { get; init; }
    public double GifDurationSeconds { get; init; } = 3;
    public int GifWidth { get; init; } = 480;
    public int GifFramesPerSecond { get; init; } = 12;
    public int? VideoExportWidth { get; init; }
    public bool VideoExportNoUpscale { get; init; } = true;
    public bool VideoExportRemoveAudio { get; init; }
    public double AudioVolumePercent { get; init; } = 100;
    public double AudioFadeInSeconds { get; init; }
    public double AudioFadeOutSeconds { get; init; }
    public int PdfRotationDegrees { get; init; } = 90;
    public int PdfSplitPagesPerFile { get; init; } = 1;
    public IReadOnlyList<string> ZipSelectedEntries { get; init; } = Array.Empty<string>();
    public string? ZipSourceSha256 { get; init; }
    public double NormalizeTargetLufs { get; init; } = -16;
    public double NormalizeTruePeakDb { get; init; } = -1;
    public double NormalizeLoudnessRange { get; init; } = 11;
    public IReadOnlyList<ImageJobOptions> ChainSteps { get; init; } = Array.Empty<ImageJobOptions>();
    public bool PdfTextPreserveLayout { get; init; } = true;
    public bool PdfOptimizeLinearize { get; init; }
    public string AudioExportFormat { get; init; } = "M4A";
    public int AudioExportBitrateKbps { get; init; } = 192;
    public int? VideoExportFramesPerSecond { get; init; }
    public double FramesStartSeconds { get; init; }
    public double FramesDurationSeconds { get; init; } = 3;
    public int FramesPerSecond { get; init; } = 1;
    public int FramesWidth { get; init; } = 1280;
    public double? ResizePercent { get; init; }
    public string ResizeFilter { get; init; } = "Auto";
    public string JpegBackgroundColor { get; init; } = "#FFFFFF";
    public int? AudioTrackStreamIndex { get; init; }
    public string? AudioTrackSourceSha256 { get; init; }
    public string AudioTrackFormat { get; init; } = "MP3";
    public int AudioTrackBitrateKbps { get; init; } = 192;
    public string TarArchiveFormat { get; init; } = "TAR.GZ";
    public string TarCompression { get; init; } = "Optimal";
    public bool AudioTagSetTitle { get; init; }
    public string AudioTagTitle { get; init; } = "";
    public bool AudioTagSetArtist { get; init; }
    public string AudioTagArtist { get; init; } = "";
    public bool AudioTagSetAlbum { get; init; }
    public string AudioTagAlbum { get; init; } = "";
    public int AudioExportVorbisQuality { get; init; } = 5;
    public string SevenZipCompression { get; init; } = "Optimal";
    public bool SevenZipPasswordProtected { get; init; }
    public bool SevenZipEncryptHeaders { get; init; }
    public IReadOnlyList<string> ArchiveSelectedEntries { get; init; } = Array.Empty<string>();
    public string? ArchiveSourceSha256 { get; init; }
    public int ArchiveSelectedCount { get; init; }
    public string WatermarkMode { get; init; } = "Text";
    public string WatermarkText { get; init; } = "";
    public string WatermarkFont { get; init; } = "SegoeUI";
    public string WatermarkColor { get; init; } = "#FFFFFF";
    public int WatermarkFontSizePercent { get; init; } = 5;
    public int WatermarkOpacityPercent { get; init; } = 50;
    public string WatermarkPosition { get; init; } = "BottomRight";
    public int WatermarkMarginPercent { get; init; } = 3;
    public string? WatermarkLogoPath { get; init; }
    public string? WatermarkLogoSha256 { get; init; }
    public int WatermarkLogoWidthPercent { get; init; } = 20;
}

/// <summary>Dimensions are displayed dimensions after applying EXIF orientation.</summary>
public sealed record ImageMetadata(string Path, string Format, int Width, int Height, int Frames, long Bytes);

public sealed record ImageJobResult(string OutputPath, long Bytes, int Width, int Height, string Format, long SourceBytes)
{
    /// <summary>Can be negative when the selected conversion produces a larger file.</summary>
    public double SavedPercent => SourceBytes > 0 ? (1d - (double)Bytes / SourceBytes) * 100d : 0;
}

public sealed class ImageProcessingException : Exception
{
    public ImageProcessingException(string message) : base(message) { }
    public ImageProcessingException(string message, Exception innerException) : base(message, innerException) { }
}
