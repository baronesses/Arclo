using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Orbit.App.History;
using Orbit.App.Platform;
using Orbit.App.Presets;
using Orbit.App.Settings;
using Orbit.Core;
using Orbit.Core.Diagnostics;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private async Task RunUsabilitySmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"usability-smoke");var run=Path.Combine(destination,"run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var checks=new List<string>();var copies=new List<string>();bool gateHeld=false;
        void Require(bool value,string text){if(!value)throw new InvalidOperationException("Usability integration: "+text);checks.Add(text);}
        T Tagged<T>(string tag)where T:FrameworkElement=>SystemsDescendants<T>(_panelContent).Single(c=>c.Tag as string==tag);
        Button Named(string label)=>SystemsDescendants<Button>(_panelContent).Single(b=>b.Content as string==label);
        void Click(string label)=>Named(label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void TagClick(string tag)=>Tagged<Button>(tag).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Button[] Commands()=>SystemsDescendants<Button>(_panelContent).Where(b=>b.Tag as string=="tool-command").ToArray();
        void Query(string value)=>Tagged<TextBox>("tool-search").Text=value;
        KeyEventArgs Key(FrameworkElement target,System.Windows.Input.Key key){var e=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(this)!,0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};target.RaiseEvent(e);return e;}
        async Task EditorsAsync()=>await Task.WhenAll(_resultToolTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(60));
        async Task FinishAsync(){await Task.WhenAll(_jobTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(90));await FlushHistoryAsync();}
        async Task ShotAsync(string name){_toast.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Capture(Path.Combine(destination,name));}
        try{
            Directory.CreateDirectory(run);File.Delete(Path.Combine(destination,"passed.json"));File.Delete(Path.Combine(destination,"error.txt"));
            Require(IsDiagnosticRun&&_bridge is null&&!ProductivityIoAllowed&&!PersonalizationIoAllowed,"diagnostic has no real hotkeys, user stores, registry or clipboard integration");
            Require(!ShouldShowWelcome(),"diagnostic launch never automatically opens onboarding");
            var image=Path.Combine(run,"картинка.png");var wave=Path.Combine(run,"звук.wav");WriteResultsImage(image);await WriteEditingMediaAsync(wave,false);
            var originals=new[]{image,wave}.ToDictionary(p=>p,SystemsDigest);
            _outputDirectory=Path.Combine(run,"output");Directory.CreateDirectory(_outputDirectory);
            _historyStore=new HistoryStore(Path.Combine(run,"history.json"));_presetStore=new PresetStore(Path.Combine(run,"presets.json"));_allowDiagnosticProductivityIo=true;
            _diagnosticsCopyOverride=text=>copies.Add(text);SetCompactMode(true);
            Require(HotkeyGesture.TryParse("Ctrl+Shift+Q",out var custom,out _),"owned custom shortcut parses for tutorial rendering");_shortcut=custom;
            _compactSize=360;ShowWelcome();
            for(int step=0;step<3;step++){
                await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);
                Require(_panel.Child is ScrollViewer scroll&&scroll.ScrollableHeight<=1,"smallest360DIP guide step fits without scrolling: "+(step+1));
                if(step==0)await ShotAsync("00-welcome-360.png");if(step<2)TagClick("welcome-next");
            }
            ClosePanel();_compactSize=440;
            _welcomeCompleted=false;ShowWelcome();
            Require(Tagged<TextBlock>("welcome-step").Text=="Шаг 1 из 3"&&!Tagged<Button>("welcome-back").IsEnabled,"guide begins on the first bounded step with Back disabled");
            await ShotAsync("01-welcome.png");var firstNext=Tagged<Button>("welcome-next");TagClick("welcome-next");
            Require(Tagged<TextBlock>("welcome-shortcut").Text==ShortcutDisplay,"second guide step displays the configured shortcut rather than a fixed default");
            firstNext.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(Tagged<TextBlock>("welcome-step").Text=="Шаг 2 из 3","detached previous-step controls cannot skip a guide step");
            TagClick("welcome-back");Require(Tagged<TextBlock>("welcome-step").Text=="Шаг 1 из 3","guide Back returns to the previous step");
            TagClick("welcome-next");TagClick("welcome-next");_welcomeSaveOverride=()=>false;TagClick("welcome-finish");
            Require(!_welcomeCompleted&&Tagged<TextBlock>("welcome-status").Visibility==Visibility.Visible,"failed guide preference save keeps the final step and rolls completion back");
            var settings=new SettingsStore(Path.Combine(run,"settings.json"));int saves=0;
            _welcomeSaveOverride=()=>{saves++;return settings.Save(new OrbitSettings{WelcomeCompleted=_welcomeCompleted,Shortcut=_shortcut.ToString()}) is null;};
            var finish=Tagged<Button>("welcome-finish");TagClick("welcome-finish");
            Require(_welcomeCompleted&&settings.Load().WelcomeCompleted&&_panel.Visibility==Visibility.Collapsed&&saves==1,"Finish persists completed onboarding only to an owned settings store and closes the guide");
            finish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(saves==1,"detached Finish cannot persist onboarding a second time");
            var imageLoaded=await LoadFilesAsync(new[]{image});Require(imageLoaded,"owned image loads for contextual tool discovery"+(imageLoaded?"":$"; status={_status.Text}; toast={_toastText.Text}; version={_loadVersion}; cancelled={_loadCancellation?.IsCancellationRequested}"));ShowWelcome();var staleNext=Tagged<Button>("welcome-next");ShowMoreTools();staleNext.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(Tagged<TextBox>("tool-search") is not null&&_files.SequenceEqual(new[]{image})&&_jobs.Count==0,"manual guide preserves selection and detached guide controls cannot replace another panel");
            Query("webp");Require(Commands().Length==1&&(Commands()[0].Content as string)=="В WebP…","search discovers a radial conversion for the current image");
            var staleSearch=Commands()[0];Query("EXIF");Require(Commands().Length==1&&(Commands()[0].Content as string)=="Удалить метаданные…","search finds an image tool through an English metadata synonym");
            staleSearch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(_jobs.Count==0,"a filtered-out detached command cannot start processing");
            Query("jpg фон");Require(Commands().Length==1&&(Commands()[0].Content as string)=="JPEG с цветом фона…","multiple search terms match JPEG aliases and background settings together");
            Query("SHA256");Require(Commands().Length==3,"compact SHA256 alias discovers file verification, manifest verification and hash generation");
            Query("неизвестная-команда-999");Require(Commands().Length==0&&Tagged<TextBlock>("tool-search-empty").Visibility==Visibility.Visible,"unknown query shows an empty state and no executable command");
            Click("Очистить");Require(Commands().Length>15&&Tagged<TextBox>("tool-search").Text=="","Clear restores the contextual default tools without keeping a query");
            Query(".-/");Require(!Key(Tagged<TextBox>("tool-search"),System.Windows.Input.Key.Enter).Handled&&_jobs.Count==0,"punctuation-only query cannot invoke a command through Enter");
            Query("webp");await ShotAsync("02-tool-search.png");
            Require(Key(Tagged<TextBox>("tool-search"),System.Windows.Input.Key.Down).Handled,"local Down navigation is consumed by the search result list");
            Require(Key(Commands()[0],System.Windows.Input.Key.Up).Handled,"local Up navigation returns from the first result toward the query");
            await _queueGate.WaitAsync();gateHeld=true;
            Require(Key(Tagged<TextBox>("tool-search"),System.Windows.Input.Key.Enter).Handled,"Enter explicitly invokes the first matching command");
            ShowWelcome();Require(Key(this,System.Windows.Input.Key.Escape).Handled&&_panel.Visibility==Visibility.Collapsed&&!_jobs.Last().Cancellation.IsCancellationRequested,"Esc closes the guide while preserving a queued conversion");
            _queueGate.Release();gateHeld=false;await FinishAsync();
            Require(_jobs.Count==1&&_jobs.Last().HistoryStatus==HistoryStatus.Succeeded&&_jobs.Last().Result?.Format=="WEBP","search Enter produces a real verified WebP through the existing queue");
            ShowMoreTools();Query("webp");var staleImage=Commands()[0];Require(await LoadFilesAsync(new[]{wave}),"owned audio replaces an image selection");staleImage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(_jobs.Count==1,"a command from the old file selection cannot process a new audio source");
            ShowMoreTools();Query("crop");Require(Commands().Length==0,"audio selection excludes image crop commands");Query("tags");Require(Commands().Length==0,"WAV does not expose unsupported audio tag editing");Query("vorbis");Require(Commands().Length==1&&(Commands()[0].Content as string)=="Экспорт M4A / Opus / OGG…","audio selection exposes Vorbis through codec aliases");
            Query("help");TagClick("tool-command");Require(Tagged<TextBlock>("welcome-step").Text=="Шаг 1 из 3"&&_files.SequenceEqual(new[]{wave}),"search opens the manual guide without changing the selected audio");
            ShowMoreTools();Query("ffmpeg");Require(Commands().Length==1,"component name finds the diagnostics command");TagClick("tool-command");await EditorsAsync();
            var report=_lastDiagnosticsReport;Require(report is not null&&report.Components.Count>=11&&report.Components.All(c=>c.State==ToolkitDiagnosticState.Ready),"real portable diagnostics launches every bundled engine and confirms both OCR languages");
            Require(Tagged<StackPanel>("diagnostics-components").Children.Count==report!.Components.Count,"diagnostics renders every real tool and language component");await ShotAsync("03-diagnostics.png");
            TagClick("diagnostics-copy");Require(copies.Count==1&&copies[0]==report.ToText()&&!copies[0].Contains(Path.GetFileName(image),StringComparison.Ordinal),"report copy is intercepted and contains component data without selected-file information");
            var staleCopy=Tagged<Button>("diagnostics-copy");ShowMoreTools();staleCopy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(copies.Count==1,"detached diagnostics Copy cannot write a report after panel closure");
            int inspections=0;_diagnosticsInspectOverride=_=>{inspections++;return Task.FromResult(report);};ShowDiagnostics();
            Require(inspections==0&&Tagged<TextBlock>("diagnostics-status").Text.StartsWith("Предыдущая проверка",StringComparison.Ordinal),"reopening diagnostics shows explicitly dated cached data without rerunning native tools");
            var pending=new TaskCompletionSource<ToolkitDiagnosticsReport>(TaskCreationOptions.RunContinuationsAsynchronously);CancellationToken pendingToken=default;
            _diagnosticsInspectOverride=token=>{pendingToken=token;return pending.Task;};TagClick("diagnostics-refresh");ShowMoreTools();
            Require(pendingToken.IsCancellationRequested,"leaving diagnostics cancels the active inspection token");pending.SetResult(report);await EditorsAsync();
            Require(Tagged<TextBox>("tool-search") is not null,"a late diagnostics completion cannot replace a different panel");
            ShowDiagnostics();_diagnosticsInspectOverride=async token=>{await Task.Delay(Timeout.Infinite,token);return report;};TagClick("diagnostics-refresh");TagClick("diagnostics-cancel");await EditorsAsync();
            Require(Tagged<TextBlock>("diagnostics-status").Text.Contains("отменена",StringComparison.Ordinal)&&Tagged<Button>("diagnostics-refresh").IsEnabled&&!Tagged<Button>("diagnostics-copy").IsEnabled,"Cancel restores refresh and does not present a partial inspection as a completed report");
            _diagnosticsInspectOverride=_=>Task.FromException<ToolkitDiagnosticsReport>(new IOException("Контрольная ошибка проверки"));TagClick("diagnostics-refresh");await EditorsAsync();
            Require(Tagged<TextBlock>("diagnostics-status").Text.Contains("Контрольная ошибка",StringComparison.Ordinal)&&Tagged<Button>("diagnostics-refresh").IsEnabled,"inspection failure remains visible and permits a retry");
            _diagnosticsInspectOverride=null;ClearSelection();ShowMoreTools();Query("webp");Require(Commands().Length==0,"empty selection cannot discover a source-dependent conversion");Query("help");Require(Commands().Length==1,"help remains available without any selected file");
            Require(originals.All(p=>SystemsDigest(p.Key)==p.Value),"all original image and audio bytes remain unchanged");
            Require(!Directory.EnumerateDirectories(_outputDirectory).Any(p=>Path.GetFileName(p).StartsWith(".orbit-",StringComparison.Ordinal)),"real conversion leaves no private output staging");
            var json=JsonSerializer.Serialize(new{checks=checks.Count,items=checks,run,userProfileIo=false,nativeClipboardWrites=false,registryWrites=false,nativeShellLaunches=0,physicalInput=false,listeningPerformed=false},new JsonSerializerOptions{WriteIndented=true});
            File.WriteAllText(Path.Combine(destination,"passed.json"),json);File.WriteAllText(Path.Combine(run,"passed.json"),json);
        }catch(Exception ex){App.Log(ex);File.WriteAllText(Path.Combine(destination,"error.txt"),ex.ToString());}
        finally{if(gateHeld)_queueGate.Release();_diagnosticsInspectOverride=null;_diagnosticsCopyOverride=null;_welcomeSaveOverride=null;Quit();}
    }
}
