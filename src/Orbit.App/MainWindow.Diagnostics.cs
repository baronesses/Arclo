using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Orbit.Core.Diagnostics;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _toolkitDiagnosticsCancellation;
    private ToolkitDiagnosticsReport? _lastDiagnosticsReport;
    private Func<CancellationToken,Task<ToolkitDiagnosticsReport>>? _diagnosticsInspectOverride;
    private Action<string>? _diagnosticsCopyOverride;
    private void StopToolkitDiagnostics()=>_toolkitDiagnosticsCancellation?.Cancel();
    private void ShowDiagnostics()
    {
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_9F4D95DF6747"),global::Orbit.Core.Localization.L.Get("Text_0D2A362E87DC"));
        int panelVersion=_comparisonVersion;
        bool Current()=>!_closing&&panelVersion==_comparisonVersion&&_panel.Visibility==Visibility.Visible;
        var scope=Text(global::Orbit.Core.Localization.L.Get("Text_8872F1ED00A5"),12);scope.SetResourceReference(TextBlock.ForegroundProperty,"GlassMutedText");_panelContent.Children.Add(scope);
        var status=Text("",12);status.Tag="diagnostics-status";status.SetResourceReference(TextBlock.ForegroundProperty,"TextBrush");_panelContent.Children.Add(status);
        var controls=new WrapPanel();var refresh=Btn(global::Orbit.Core.Localization.L.Get("Text_DC7DBEB80969"),(_,_)=>{});refresh.Tag="diagnostics-refresh";
        var cancel=Btn(global::Orbit.Core.Localization.L.Get("Text_0A3EB4313489"),(_,_)=>{if(Current())StopToolkitDiagnostics();});cancel.IsEnabled=false;cancel.Tag="diagnostics-cancel";
        var copy=Btn(global::Orbit.Core.Localization.L.Get("Text_B1050BDFD62A"),(_,_)=>{});copy.Tag="diagnostics-copy";copy.IsEnabled=false;
        controls.Children.Add(refresh);controls.Children.Add(cancel);controls.Children.Add(copy);_panelContent.Children.Add(controls);
        var rows=new StackPanel{Tag="diagnostics-components",Margin=new Thickness(0,8,0,0)};_panelContent.Children.Add(rows);
        ToolkitDiagnosticsReport? displayed=null;
        void Render(ToolkitDiagnosticsReport report,bool cached){
            displayed=report;rows.Children.Clear();copy.IsEnabled=true;
            int ready=report.Components.Count(c=>c.State.ToString()=="Ready");
            status.Text=global::Orbit.Core.Localization.L.Format("Text_147686854730",(object?)((cached?global::Orbit.Core.Localization.L.Get("Text_A029BC63737B"):global::Orbit.Core.Localization.L.Get("Text_880C43C534C6"))),(object?)(report.GeneratedUtc.ToLocalTime()),(object?)(ready),(object?)(report.Components.Count));
            foreach(var component in report.Components){
                var state=component.State.ToString();var label=state=="Ready"?global::Orbit.Core.Localization.L.Get("Text_0725CEA11B0B"):state=="Missing"?global::Orbit.Core.Localization.L.Get("Text_EA5B0D964DFA"):global::Orbit.Core.Localization.L.Get("Text_75AF9113CA5F");
                var title=Text(label+" · "+component.Name,12,state=="Ready"?"#B9E1D6":state=="Missing"?"#E3CCA3":"#EDACB9");
                if(SystemParameters.HighContrast)title.SetResourceReference(TextBlock.ForegroundProperty,"TextBrush");
                var detail=new StackPanel();
                void DetailText(string value){var block=Text(value,11);block.SetResourceReference(TextBlock.ForegroundProperty,"TextBrush");detail.Children.Add(block);}
                if(!string.IsNullOrWhiteSpace(component.Version))DetailText(component.Version);
                if(!string.IsNullOrWhiteSpace(component.Path))detail.Children.Add(new TextBox{Text=component.Path,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,3,0,4)});
                if(!string.IsNullOrWhiteSpace(component.Detail))DetailText(component.Detail);
                var expander=new Expander{Header=title,Content=detail,Margin=new Thickness(0,1,0,2)};expander.SetResourceReference(Control.ForegroundProperty,"TextBrush");rows.Children.Add(expander);
            }
        }
        async Task RefreshAsync(){
            if(!Current()||!refresh.IsEnabled)return;
            StopToolkitDiagnostics();var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_resultToolsCancellation.Token);_toolkitDiagnosticsCancellation=cancellation;
            refresh.IsEnabled=false;cancel.IsEnabled=true;copy.IsEnabled=false;displayed=null;rows.Children.Clear();status.Text=global::Orbit.Core.Localization.L.Get("Text_0AE0086BA673");
            try{
                var report=await(_diagnosticsInspectOverride?.Invoke(cancellation.Token)??new ToolkitDiagnosticsEngine().InspectAsync(cancellation.Token));
                if(!Current()||cancellation.IsCancellationRequested)return;
                _lastDiagnosticsReport=report;Render(report,false);
            }catch(OperationCanceledException){if(Current())status.Text=global::Orbit.Core.Localization.L.Get("Text_B73821F43D17");}
            catch(Exception ex){if(Current())status.Text=global::Orbit.Core.Localization.L.Get("Text_32886579562A")+(ex.Message.Length>512?ex.Message[..512]:ex.Message);}
            finally{if(ReferenceEquals(_toolkitDiagnosticsCancellation,cancellation))_toolkitDiagnosticsCancellation=null;cancellation.Dispose();if(Current()){refresh.IsEnabled=true;cancel.IsEnabled=false;}}
        }
        refresh.Click+=(_,_)=>{if(Current())_ = TrackResultTask(RefreshAsync());};
        copy.Click+=(_,_)=>{
            if(!Current()||!copy.IsEnabled||displayed is null)return;
            try{var text=displayed.ToText();if(_diagnosticsCopyOverride is not null)_diagnosticsCopyOverride(text);else if(!IsDiagnosticRun)Clipboard.SetText(text);else return;Toast(global::Orbit.Core.Localization.L.Get("Text_0EBEDF8F8D4F"));}
            catch(Exception ex){Toast(global::Orbit.Core.Localization.L.Get("Text_97604C829E87")+ex.Message);}
        };
        if(_lastDiagnosticsReport is not null)Render(_lastDiagnosticsReport,true);else _=TrackResultTask(RefreshAsync());
    }
}
