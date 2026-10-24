using System.Diagnostics;
using System.Text;
namespace Orbit.Core.Watermarks;
internal sealed class WatermarkNative(ImageMagickEngine images)
{
    internal async Task<byte[]> RunAsync(IReadOnlyList<string> args,CancellationToken token,byte[]? input=null,int outputLimit=1048576,string? outputFile=null)
    {
        token.ThrowIfCancellationRequested();string exe=images.ExecutablePath,home=Path.GetDirectoryName(exe)!;
        var info=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(Directory.Exists(Path.Combine(home,"modules","coders"))){info.Environment["MAGICK_HOME"]=home;info.Environment["MAGICK_CONFIGURE_PATH"]=home;info.Environment["MAGICK_CODER_MODULE_PATH"]=Path.Combine(home,"modules","coders");info.Environment["MAGICK_FILTER_MODULE_PATH"]=Path.Combine(home,"modules","filters");}info.Environment["LC_ALL"]="C";
        foreach(var arg in args)info.ArgumentList.Add(arg);
        var pending=Task.Run(()=>{var p=new Process{StartInfo=info};try{if(!p.Start())throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_3F6A4C5351D1"));return p;}catch{p.Dispose();throw;}},CancellationToken.None);
        Process process;
        try{process=await pending.WaitAsync(TimeSpan.FromSeconds(5),token).ConfigureAwait(false);}catch{_=pending.ContinueWith(async t=>{if(t.IsCompletedSuccessfully){await Stop(t.Result);t.Result.Dispose();}else _=t.Exception;},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default).Unwrap();throw;}
        using(process)
        {
            var stdout=Read(process.StandardOutput.BaseStream,outputLimit,token);var stderr=Read(process.StandardError.BaseStream,262144,token);
            var sending=Task.Run(async()=>{try{if(input is not null)await process.StandardInput.BaseStream.WriteAsync(input,token).ConfigureAwait(false);}finally{process.StandardInput.Close();}},CancellationToken.None);
            var waiting=process.WaitForExitAsync(token);var tasks=new Task[]{stdout,stderr,sending,waiting};
            try
            {
                while(tasks.Any(t=>!t.IsCompleted))
                {
                    token.ThrowIfCancellationRequested();var fault=tasks.FirstOrDefault(t=>t.IsFaulted || t.IsCanceled);if(fault is not null)await fault.ConfigureAwait(false);
                    if(outputFile is not null && File.Exists(outputFile) && new FileInfo(outputFile).Length>ImageWatermarkEngine.MaximumOutputBytes)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_249320E8CE4D"));
                    await Task.WhenAny(Task.WhenAll(tasks),Task.Delay(30,token)).ConfigureAwait(false);
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);var error=Encoding.UTF8.GetString(await stderr.ConfigureAwait(false));
                if(outputFile is not null && (!File.Exists(outputFile) || new FileInfo(outputFile).Length is <=0 or >ImageWatermarkEngine.MaximumOutputBytes))throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_39A31165AB63"));
                var fontWarning=error.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries).Any(line=>
                    !line.Contains("Metrics: text:",StringComparison.Ordinal) && line.Contains(" @ warning/",StringComparison.Ordinal)
                    && (line.Contains("font",StringComparison.OrdinalIgnoreCase)||line.Contains("glyph",StringComparison.OrdinalIgnoreCase)));
                if(process.ExitCode!=0 || fontWarning)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_B274CFAFB5C3"));
                LastError=error;return await stdout.ConfigureAwait(false);
            }
            finally
            {
                await Stop(process).ConfigureAwait(false);foreach(var t in tasks)_=t.ContinueWith(f=>_=f.Exception,CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            }
        }
    }
    internal string LastError{get;private set;}="";
    private static async Task<byte[]> Read(Stream source,int maximum,CancellationToken token){using var result=new MemoryStream();var buffer=new byte[16384];int n;while((n=await source.ReadAsync(buffer,token).ConfigureAwait(false))>0){if(result.Length+n>maximum)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_3756BB21874D"));result.Write(buffer,0,n);}return result.ToArray();}
    private static async Task Stop(Process p){try{if(!p.HasExited)p.Kill(true);}catch{}try{await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);}catch{}}
}
