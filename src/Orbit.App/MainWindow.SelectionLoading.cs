using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Orbit.App.Platform;
using Orbit.App.Controls;
using Orbit.Core;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private bool _selectionPreviewReady;
    private void UpdatePreviewActionAvailability()
    {
        foreach(var sector in _radial.Children.OfType<SectorButton>())
            if(sector.Action==ImageAction.Crop)sector.IsEnabled=_selectionPreviewReady&&_metadata?.Kind==ToolkitKind.Image;
    }
    // Diagnostic hooks exercise the real publication/version paths with a deliberately slow reader/decoder.
    private Func<IntPtr,Task<ExplorerSelectionResult>>? _selectionReaderOverride;
    private Func<IntPtr>? _invocationForegroundOverride;
    private Func<string,CancellationToken,Task<ToolkitMetadata>>? _selectionInspectorOverride;
    private Func<string,CancellationToken,Task<byte[]?>>? _selectionPreviewOverride;
    private Task<ExplorerSelectionResult> ReadInvocationSelectionAsync(IntPtr owner)=>
        IsDiagnosticRun&&_selectionReaderOverride!=null?_selectionReaderOverride(owner):ReadExplorerSelectionAsync(owner);
    private IntPtr InvocationForeground()=>IsDiagnosticRun&&_invocationForegroundOverride!=null?_invocationForegroundOverride():NativeScreen.GetCursorInvocation().ForegroundWindow;

    private async Task<ToolkitMetadata> InspectSelectionAsync(string path,CancellationToken ct)
    {
        if(IsDiagnosticRun&&_selectionInspectorOverride!=null)return await _selectionInspectorOverride(path,ct);
        return await Task.Run(async()=>{
            if(_selectionCache.TryGetMetadata(path,out var cached))return cached;
            var metadata=await _engine.InspectAsync(path,ct);
            _selectionCache.StoreMetadata(path,metadata);
            return metadata;
        },ct);
    }

    private async Task LoadSelectionPreviewAsync(string path,int version,CancellationToken ct)
    {
        try{
            byte[]? png;
            if(IsDiagnosticRun&&_selectionPreviewOverride!=null)png=await _selectionPreviewOverride(path,ct);
            else png=await Task.Run(async()=>{
                if(_selectionCache.TryGetPreview(path,out var cached))return cached;
                var bytes=await _engine.CreatePreviewAsync(path,480,ct);
                if(bytes!=null)_selectionCache.StorePreview(path,bytes);
                return bytes;
            },ct);
            if(png==null||ct.IsCancellationRequested)return;
            var image=await Task.Run(()=>{
                var bitmap=new BitmapImage();using var stream=new MemoryStream(png);
                bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();return bitmap;
            },ct);
            if(version!=_loadVersion||_closing||ct.IsCancellationRequested)return;
            var previous=_preview;_preview=image;_radial.Preview.Source=image;_selectionPreviewReady=true;UpdatePreviewActionAvailability();
            // A user may already have opened a size/crop card after metadata became ready.
            foreach(var crop in GlassDescendants<CropPreview>(_panelContent))if(ReferenceEquals(crop.Source,previous))crop.Source=image;
            foreach(var preview in GlassDescendants<Image>(_panelContent))if(ReferenceEquals(preview.Source,previous))preview.Source=image;
        }catch(OperationCanceledException){ }
        catch(Exception error){if(IsDiagnosticRun)App.Log(error);/* A thumbnail failure must not discard a validated file. */}
    }
    private static async Task ObserveSelectionPreviewAsync(Task preview){try{await preview;}catch(OperationCanceledException){ }}
}
