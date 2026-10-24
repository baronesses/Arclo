using System;
using System.IO;
using System.Security;
using System.Text.Json;

namespace Orbit.App.Settings;

/// <summary>Small local JSON store. Construction performs no I/O; diagnostic runs can skip Load entirely.</summary>
public sealed class SettingsStore
{
    private const int MaximumSettingsBytes=64*1024;
    private static readonly JsonSerializerOptions JsonOptions=new()
    {
        PropertyNamingPolicy=JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive=true,
        WriteIndented=true,
        MaxDepth=32
    };

    public SettingsStore(string? path=null)
    {
        FilePath=path??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Arclo","settings.json");
    }

    public string FilePath { get; }

    /// <summary>Missing, malformed, oversized or inaccessible files fall back to defaults without modifying the file.</summary>
    public OrbitSettings Load()
    {
        try
        {
            using var stream=new FileStream(FilePath,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
            if(stream.Length is <=0 or >MaximumSettingsBytes)return new OrbitSettings();
            var bytes=new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            // Accept UTF-8 BOM files created by Windows editors as well as our BOM-free output.
            int offset=bytes.Length>=3&&bytes[0]==0xef&&bytes[1]==0xbb&&bytes[2]==0xbf?3:0;
            var settings=(JsonSerializer.Deserialize<OrbitSettings>(bytes.AsSpan(offset),JsonOptions)??new OrbitSettings()).Normalize();
            using var document=JsonDocument.Parse(bytes.AsMemory(offset));
            bool hasAppearanceVersion=false;
            if(document.RootElement.ValueKind==JsonValueKind.Object)
                foreach(var property in document.RootElement.EnumerateObject())
                    if(property.Name.Equals("appearanceVersion",StringComparison.OrdinalIgnoreCase))hasAppearanceVersion=true;
            // Move the former default once; an explicitly chosen 440 in the new UI remains valid.
            return !hasAppearanceVersion&&settings.CompactSize==440?settings with{CompactSize=380}:settings;
        }
        catch(Exception exception)when(IsExpectedFailure(exception)) { return new OrbitSettings(); }
    }

    /// <summary>Returns null on success or a user-facing Russian error. The previous file survives failed writes.</summary>
    public string? Save(OrbitSettings settings)
    {
        string? temporaryPath=null;
        try
        {
            ArgumentNullException.ThrowIfNull(settings);
            var target=Path.GetFullPath(FilePath);
            var directory=Path.GetDirectoryName(target)!;
            var bytes=JsonSerializer.SerializeToUtf8Bytes(settings.Normalize(),JsonOptions);
            if(bytes.Length>MaximumSettingsBytes)return global::Orbit.Core.Localization.L.Get("Text_17E09B579272");
            Directory.CreateDirectory(directory);
            temporaryPath=Path.Combine(directory,".settings-"+Guid.NewGuid().ToString("N")+".tmp");
            using(var stream=new FileStream(temporaryPath,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk:true);
            }
            // The temporary file is on the same volume; replacement never exposes partially written JSON.
            File.Move(temporaryPath,target,overwrite:true);
            return null;
        }
        catch(Exception exception)when(IsExpectedFailure(exception))
        {
            return exception switch
            {
                UnauthorizedAccessException or SecurityException=>global::Orbit.Core.Localization.L.Get("Text_1032BD1606AB"),
                ArgumentException or NotSupportedException=>global::Orbit.Core.Localization.L.Get("Text_69877CEDA408"),
                _=>global::Orbit.Core.Localization.L.Get("Text_40CCB7BF0E77")
            };
        }
        finally
        {
            if(temporaryPath!=null)
            {
                try { File.Delete(temporaryPath); }
                catch(Exception exception)when(IsExpectedFailure(exception)) { }
            }
        }
    }

    private static bool IsExpectedFailure(Exception exception)=>exception is IOException or UnauthorizedAccessException or
        JsonException or ArgumentException or NotSupportedException or SecurityException;
}
