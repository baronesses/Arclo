using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Orbit.Core.ContactSheets;

/// <summary>Creates a lossless PNG grid from validated, auto-oriented image previews without modifying sources.</summary>
public sealed class ContactSheetEngine
{
    public const int MaximumImages = 100;
    public const long MaximumSourceBytes = 512L * 1024 * 1024;
    public const long MaximumSingleSourceBytes = 256L * 1024 * 1024;
    public const long MaximumSourcePixels = 100_000_000;
    public const int MaximumSourceSide = 32768;
    public const long MaximumPixels = 40_000_000;
    public const int MaximumSide = 16000;
    private readonly ImageMagickEngine _images;
    private readonly TimeSpan _operationTimeout;
    // The source-linked smoke observes/cancels a real native compose process, never a fake processor.
    internal Action<int>? ProcessStarted { get; set; }

    public ContactSheetEngine(string? imageMagickExecutable = null)
        : this(imageMagickExecutable, TimeSpan.FromMinutes(5)) { }

    public ContactSheetEngine(string? imageMagickExecutable, TimeSpan operationTimeout)
    {
        if (operationTimeout <= TimeSpan.Zero || operationTimeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        _images = new ImageMagickEngine(imageMagickExecutable);
        _operationTimeout = operationTimeout;
    }

    public Task<ImageJobResult> CreateAsync(IReadOnlyList<string> paths, ContactSheetOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (paths.Count is < 1 or > MaximumImages) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Format("Text_CE9EC9269DB2",(object?)(MaximumImages)));
        var snapshot = paths.ToArray();
        return Task.Run(() => CreateCoreAsync(snapshot, options, cancellationToken), cancellationToken);
    }

    private async Task<ImageJobResult> CreateCoreAsync(string[] paths, ContactSheetOptions options, CancellationToken cancellationToken)
    {
        var (width,height) = ValidateGrid(paths.Length,options);
        var handles = new List<FileStream>();
        string? temporary = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_operationTimeout);
        var ct = deadline.Token;
        try
        {
            long sourceBytes = 0;
            var sources = new List<ImageMetadata>();
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                var fullPath = FullPath(path);
                // Keep each source readable but deny writes/deletion until its result has been committed.
                var handle = new FileStream(fullPath,FileMode.Open,FileAccess.Read,FileShare.Read,1);
                handles.Add(handle);
                if(handle.Length>MaximumSingleSourceBytes)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_9AE3CFE89A00"));
                sourceBytes = checked(sourceBytes + handle.Length);
                if (sourceBytes > MaximumSourceBytes) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_789575A30865"));
                var source=await _images.InspectAsync(fullPath,ct).ConfigureAwait(false);
                if(source.Width>MaximumSourceSide||source.Height>MaximumSourceSide||(long)source.Width*source.Height>MaximumSourcePixels)
                    throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_EFFC624605DA"));
                sources.Add(source);
            }
            ct.ThrowIfCancellationRequested();
            var directory = string.IsNullOrWhiteSpace(options.OutputDirectory) ? Path.GetDirectoryName(sources[0].Path)! : FullPath(options.OutputDirectory);
            if (!Directory.Exists(directory)) throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_45C406D632F2"));
            temporary = Path.Combine(directory,".orbit-contact-sheet-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            for (int index=0;index<sources.Count;index++)
            {
                ct.ThrowIfCancellationRequested();
                var preview=await _images.CreatePreviewAsync(sources[index].Path,options.ThumbnailSize,ct).ConfigureAwait(false);
                await File.WriteAllBytesAsync(Path.Combine(temporary,TileName(index)),preview,ct).ConfigureAwait(false);
            }
            var arguments = new List<string>{"-limit","thread","2","-limit","memory","2GiB","-limit","map","0","-limit","disk","0",
                "-define","filename:literal=true","-size",$"{width}x{height}",options.WhiteBackground?"xc:white":"xc:none"};
            var cell=options.ThumbnailSize.ToString(CultureInfo.InvariantCulture)+"x"+options.ThumbnailSize.ToString(CultureInfo.InvariantCulture);
            for (int index=0;index<sources.Count;index++)
            {
                int x=options.Gap+(index%options.Columns)*(options.ThumbnailSize+options.Gap);
                int y=options.Gap+(index/options.Columns)*(options.ThumbnailSize+options.Gap);
                // Only generated ASCII leaf names reach the native argv; user names are never evaluated as annotations.
                arguments.AddRange(["(","PNG:"+TileName(index),"+repage","-resize",cell,"-background","none","-gravity","center","-extent",cell,"+repage",
                    ")","-gravity","NorthWest","-geometry",FormattableString.Invariant($"+{x}+{y}"),"-compose","Over","-composite"]);
            }
            arguments.AddRange(["-define","png:color-type=6","-define","png:compression-level=9","PNG:result.png"]);
            await ComposeAsync(arguments,temporary,ct).ConfigureAwait(false);
            var resultPath=Path.Combine(temporary,"result.png");
            var result=await _images.InspectAsync(resultPath,ct).ConfigureAwait(false);
            if(result.Format!="PNG"||result.Width!=width||result.Height!=height||result.Bytes<=0)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_3F156B8B79C6"));
            ct.ThrowIfCancellationRequested();
            var finalPath=CommitUnique(resultPath,directory,sources[0].Path,ct);
            return new(finalPath,result.Bytes,width,height,"PNG",sourceBytes);
        }
        catch(OperationCanceledException)when(!cancellationToken.IsCancellationRequested)
        {throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_939E0E4B32B2"));}
        catch(UnauthorizedAccessException ex){throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_DD810D43A773"),ex);}
        catch(IOException ex){throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_60B996E32F01"),ex);}
        finally
        {
            foreach(var handle in handles)handle.Dispose();
            if(temporary is not null)try{if(Directory.Exists(temporary)&&(File.GetAttributes(temporary)&FileAttributes.ReparsePoint)==0)Directory.Delete(temporary,recursive:true);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }

    private static (int Width,int Height) ValidateGrid(int count,ContactSheetOptions options)
    {
        if(options.Columns is <1 or >6)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_78FF0FDCAC3A"));
        if(options.ThumbnailSize is not (160 or 320 or 640))throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_6DEB195CFE31"));
        if(options.Gap is <0 or >32)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_6F259372ABA8"));
        int rows=(count+options.Columns-1)/options.Columns;
        int width=options.Columns*options.ThumbnailSize+(options.Columns+1)*options.Gap;
        int height=rows*options.ThumbnailSize+(rows+1)*options.Gap;
        if(width>MaximumSide||height>MaximumSide)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_0A7FF1732778"));
        if((long)width*height>MaximumPixels)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_34A8ABF63135"));
        return(width,height);
    }
    private static string FullPath(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||path.StartsWith(@"\\.\",StringComparison.Ordinal)||path.StartsWith(@"\\?\",StringComparison.Ordinal)||path.StartsWith(@"\??\",StringComparison.Ordinal))
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_1582E05F3A84"));
        try{return Path.GetFullPath(path);}catch(Exception ex)when(ex is ArgumentException or NotSupportedException or PathTooLongException){throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_CD979D9DAA0A"),ex);}
    }
    private static string TileName(int index)=>"tile-"+index.ToString("D3",CultureInfo.InvariantCulture)+".png";
    private static string CommitUnique(string source,string directory,string first,CancellationToken ct)
    {
        var stem=Path.GetFileNameWithoutExtension(first);
        if(stem.Length>200){stem=stem[..200];if(char.IsHighSurrogate(stem[^1]))stem=stem[..^1];}stem=stem.TrimEnd(' ','.');if(stem.Length==0)stem="images";
        for(int index=0;index<10000;index++){
            ct.ThrowIfCancellationRequested();var target=Path.Combine(directory,stem+"_contact-sheet"+(index==0?"":"_"+index.ToString(CultureInfo.InvariantCulture))+".png");
            try{File.Move(source,target,overwrite:false);return target;}catch(IOException)when(File.Exists(target)||Directory.Exists(target)){}
        }throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_2B7D79BAF0AB"));
    }
    private async Task ComposeAsync(IReadOnlyList<string> arguments,string workingDirectory,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var executable=_images.ExecutablePath;var directory=Path.GetDirectoryName(executable)!;
        var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=workingDirectory,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        if(Directory.Exists(Path.Combine(directory,"modules","coders"))){info.Environment["MAGICK_HOME"]=directory;info.Environment["MAGICK_CONFIGURE_PATH"]=directory;info.Environment["MAGICK_CODER_MODULE_PATH"]=Path.Combine(directory,"modules","coders");info.Environment["MAGICK_FILTER_MODULE_PATH"]=Path.Combine(directory,"modules","filters");}
        foreach(var argument in arguments)info.ArgumentList.Add(argument);
        using var process=new Process{StartInfo=info};
        try{if(!process.Start())throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_9EADA39A1850"));}
        catch(System.ComponentModel.Win32Exception ex){throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_AEE38B4A5B2A"),ex);}
        var stdout=ReadLimitedAsync(process.StandardOutput,4096);var stderr=ReadLimitedAsync(process.StandardError,16384);
        try{
            ProcessStarted?.Invoke(process.Id);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);await stdout.ConfigureAwait(false);var error=await stderr.ConfigureAwait(false);ct.ThrowIfCancellationRequested();
            if(process.ExitCode!=0)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_DBE1892724BE")+error.Trim()[..Math.Min(error.Trim().Length,1000)]);
        }finally{
            if(!process.HasExited){try{process.Kill(entireProcessTree:true);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);}
            await Task.WhenAll(stdout,stderr).ConfigureAwait(false);
        }
    }
    private static async Task<string> ReadLimitedAsync(StreamReader reader,int limit)
    {
        var text=new StringBuilder();var buffer=new char[4096];int count;
        while((count=await reader.ReadAsync(buffer).ConfigureAwait(false))>0)if(text.Length<limit)text.Append(buffer,0,Math.Min(count,limit-text.Length));return text.ToString();
    }
}
