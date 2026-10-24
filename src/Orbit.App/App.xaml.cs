using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Orbit.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private static string? _diagnosticLogDirectory;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) => { Log(ex.Exception); MessageBox.Show(ex.Exception.Message, global::Orbit.Core.Localization.L.Get("Text_21D49B1F4E3A")); ex.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log(ex.Exception); ex.SetObserved(); };
        var isolated = e.Args.Contains("--capture") || e.Args.Contains("--ui-smoke") || e.Args.Contains("--toolkit-smoke") || e.Args.Contains("--popup-smoke") || e.Args.Contains("--glass-smoke") || e.Args.Contains("--folder-smoke") || e.Args.Contains("--systems-smoke") || e.Args.Contains("--editing-smoke") || e.Args.Contains("--productivity-smoke") || e.Args.Contains("--results-smoke");
        isolated|=e.Args.Contains("--image-tools-smoke")||e.Args.Contains("--media-tools-smoke")||e.Args.Contains("--personalization-smoke")||e.Args.Contains("--document-tools-smoke")||e.Args.Contains("--workflows-smoke");
        isolated|=e.Args.Contains("--pdf-utilities-smoke");
        isolated|=e.Args.Contains("--media-export-smoke");
        isolated|=e.Args.Contains("--image-editing-smoke");
        isolated|=e.Args.Contains("--media-tracks-smoke");
        isolated|=e.Args.Contains("--archive-integrity-smoke");
        isolated|=e.Args.Contains("--audio-metadata-smoke")||e.Args.Contains("--usability-smoke")||e.Args.Contains("--sevenzip-smoke")||e.Args.Contains("--sevenzip-password-smoke")||e.Args.Contains("--archive-selection-smoke")||e.Args.Contains("--watermark-smoke")||e.Args.Contains("--queue-smoke");
        isolated|=e.Args.Contains("--themes-smoke")||e.Args.Contains("--selection-latency-smoke");
        isolated|=e.Args.Contains("--download-smoke");
        isolated|=e.Args.Contains("--download-flow-smoke");
        isolated|=e.Args.Contains("--radial-workspace-smoke");
        isolated|=e.Args.Contains("--trim-timeline-smoke")||e.Args.Contains("--standalone-smoke")||e.Args.Contains("--localization-smoke");
        Orbit.Core.Localization.L.SetLanguage(isolated?"ru":new Settings.SettingsStore().Load().Language);
        Orbit.Core.ImageMagickEngine.ManagedFallback=new Codecs.WindowsImageCodec();
        Orbit.Core.ImageMagickEngine.PreferManagedCodecs=!isolated||e.Args.Contains("--standalone-smoke")||e.Args.Contains("--localization-smoke");
        Platform.AppComponents.Initialize(isolated);
        if(isolated){
            _diagnosticLogDirectory=Path.Combine(AppContext.BaseDirectory,"diagnostic-logs");
            var temporary=Path.Combine(AppContext.BaseDirectory,"diagnostic-smoke-temp");Directory.CreateDirectory(temporary);
            Environment.SetEnvironmentVariable("TEMP",temporary);Environment.SetEnvironmentVariable("TMP",temporary);
        }
        _mutex = new Mutex(true, "Local\\OrbitToolkitPreview", out var first);
        if (!first && !isolated)
        {
            if(e.Args.Contains("--background")&&!e.Args.Any(path=>File.Exists(path)||Directory.Exists(path))){Shutdown();return;}
            var inbox = Path.Combine(Path.GetTempPath(), "OrbitToolkit-inbox");
            Directory.CreateDirectory(inbox);
            File.WriteAllLines(Path.Combine(inbox, Guid.NewGuid()+".txt"), e.Args.Where(path=>File.Exists(path)||Directory.Exists(path)).Select(path=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))));
            Shutdown(); return;
        }
        var window = new MainWindow(e.Args);
        MainWindow = window;
        if(e.Args.Contains("--themes-smoke")||e.Args.Contains("--selection-latency-smoke"))window.ShowActivated=false;
        if(e.Args.Contains("--queue-smoke")){window.ShowActivated=false;window.IsHitTestVisible=false;}
        if(e.Args.Contains("--download-smoke")){window.ShowActivated=false;window.IsHitTestVisible=false;}
        if(e.Args.Contains("--download-flow-smoke")){window.ShowActivated=false;window.IsHitTestVisible=false;}
        if(e.Args.Contains("--radial-workspace-smoke")){window.ShowActivated=false;window.IsHitTestVisible=false;}
        if(e.Args.Contains("--trim-timeline-smoke")){window.ShowActivated=false;window.IsHitTestVisible=false;}
        if(e.Args.Contains("--standalone-smoke")){window.ShowActivated=false;window.IsHitTestVisible=false;}
        if(e.Args.Contains("--localization-smoke")){window.ShowActivated=false;window.IsHitTestVisible=false;}
        if(e.Args.Contains("--background")&&!isolated&&!e.Args.Any(path=>File.Exists(path)||Directory.Exists(path))){window.ShowActivated=false;window.Opacity=0;}
        window.Show();
    }
    internal static void Log(Exception ex)
    {
        try { var dir = _diagnosticLogDirectory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arclo"); Directory.CreateDirectory(dir); File.AppendAllText(Path.Combine(dir,"diagnostics.log"), DateTime.Now.ToString("s")+" "+ex+Environment.NewLine); } catch { }
    }
    protected override void OnExit(ExitEventArgs e) { _mutex?.Dispose(); base.OnExit(e); }
}
