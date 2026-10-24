using System;
using System.IO;
using System.Linq;
using Orbit.Core;
using Orbit.Core.Downloads;

namespace Orbit.App.History;

internal static partial class HistoryValidation
{
    private static bool TryDownload(HistoryEntry entry,bool startup,out HistoryEntry normalized)
    {
        normalized=null!;
        if(entry.Id==Guid.Empty||!Enum.IsDefined(entry.Status)||entry.CreatedUtc.Year is <2000 or >9998
            ||entry.SourcePaths is null||entry.SourcePaths.Count!=0||entry.Download is not{} request||entry.Options is null)return false;
        if(entry.FinishedUtc is{} finish&&(finish.Year is <2000 or >9998||finish<entry.CreatedUtc))return false;
        // History is passive metadata. Never inspect a URL, file or folder while loading it.
        if(request.Url is null||request.Url.Length>8192||request.Url.Any(char.IsControl)
            ||!Uri.TryCreate(request.Url,UriKind.Absolute,out var uri)||uri.Scheme is not("http" or "https")
            ||string.IsNullOrEmpty(uri.Host)||uri.UserInfo.Length!=0||uri.AbsoluteUri.Length>8192||!Enum.IsDefined(request.Mode)
            ||!TryPath(request.OutputDirectory,out var directory))return false;
        var action=request.Mode==DownloadMode.AudioMp3?ImageAction.DownloadAudio:ImageAction.DownloadVideo;
        if(entry.Options.Action!=action)return false;
        var format=request.Mode==DownloadMode.AudioMp3?"MP3":"MP4";
        ImageJobResult? result=null;
        if(entry.Result is{} value){
            if(!TryPath(value.OutputPath,out var output)||!string.Equals(Path.GetDirectoryName(output),directory,StringComparison.OrdinalIgnoreCase)
                ||!string.Equals(Path.GetExtension(output),"."+format,StringComparison.OrdinalIgnoreCase)||value.Bytes<=0
                ||value.SourceBytes!=0||value.Width!=0||value.Height!=0||value.Format!=format)return false;
            result=value with{OutputPath=output};
        }
        if(entry.Status==HistoryStatus.Succeeded&&result is null)return false;
        var status=startup&&entry.Status is HistoryStatus.Queued or HistoryStatus.Running?HistoryStatus.Interrupted:entry.Status;
        DateTimeOffset? finished=status is HistoryStatus.Queued or HistoryStatus.Running?null:entry.FinishedUtc?.ToUniversalTime()??entry.CreatedUtc.ToUniversalTime();
        normalized=entry with{SourcePaths=Array.Empty<string>(),Options=new ImageJobOptions{Action=action,OutputDirectory=directory},
            Download=request with{Url=uri.AbsoluteUri,OutputDirectory=directory},Result=result,Status=status,
            CreatedUtc=entry.CreatedUtc.ToUniversalTime(),FinishedUtc=finished,
            ErrorCaption=entry.ErrorCaption is{} caption?new string(caption.Where(c=>!char.IsControl(c)||c is '\n' or '\r' or '\t').Take(2048).ToArray()):null};
        return true;
    }
}
