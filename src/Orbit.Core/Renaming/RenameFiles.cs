using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Orbit.Core.Renaming;

internal static class RenameFiles
{
    internal static readonly StringComparer PathComparer=StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> Reserved=new(StringComparer.OrdinalIgnoreCase)
        {"CON","PRN","AUX","NUL","CLOCK$","CONIN$","CONOUT$"};

    internal static string FullPath(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||path.StartsWith(@"\\?\",StringComparison.Ordinal)||path.StartsWith(@"\\.\",StringComparison.Ordinal))
            throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_82E20A0A6898"));
        return Path.GetFullPath(path);
    }
    internal static string NameKey(string leaf)=>leaf.Normalize(NormalizationForm.FormC);
    internal static void ValidateLeaf(string name)
    {
        if(string.IsNullOrWhiteSpace(name)||name.Length>255||name is "." or ".."||name.EndsWith('.')||name.EndsWith(' ')
            ||name.Any(c=>char.IsControl(c)||"<>:\"/\\|?*".Contains(c)))
            throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_253210412B6D")+name+global::Orbit.Core.Localization.L.Get("Text_66F1FCECA906"));
        var stem=NameKey(name.Split('.')[0].TrimEnd(' '));
        if(Reserved.Contains(stem)||(stem.Length==4&&(stem.StartsWith("COM",StringComparison.OrdinalIgnoreCase)||stem.StartsWith("LPT",StringComparison.OrdinalIgnoreCase))
            &&(stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3')))
            throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_143A77E705F0")+name+global::Orbit.Core.Localization.L.Get("Text_88176B31D8D0"));
        _=NameKey(name); // Reject malformed Unicode before any filesystem mutation.
    }
    internal static FileAttributes SafeAttributes(string path,bool regularFile)
    {
        var attributes=File.GetAttributes(path);
        if(regularFile&&(attributes&FileAttributes.Directory)!=0)throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_8C91116245C1"));
        string? current=path;int depth=0;
        while(!string.IsNullOrEmpty(current)) {
            if(++depth>256)throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_EBE2353BB598"));
            if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_35EA998D14B6")+current);
            current=Path.GetDirectoryName(current);
        }
        return attributes;
    }
    internal static RenameStamp ReadStamp(string path)
    {
        SafeAttributes(path,true);
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,1);
        var stamp=Stamp(stream,path);SafeAttributes(path,true);return stamp;
    }
    internal static async Task<RenameLease> LockFingerprintAsync(string path,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();SafeAttributes(path,true);
        // Keep writes denied through the transaction, while permitting our own same-directory moves.
        var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete,81920,FileOptions.Asynchronous|FileOptions.SequentialScan);
        try {
            var before=Stamp(stream,path);
            var hash=Convert.ToHexString(await SHA256.HashDataAsync(stream,ct).ConfigureAwait(false));
            var after=Stamp(stream,path);SafeAttributes(path,true);
            if(before!=after)throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_165F1F4F7D62")+path);
            return new(stream,new(before.Length,before.LastWriteTimeUtc,before.FileIdentity,hash));
        } catch {await stream.DisposeAsync().ConfigureAwait(false);throw;}
    }
    internal static bool Matches(RenameStamp stamp,RenameFingerprint fingerprint)=>stamp.Length==fingerprint.Length
        &&stamp.LastWriteTimeUtc==fingerprint.LastWriteTimeUtc&&stamp.FileIdentity==fingerprint.FileIdentity;
    internal static bool SameFingerprint(RenameFingerprint a,RenameFingerprint b)=>a==b;

    internal static void CheckTargets(IReadOnlyList<(string Source,string Target)> routes,CancellationToken ct)
    {
        var sources=new HashSet<string>(routes.Select(r=>r.Source),PathComparer);
        foreach(var group in routes.GroupBy(r=>Path.GetDirectoryName(r.Target)!,PathComparer)) {
            ct.ThrowIfCancellationRequested();SafeAttributes(group.Key,false);
            var targets=new HashSet<string>(PathComparer);
            foreach(var route in group) {
                if(!PathComparer.Equals(Path.GetDirectoryName(route.Source),group.Key))throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_BDB05787BD1D"));
                ValidateLeaf(Path.GetFileName(route.Target));
                if(!targets.Add(NameKey(Path.GetFileName(route.Target))))throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_93E706AFAA46"));
            }
            foreach(var entry in Directory.EnumerateFileSystemEntries(group.Key)) {
                ct.ThrowIfCancellationRequested();
                if(targets.Contains(NameKey(Path.GetFileName(entry)))&&!sources.Contains(Path.GetFullPath(entry)))
                    throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_DD023713F62C")+entry);
            }
        }
    }
    private static RenameStamp Stamp(FileStream stream,string path)
    {
        if(!OperatingSystem.IsWindows())return new(stream.Length,File.GetLastWriteTimeUtc(path),null);
        if(!GetFileInformationByHandle(stream.SafeFileHandle,out var info))
            throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_FF933C9CFCE4")+path,new Win32Exception(Marshal.GetLastWin32Error()));
        var length=((long)info.SizeHigh<<32)|info.SizeLow;
        var modified=DateTime.FromFileTimeUtc(((long)info.ModifiedHigh<<32)|info.ModifiedLow);
        var identity=$"{info.VolumeSerial:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}";
        return new(length,modified,identity);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes,CreatedLow,CreatedHigh,AccessedLow,AccessedHigh,ModifiedLow,ModifiedHigh;
        public uint VolumeSerial,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;
    }
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file,out FileInformation information);
}

internal sealed class RenameLease(FileStream stream,RenameFingerprint fingerprint):IDisposable
{
    internal RenameFingerprint Fingerprint {get;}=fingerprint;
    public void Dispose()=>stream.Dispose();
}
