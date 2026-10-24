using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Orbit.Core.Documents;
namespace Orbit.Core.Watermarks;
public sealed class ImageWatermarkEngine
{
    public const long MaximumSourceBytes=256L*1024*1024,MaximumSourcePixels=40000000,MaximumLogoBytes=16L*1024*1024,MaximumLogoPixels=20000000,MaximumOutputBytes=256L*1024*1024;
    public const int MaximumSourceDimension=16000,MaximumLogoDimension=8192;
    private const string Prefix=".orbit-watermark-";
    private readonly ImageMagickEngine _images;private readonly TimeSpan _timeout;
    public ImageWatermarkEngine(string? imageMagickExecutable=null,TimeSpan? operationTimeout=null){_images=new(imageMagickExecutable);_timeout=operationTimeout??TimeSpan.FromMinutes(3);if(_timeout<=TimeSpan.Zero || _timeout>TimeSpan.FromHours(1))throw new ArgumentOutOfRangeException(nameof(operationTimeout));}
    public string ExecutablePath=>_images.ExecutablePath;
    public IReadOnlyList<string> GetAvailableFonts()=>new[]{"SegoeUI","Arial"}.Where(id=>File.Exists(FontPath(id))).ToArray();
    public async Task<WatermarkLogoInfo> InspectLogoAsync(string path,CancellationToken ct=default)
    {
        using var deadline=Deadline(ct);var token=deadline.Token;
        try{var normalized=Normalize(path);await using var lease=await Open(normalized,true,token);var image=await Inspect(normalized,true,token);return new(normalized,Convert.ToHexString(await SHA256.HashDataAsync(lease,token)),image);}
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_B2C8899A41DA"));}
        catch(Exception ex)when(Expected(ex)){throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_4A737FDB2FA1"),ex);}
    }
    public async Task<ImageJobResult> ApplyAsync(string path,WatermarkOptions options,IProgress<double>? progress=null,CancellationToken ct=default)=> (await Process(path,options,false,960,progress,ct)).Result!;
    public async Task<byte[]> CreatePreviewAsync(string path,WatermarkOptions options,int maxSize=960,CancellationToken ct=default)=>(await Process(path,options,true,maxSize,null,ct)).Preview!;
    private async Task<(ImageJobResult? Result,byte[]? Preview)> Process(string path,WatermarkOptions options,bool preview,int maxSize,IProgress<double>? progress,CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(options);Validate(options);if(maxSize is <1 or >2048)throw new ArgumentOutOfRangeException(nameof(maxSize));
        using var deadline=Deadline(caller);var token=deadline.Token;string? temporary=null;FileStream? logoLease=null;
        try
        {
            var sourcePath=Normalize(path);await using var sourceLease=await Open(sourcePath,false,token);var source=await Inspect(sourcePath,false,token);var native=new WatermarkNative(_images);var detail=await Details(source,native,token);ValidateDepth(source,detail);
            string? logoPath=null;ImageMetadata? logo=null;
            if(options.Mode==WatermarkMode.Logo)
            {
                logoPath=Normalize(options.LogoPath!);logoLease=await Open(logoPath,true,token);var fingerprint=Convert.ToHexString(await SHA256.HashDataAsync(logoLease,token));
                if(!fingerprint.Equals(options.LogoSha256,StringComparison.OrdinalIgnoreCase))throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_215BBBD977E5"));logo=await Inspect(logoPath,true,token);
            }
            progress?.Report(.1);string directory=preview?Path.GetTempPath():string.IsNullOrWhiteSpace(options.OutputDirectory)?Path.GetDirectoryName(sourcePath)!:Path.GetFullPath(options.OutputDirectory);
            if(!await Task.Run(()=>Directory.Exists(directory),CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3),token))throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_F5D6DF7FEC9A"));
            temporary=Path.Combine(directory,Prefix+Guid.NewGuid().ToString("N"));
            var creatingPath=temporary;var creating=Task.Run(()=>Directory.CreateDirectory(creatingPath),CancellationToken.None);
            try{await creating.WaitAsync(TimeSpan.FromSeconds(3),token).ConfigureAwait(false);}
            catch{_=creating.ContinueWith(async done=>{if(done.IsCompletedSuccessfully)await Cleanup(creatingPath).ConfigureAwait(false);else _=done.Exception;},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default).Unwrap();throw;}
            int margin=(int)Math.Min((Math.Min(source.Width,source.Height)-1)/2,Math.Round(Math.Min(source.Width,source.Height)*options.MarginPercent/100,MidpointRounding.AwayFromZero));int availableW=source.Width-margin*2,availableH=source.Height-margin*2;
            var layer=Path.Combine(temporary,"layer.png");
            if(options.Mode==WatermarkMode.Text)
            {
                var font=FontPath(options.FontFamily);if(!File.Exists(font))throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_02467367B2B5"));
                if(!WatermarkFontCoverage.Supports(font,options.Text))throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_A03A0BD6D059"));
                int nominal=Math.Max(1,(int)Math.Round(Math.Min(source.Width,source.Height)*options.RelativeSizePercent/100,MidpointRounding.AwayFromZero));int band=Math.Min(availableH,nominal*2+8);
                var args=Resources();args.AddRange(["-density","72","-background","none","-fill",options.Color.ToUpperInvariant(),"-font",font.Replace('\\','/'),"-size",$"{availableW}x{band}","-define","label:max-pointsize="+nominal,"-define","label:min-pointsize=1","label:@-","-format","%[label:pointsize]","-write","info:","-trim","+repage","-alpha","set","-channel","A","-evaluate","Multiply",F(options.OpacityPercent/100),"+channel","-strip","PNG:"+layer]);
                var text=Encoding.UTF8.GetBytes(options.Text);var raw=Encoding.UTF8.GetString(await native.RunAsync(args,token,text,outputFile:layer));
                if(!double.TryParse(raw,NumberStyles.Float,CultureInfo.InvariantCulture,out var actual) || !double.IsFinite(actual) || actual<1 || actual>nominal)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_9D806A1B2ED0"));
                var measure=Resources();measure.AddRange(["-density","72","-size","1x1","xc:none","-font",font.Replace('\\','/'),"-pointsize",F(actual),"-debug","Annotate","-annotate","+0+0","@-","null:"]);await native.RunAsync(measure,token,text);
                var metrics=Regex.Matches(native.LastError,@"Metrics: text:.*; width: ([0-9.]+); height: ([0-9.]+); ascent:").LastOrDefault();
                if(metrics is null || !double.TryParse(metrics.Groups[1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var width) || !double.TryParse(metrics.Groups[2].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var height) || width>availableW || height>band)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_3833F2C0DF67"));
            }
            else
            {
                var logoDetail=await Details(logo!,native,token);ValidateDepth(logo!,logoDetail);var args=Resources();args.AddRange(["-define","filename:literal=true",logo!.Format+":"+logoPath,"-auto-orient"]);Color(args,logoDetail.Profiles);
                int width=Math.Min(availableW,Math.Max(1,(int)Math.Round(source.Width*options.LogoWidthPercent/100,MidpointRounding.AwayFromZero)));
                args.AddRange(["-filter","Lanczos","-resize",$"{width}x{availableH}","+repage","-alpha","set","-channel","A","-evaluate","Multiply",F(options.OpacityPercent/100),"+channel","-strip","PNG:"+layer]);await native.RunAsync(args,token,outputFile:layer);
            }
            var layerInfo=await _images.InspectAsync(layer,token);if(layerInfo.Width>availableW || layerInfo.Height>availableH)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_779E1D8C47E0"));
            progress?.Report(.45);var extension=Path.GetExtension(sourcePath);var result=Path.Combine(temporary,"result"+(preview?".png":extension));var composite=Resources();composite.AddRange(["-define","filename:literal=true",source.Format+":"+sourcePath,"-auto-orient"]);Color(composite,detail.Profiles);
            var updatedXmp=await NormalizeXmp(source,native,temporary,token);if(updatedXmp is not null)composite.AddRange(["-profile",updatedXmp]);
            if(source.Format=="TIFF")await PreserveTiffProperties(source,native,composite,token);
            int offsetX=options.Position is WatermarkPosition.Top or WatermarkPosition.Center or WatermarkPosition.Bottom?0:margin;int offsetY=options.Position is WatermarkPosition.Left or WatermarkPosition.Center or WatermarkPosition.Right?0:margin;
            composite.AddRange(["PNG:"+layer,"-gravity",Gravity(options.Position),"-geometry","+"+offsetX+"+"+offsetY,"-compose","Over","-composite","-orient","TopLeft"]);
            if(detail.Profiles.Contains("exif",StringComparison.OrdinalIgnoreCase))composite.AddRange(["-set","exif:Orientation","1","-set","exif:PixelXDimension",source.Width.ToString(CultureInfo.InvariantCulture),"-set","exif:PixelYDimension",source.Height.ToString(CultureInfo.InvariantCulture)]);
            var format=preview?"PNG":source.Format;Encode(composite,format,detail.Depth);composite.Add(format+":"+result);await native.RunAsync(composite,token,outputFile:result);
            var output=await _images.InspectAsync(result,token);if(output.Format!=format || output.Width!=source.Width || output.Height!=source.Height || output.Bytes is <=0 or >MaximumOutputBytes)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_691FB46AFDC5"));
            var outputDetails=await Details(output,native,token);if(outputDetails.Orientation is not ("TopLeft" or "Undefined"))throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_524796A71872"));
            progress?.Report(.9);token.ThrowIfCancellationRequested();
            if(preview){var args=Resources();args.AddRange(["-define","filename:literal=true","PNG:"+result,"-thumbnail",$"{maxSize}x{maxSize}>","-depth","8","PNG:-"]);return(null,await native.RunAsync(args,token,outputLimit:33554432));}
            var published=Commit(result,directory,sourcePath,extension,token);progress?.Report(1);return(new ImageJobResult(published,output.Bytes,output.Width,output.Height,output.Format,source.Bytes),null);
        }
        catch(OperationCanceledException)when(!caller.IsCancellationRequested){throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_F89D5E7CBB1E"));}
        catch(Exception ex)when(Expected(ex)){throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_1C08AA4BEBA8"),ex);}
        finally{if(logoLease is not null)await logoLease.DisposeAsync();await Cleanup(temporary);}
    }
    private async Task<ImageMetadata> Inspect(string path,bool logo,CancellationToken token)
    {var image=await _images.InspectAsync(path,token);if(image.Frames!=1 || image.Width>(logo?MaximumLogoDimension:MaximumSourceDimension) || image.Height>(logo?MaximumLogoDimension:MaximumSourceDimension) || (long)image.Width*image.Height>(logo?MaximumLogoPixels:MaximumSourcePixels))throw new ImageProcessingException(logo?global::Orbit.Core.Localization.L.Get("Text_1D6BFA338842"):global::Orbit.Core.Localization.L.Get("Text_4D2CC759D8F3"));if(!Match(image.Format,Path.GetExtension(path)))throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_639AEEDC5B36"));return image;}
    private static void Validate(WatermarkOptions o)
    {
        if(!Enum.IsDefined(o.Mode)||!Enum.IsDefined(o.Position))throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_A6C87520E839"));
        if(!Finite(o.OpacityPercent,1,100)||!Finite(o.MarginPercent,0,20))throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_9243BBE113A3"));
        if(o.Mode==WatermarkMode.Text){if(string.IsNullOrWhiteSpace(o.Text)||o.Text.Length>200)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_AD973A78D4AF"));for(int i=0;i<o.Text.Length;i++){char c=o.Text[i];if(char.IsControl(c)||c is '\u2028' or '\u2029'||char.IsLowSurrogate(c)||char.IsHighSurrogate(c)&&(i+1>=o.Text.Length||!char.IsLowSurrogate(o.Text[++i])))throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_5872C94967B6"));}if(o.FontFamily is not ("SegoeUI" or "Arial")||!Regex.IsMatch(o.Color??"",@"\A#[0-9a-fA-F]{6}\z")||!Finite(o.RelativeSizePercent,1,15))throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_E8E2C0D9E11A"));}
        else if(string.IsNullOrWhiteSpace(o.LogoPath)||o.LogoSha256 is not {Length:64}||!o.LogoSha256.All(char.IsAsciiHexDigit)||!Finite(o.LogoWidthPercent,1,100))throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_03E230D5678E"));
    }
    private static bool Finite(double x,double a,double b)=>double.IsFinite(x)&&x>=a&&x<=b;
    private static string FontPath(string id)=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts),id switch{"SegoeUI"=>"segoeui.ttf","Arial"=>"arial.ttf",_=>throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_FB4B9387DF47"))});
    private static List<string> Resources()=>["-limit","thread","2","-limit","memory","512MiB","-limit","map","0","-limit","disk","0","-define","filename:literal=true"];
    private void Color(List<string> args,string profiles){var srgb=Path.Combine(Path.GetDirectoryName(ExecutablePath)!,"sRGB.icc");if(File.Exists(srgb))args.AddRange(["-profile",srgb]);else if(profiles.Split([',',' '],StringSplitOptions.RemoveEmptyEntries).Any(x=>x is "icc" or "icm"))throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_2AFBB3160B6B"));args.AddRange(["-colorspace","sRGB"]);}
    private static void Encode(List<string> a,string format,int depth){switch(format){case"JPEG":a.AddRange(["-quality","95","-sampling-factor","4:4:4"]);break;case"WEBP":a.AddRange(["-quality","100","-define","webp:lossless=true"]);break;case"AVIF":a.AddRange(["-quality","100","-define","heic:chroma=444","-define","heic:speed=6"]);break;case"PNG":a.AddRange(["-depth",depth>8?"16":"8","-define","png:preserve-iCCP=true","-define","png:compression-level=9"]);break;case"TIFF":a.AddRange(["-depth",depth>8?"16":"8","-compress","Zip"]);break;}}
    private async Task<(string Profiles,int Depth,string Orientation)> Details(ImageMetadata image,WatermarkNative native,CancellationToken token){var args=new List<string>{"identify"};args.AddRange(Resources());args.AddRange(["-define","filename:literal=true","-format","%[profiles]|%z|%[orientation]",image.Format+":"+image.Path]);var fields=Encoding.UTF8.GetString(await native.RunAsync(args,token)).Trim().Split('|');if(fields.Length!=3||!int.TryParse(fields[1],out var depth))throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_4D5EA7AFA93F"));return(fields[0],depth,fields[2]);}
    private static void ValidateDepth(ImageMetadata image,(string Profiles,int Depth,string Orientation) d){if(d.Depth is <1 or >16 && image.Format is "PNG" or "TIFF")throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_8211253B7B6B"));}
    private static async Task<string?> NormalizeXmp(ImageMetadata source,WatermarkNative native,string temp,CancellationToken token)
    {
        var details=new List<string>{"identify"};details.AddRange(Resources());details.AddRange(["-ping","-define","filename:literal=true","-format","%[profiles]",source.Format+":"+source.Path]);var profiles=Encoding.UTF8.GetString(await native.RunAsync(details,token));if(!profiles.Contains("xmp",StringComparison.OrdinalIgnoreCase))return null;
        var args=Resources();args.AddRange(["-define","filename:literal=true",source.Format+":"+source.Path,"xmp:-"]);var bytes=await native.RunAsync(args,token,outputLimit:2097152);
        using var input=new MemoryStream(bytes);using var reader=XmlReader.Create(input,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=2097152});var doc=XDocument.Load(reader,LoadOptions.PreserveWhitespace);
        string Value(XName name)=>name.LocalName is "Orientation"?"1":name.LocalName is "PixelXDimension" or "ImageWidth"?source.Width.ToString(CultureInfo.InvariantCulture):source.Height.ToString(CultureInfo.InvariantCulture);
        bool Target(XName name)=>name.NamespaceName is "http://ns.adobe.com/tiff/1.0/" or "http://ns.adobe.com/exif/1.0/" && name.LocalName is "Orientation" or "PixelXDimension" or "PixelYDimension" or "ImageWidth" or "ImageLength";
        foreach(var node in doc.Descendants()){foreach(var a in node.Attributes().Where(a=>Target(a.Name)))a.Value=Value(a.Name);if(Target(node.Name))node.Value=Value(node.Name);}
        var path=Path.Combine(temp,"normalized.xmp");await File.WriteAllTextAsync(path,doc.ToString(SaveOptions.DisableFormatting),new UTF8Encoding(false),token);return path;
    }
    private static async Task PreserveTiffProperties(ImageMetadata source,WatermarkNative native,List<string> args,CancellationToken token)
    {
        var query=new List<string>{"identify"};query.AddRange(Resources());query.AddRange(["-format","%[tiff:*]","TIFF:"+source.Path]);var properties=Encoding.UTF8.GetString(await native.RunAsync(query,token));
        // TIFF reads these as image properties but writes their artifact counterparts.
        // Constant substitutions are interpreted once; embedded metadata syntax stays literal.
        foreach(var key in new[]{"artist","document","hostcomputer","timestamp","make","model","software","copyright"})
            if(properties.Split('\n').Any(line=>line.StartsWith("tiff:"+key+"=",StringComparison.Ordinal)))args.AddRange(["-set","artifact:tiff:"+key,"%[tiff:"+key+"]"]);
    }
    private static string Gravity(WatermarkPosition p)=>p switch{WatermarkPosition.TopLeft=>"NorthWest",WatermarkPosition.Top=>"North",WatermarkPosition.TopRight=>"NorthEast",WatermarkPosition.Left=>"West",WatermarkPosition.Center=>"Center",WatermarkPosition.Right=>"East",WatermarkPosition.BottomLeft=>"SouthWest",WatermarkPosition.Bottom=>"South",_=>"SouthEast"};
    private static string F(double value)=>value.ToString("0.########",CultureInfo.InvariantCulture);
    private static bool Match(string f,string e)=>f switch{"JPEG"=>e.Equals(".jpg",StringComparison.OrdinalIgnoreCase)||e.Equals(".jpeg",StringComparison.OrdinalIgnoreCase),"TIFF"=>e.Equals(".tif",StringComparison.OrdinalIgnoreCase)||e.Equals(".tiff",StringComparison.OrdinalIgnoreCase),_=>e.Equals("."+f,StringComparison.OrdinalIgnoreCase)};
    private static string Normalize(string path){if(string.IsNullOrWhiteSpace(path))throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_633AD966C74E"));var full=Path.GetFullPath(path);if(full.StartsWith(@"\\?\")||full.StartsWith(@"\\.\")||full.IndexOf(':',full.Length>1&&full[1]==':'?2:0)>=0||!new[]{".png",".jpg",".jpeg",".webp",".avif",".bmp",".tif",".tiff"}.Contains(Path.GetExtension(full),StringComparer.OrdinalIgnoreCase))throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_855737496B2B"));return full;}
    private static async Task<FileStream> Open(string path,bool logo,CancellationToken token){var task=Task.Run(()=>{ArchiveSourcePlanner.SafeAttributes(path,token);var s=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous);try{ArchiveSourcePlanner.SafeAttributes(path,token);if((File.GetAttributes(s.SafeFileHandle)&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0||s.Length<=0||s.Length>(logo?MaximumLogoBytes:MaximumSourceBytes))throw new ImageProcessingException(logo?global::Orbit.Core.Localization.L.Get("Text_2348A1BCB37C"):global::Orbit.Core.Localization.L.Get("Text_F55A9AAA2F1B"));WatermarkAnimationGuard.Check(s);return s;}catch{s.Dispose();throw;}},CancellationToken.None);try{return await task.WaitAsync(TimeSpan.FromSeconds(3),token);}catch{_=task.ContinueWith(t=>{if(t.IsCompletedSuccessfully)t.Result.Dispose();else _=t.Exception;},TaskScheduler.Default);throw;}}
    private static string Commit(string result,string directory,string source,string extension,CancellationToken token){var stem=Path.GetFileNameWithoutExtension(source);if(stem.Length>180){stem=stem[..180];if(char.IsHighSurrogate(stem[^1]))stem=stem[..^1];}for(int n=1;n<=10000;n++){token.ThrowIfCancellationRequested();var target=Path.Combine(directory,stem+"_watermarked"+(n==1?"":" ("+n+")")+extension);try{File.Move(result,target,false);return target;}catch(IOException)when(File.Exists(target)||Directory.Exists(target)){}}throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_7D0DAD0A78C1"));}
    private static async Task Cleanup(string? temp){if(temp is null||!Path.GetFileName(temp).StartsWith(Prefix,StringComparison.Ordinal))return;var task=Task.Run(async()=>{for(int i=0;i<11;i++){try{if(Directory.Exists(temp)&&(File.GetAttributes(temp)&FileAttributes.ReparsePoint)==0)Directory.Delete(temp,true);return;}catch(Exception e)when(e is IOException or UnauthorizedAccessException){}await Task.Delay(Math.Min(300,25<<Math.Min(i,4)));}});try{await task.WaitAsync(TimeSpan.FromSeconds(3));}catch(TimeoutException){_=task.ContinueWith(t=>_=t.Exception,TaskContinuationOptions.OnlyOnFaulted);}}
    private CancellationTokenSource Deadline(CancellationToken token){var cts=CancellationTokenSource.CreateLinkedTokenSource(token);cts.CancelAfter(_timeout);return cts;}
    private static bool Expected(Exception e)=>e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or TimeoutException or DocumentProcessingException or XmlException or OverflowException;
}
