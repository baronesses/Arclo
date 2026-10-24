using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orbit.Core;
using Orbit.Core.Presets;

namespace Orbit.App.Presets;

public sealed record PresetLoadResult(IReadOnlyList<ProcessingPreset> Presets,string? Warning);
public sealed record PresetMutationResult(IReadOnlyList<ProcessingPreset> Presets,ProcessingPreset? Preset,string? Error)
{ public bool Success=>Error is null; }

/// <summary>Explicit local CRUD; construction and loading never write, execute jobs or repair malformed JSON.</summary>
public sealed class PresetStore
{
    public const int MaximumFileBytes=256*1024;
    private static readonly ConcurrentDictionary<string,object> Gates=new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=true,WriteIndented=true,MaxDepth=16,
        Converters={new JsonStringEnumConverter<ImageAction>(allowIntegerValues:false)}};
    private readonly object _gate;
    public PresetStore(string? path=null)
    {
        FilePath=path??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Arclo","presets.json");
        // Normalization is deferred so a bad optional path can be reported by Load/Save without constructor I/O.
        string key;try{key=Path.GetFullPath(FilePath);}catch{key=FilePath;}_gate=Gates.GetOrAdd(key,_=>new object());
    }
    public string FilePath {get;}
    public PresetLoadResult Load(){lock(_gate)return LoadCore();}
    /// <summary>Explicit user recovery only: replaces malformed custom JSON with an empty catalog.</summary>
    public PresetMutationResult Reset()
    {
        lock(_gate){try{var empty=Array.Empty<ProcessingPreset>();WriteAtomic(empty);return new(empty,null,null);}
            catch(Exception ex)when(ExpectedFailure(ex)){return new(Array.Empty<ProcessingPreset>(),null,global::Orbit.Core.Localization.L.Get("Text_8D0B4AA29E7A")+ex.Message);}}
    }
    public PresetMutationResult Add(string name,ImageJobOptions options)=>Mutate(items=>{
        var preset=PresetCatalog.CreateCustom(name,options);items.Add(preset);return preset;
    });
    public PresetMutationResult Update(string id,string name,ImageJobOptions options)=>Mutate(items=>{
        int index=items.FindIndex(p=>string.Equals(p.Id,id,StringComparison.OrdinalIgnoreCase));
        if(index<0)throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_236A0D4F4CBD"));
        var preset=PresetCatalog.CreateCustom(name,options,items[index].Id);items[index]=preset;return preset;
    });
    public PresetMutationResult Delete(string id)=>Mutate(items=>{
        int index=items.FindIndex(p=>string.Equals(p.Id,id,StringComparison.OrdinalIgnoreCase));
        if(index<0)throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_CC38E5EC7B94"));
        items.RemoveAt(index);return null;
    });
    public PresetMutationResult Save(IReadOnlyList<ProcessingPreset> presets)=>Mutate(items=>{
        if(presets is null)throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_837305FD5BC3"));items.Clear();items.AddRange(presets);return null;
    });

    private PresetMutationResult Mutate(Func<List<ProcessingPreset>,ProcessingPreset?> change)
    {
        lock(_gate){
            var loaded=LoadCore();
            if(loaded.Warning is not null)return new(loaded.Presets,null,global::Orbit.Core.Localization.L.Get("Text_D65C08681E84")+loaded.Warning+global::Orbit.Core.Localization.L.Get("Text_CC3D903812D1"));
            try{
                var items=loaded.Presets.ToList();var selected=change(items);var normalized=ValidateList(items);WriteAtomic(normalized);
                return new(normalized,selected is null?null:normalized.Single(p=>p.Id==selected.Id),null);
            }catch(Exception ex)when(ExpectedFailure(ex)){return new(loaded.Presets,null,global::Orbit.Core.Localization.L.Get("Text_33E262158157")+ex.Message);}
        }
    }
    private PresetLoadResult LoadCore()
    {
        try{
            using var stream=new FileStream(FilePath,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
            if(stream.Length is <=0 or >MaximumFileBytes)throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_3C0B2F20BCE0"));
            var bytes=new byte[(int)stream.Length];stream.ReadExactly(bytes);int offset=bytes.Length>=3&&bytes[0]==0xef&&bytes[1]==0xbb&&bytes[2]==0xbf?3:0;
            using(var schema=JsonDocument.Parse(bytes.AsMemory(offset),new JsonDocumentOptions{MaxDepth=16}))ValidateJsonSchema(schema.RootElement);
            var document=JsonSerializer.Deserialize<PresetFile>(bytes.AsSpan(offset),JsonOptions);
            if(document is null || document.Version!=1 || document.Presets is null)throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_929CCD4A6E3C"));
            return new(ValidateList(document.Presets),null);
        }catch(FileNotFoundException){return new(Array.Empty<ProcessingPreset>(),null);}
        catch(DirectoryNotFoundException){return new(Array.Empty<ProcessingPreset>(),null);}
        catch(Exception ex)when(ExpectedFailure(ex)){return new(Array.Empty<ProcessingPreset>(),global::Orbit.Core.Localization.L.Get("Text_F087985D47DB")+ex.Message);}
    }
    private static void ValidateJsonSchema(JsonElement root)
    {
        // A missing Action must not silently become enum zero (JPEG); ambiguous case aliases also fail closed.
        void UniqueProperties(JsonElement element){
            if(element.ValueKind==JsonValueKind.Object){var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var property in element.EnumerateObject()){
                if(!seen.Add(property.Name))throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_8E0C8C918421"));UniqueProperties(property.Value);}}
            else if(element.ValueKind==JsonValueKind.Array)foreach(var item in element.EnumerateArray())UniqueProperties(item);
        }
        JsonElement Property(JsonElement element,string name){if(element.ValueKind==JsonValueKind.Object)foreach(var property in element.EnumerateObject())if(string.Equals(property.Name,name,StringComparison.OrdinalIgnoreCase))return property.Value;return default;}
        UniqueProperties(root);var presets=Property(root,"presets");
        if(presets.ValueKind!=JsonValueKind.Array)throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_19C82D243AF5"));
        foreach(var preset in presets.EnumerateArray()){
            var options=Property(preset,"options");var action=Property(options,"action");
            bool NamedAction(JsonElement value)=>value.ValueKind==JsonValueKind.String
                &&Enum.GetNames<ImageAction>().Contains(value.GetString(),StringComparer.OrdinalIgnoreCase);
            if(!NamedAction(action))throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_6ADC51101E0F"));
            if(string.Equals(action.GetString(),nameof(ImageAction.ImageChain),StringComparison.OrdinalIgnoreCase)){
                var steps=Property(options,"chainSteps");
                if(steps.ValueKind!=JsonValueKind.Array||steps.GetArrayLength() is <2 or >4)
                    throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_B9503DD93E64"));
                foreach(var step in steps.EnumerateArray())if(!NamedAction(Property(step,"action")))
                    throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_3962ECD03195"));
            }
        }
    }
    private static IReadOnlyList<ProcessingPreset> ValidateList(IReadOnlyList<ProcessingPreset> items)
    {
        if(items.Count>PresetCatalog.MaximumCustomPresets)throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_4493AAEA5CD6"));
        var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var valid=new List<ProcessingPreset>();
        foreach(var item in items){var preset=PresetCatalog.ValidateCustom(item);if(!ids.Add(preset.Id))throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_C513431DDAD5"));
            if(!names.Add(preset.Name))throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_24A3D425D1D1"));valid.Add(preset);}
        return valid.AsReadOnly();
    }
    private void WriteAtomic(IReadOnlyList<ProcessingPreset> presets)
    {
        var target=Path.GetFullPath(FilePath);var parent=Path.GetDirectoryName(target)!;string? temporary=null;
        try{
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new PresetFile(1,presets),JsonOptions);
            if(bytes.Length>MaximumFileBytes)throw new PresetValidationException(global::Orbit.Core.Localization.L.Get("Text_1C474727F817"));
            Directory.CreateDirectory(parent);temporary=Path.Combine(parent,".orbit-presets-"+Guid.NewGuid().ToString("N")+".tmp");
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){stream.Write(bytes);stream.Flush(flushToDisk:true);}
            File.Move(temporary,target,overwrite:true);
        }finally{if(temporary is not null)try{File.Delete(temporary);}catch(Exception ex)when(ExpectedFailure(ex)){} }
    }
    private static bool ExpectedFailure(Exception ex)=>ex is IOException or UnauthorizedAccessException or SecurityException or JsonException or ArgumentException or NotSupportedException or PresetValidationException;
    private sealed record PresetFile(int Version,IReadOnlyList<ProcessingPreset> Presets);
}
