using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Orbit.App.Layout;
using Orbit.App.Platform;
using Orbit.Core;

namespace Orbit.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// MainWindow integration checks using deterministic input samples and its real hide/invalidation paths.
    /// This diagnostic never activates a window, synthesizes input, captures the mouse, or starts a codec job.
    /// </summary>
    private Task RunPopupSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"popup-smoke");
        var checks=new List<string>();
        var bounds=new Rect(0,0,440,440);
        var center=new Point(220,220);
        var away=new Point(1000,1000);
        var corner=new Point(1,1);
        var fakeInvocation=new HotkeyInvocation(IntPtr.Zero,220,220);
        var originalLoadCancellation=_loadCancellation;
        CancellationTokenSource? pendingLoad=null;
        JobEntry? sentinelJob=null;
        void Require(bool condition,string name)
        {
            if(!condition)throw new InvalidOperationException("Popup integration: "+name);
            checks.Add(name);
        }
        PopupDismissInput Input(double milliseconds,Point cursor,bool active=true,bool suppressed=false,
            PopupContentMode mode=PopupContentMode.Radial,bool? compact=null)
            =>new(TimeSpan.FromMilliseconds(milliseconds),bounds,cursor,1,mode,true,compact??_compact,active,suppressed);
        void ArmCompact()
        {
            StopPopupDismissal();
            WindowState=WindowState.Normal;
            _popupAnchor=fakeInvocation;
            SetCompactMode(true);
            _popupDismiss.OnShown(TimeSpan.Zero);
            _popupDismissTimer.Start();
        }
        void RequireDismissed(string name)
        {
            Require(WindowState==WindowState.Minimized&&!IsVisible,name+": actual hidden window is minimized");
            Require(!_popupDismissTimer.IsEnabled,name+": dismissal timer stopped");
        }
        try
        {
            Directory.CreateDirectory(destination);
            File.Delete(Path.Combine(destination,"passed.json"));
            File.Delete(Path.Combine(destination,"error.txt"));
            Require(_args.Contains("--popup-smoke")&&IsDiagnosticRun,"diagnostic flag isolates the test");
            Require(_bridge is null,"no tray or global hotkey bridge was installed");
            Hide();
            Require(!IsVisible,"test window stays hidden without activation");

            ArmCompact();
            Require(_compact&&_popupDismissTimer.IsEnabled,"compact popup policy and real timer armed manually");
            int versionBefore=_loadVersion;
            Require(ApplyPopupDismissal(Input(219,center,active:false))==PopupDismissReason.None&&WindowState==WindowState.Normal,
                "foreground loss during show grace does not hide");
            Require(ApplyPopupDismissal(Input(220,center,active:false))==PopupDismissReason.ForegroundChanged,
                "foreground loss reaches the MainWindow dismissal path");
            RequireDismissed("foreground loss");
            Require(_loadVersion==versionBefore+1,"foreground dismissal invalidates the pending load version");

            WindowState=WindowState.Normal;
            SetCompactMode(false);
            versionBefore=_loadVersion;
            Require(ApplyPopupDismissal(Input(10000,away,active:false,compact:false))==PopupDismissReason.None,
                "full manager ignores foreground and cursor dismissal input");
            Require(!_compact&&WindowState==WindowState.Normal&&_loadVersion==versionBefore&&!_popupDismissTimer.IsEnabled,
                "manager remains open and does not invalidate selection on policy ticks");

            ArmCompact();
            Require(ApplyPopupDismissal(Input(220,away))==PopupDismissReason.None,"first cursor departure starts a leave interval");
            Require(ApplyPopupDismissal(Input(400,center))==PopupDismissReason.None,"cursor return cancels the first leave interval");
            Require(ApplyPopupDismissal(Input(450,away))==PopupDismissReason.None&&
                ApplyPopupDismissal(Input(749,away))==PopupDismissReason.None&&WindowState==WindowState.Normal,
                "second cursor departure receives its own complete delay");
            Require(ApplyPopupDismissal(Input(750,away))==PopupDismissReason.CursorLeft,"continuous cursor absence eventually dismisses");
            RequireDismissed("cursor departure");

            ArmCompact();
            OpenPanel("Диагностическая карточка","Проверка границ без пользовательского ввода");
            _popupDismiss.OnShown(TimeSpan.Zero);
            Require(_panel.Visibility==Visibility.Visible&&
                ApplyPopupDismissal(Input(220,corner,mode:PopupContentMode.Card))==PopupDismissReason.None&&
                ApplyPopupDismissal(Input(1000,corner,mode:PopupContentMode.Card))==PopupDismissReason.None&&WindowState==WindowState.Normal,
                "real parameter card keeps its rectangular corner inside");
            ClosePanel();
            Require(ApplyPopupDismissal(Input(1001,corner))==PopupDismissReason.None&&
                ApplyPopupDismissal(Input(1300,corner))==PopupDismissReason.None,
                "returning to radial geometry starts a fresh corner leave delay");
            Require(ApplyPopupDismissal(Input(1301,corner))==PopupDismissReason.CursorLeft,
                "transparent radial corner is outside the circle");
            RequireDismissed("radial corner");

            ArmCompact();
            WithPopupInteraction(()=>
            {
                Require(_popupInteractionDepth==1,"modal interaction enters the MainWindow guard");
                Require(ApplyPopupDismissal(Input(1000,away,active:false,suppressed:_popupInteractionDepth>0))==PopupDismissReason.None&&
                    ApplyPopupDismissal(Input(100000,away,active:false,suppressed:_popupInteractionDepth>0))==PopupDismissReason.None&&WindowState==WindowState.Normal,
                    "modal guard blocks foreground and cursor dismissal while held");
                return true;
            });
            Require(_popupInteractionDepth==0,"modal interaction releases its guard");
            Require(ApplyPopupDismissal(Input(100100,away,active:false))==PopupDismissReason.None&&
                ApplyPopupDismissal(Input(100319,away,active:false))==PopupDismissReason.None,
                "modal release grants a fresh focus restoration grace period");
            Require(ApplyPopupDismissal(Input(100320,away,active:false))==PopupDismissReason.ForegroundChanged,
                "foreground dismissal resumes after modal release grace");
            RequireDismissed("modal release");

            ArmCompact();
            Require(ApplyPopupDismissal(Input(220,away,suppressed:true))==PopupDismissReason.None&&
                ApplyPopupDismissal(Input(5000,away,active:false,suppressed:true))==PopupDismissReason.None&&WindowState==WindowState.Normal,
                "simulated capture or drag hold prevents dismissal");
            Require(ApplyPopupDismissal(Input(5100,away))==PopupDismissReason.None&&
                ApplyPopupDismissal(Input(5319,away))==PopupDismissReason.None&&
                ApplyPopupDismissal(Input(5320,away))==PopupDismissReason.None&&
                ApplyPopupDismissal(Input(5619,away))==PopupDismissReason.None,
                "capture release restarts grace and the complete cursor leave delay");
            Require(ApplyPopupDismissal(Input(5620,away))==PopupDismissReason.CursorLeft,"cursor dismissal resumes after capture release");
            RequireDismissed("capture release");

            ArmCompact();
            var selectionBefore=_files;
            try
            {
                _files=new List<string>{Path.Combine(destination,"diagnostic-selection-not-opened.png")};
                var protectedSelection=_files;
                int invocationBefore=_invocationVersion,loadBefore=_loadVersion,sessionBefore=_popupSession,pendingBefore=_pendingPopupInvocation;
                WithPopupInteraction(()=>
                {
                    InvokeAtCursor(fakeInvocation);
                    Require(_invocationVersion==invocationBefore&&_loadVersion==loadBefore&&_popupSession==sessionBefore&&
                        _pendingPopupInvocation==pendingBefore&&ReferenceEquals(_files,protectedSelection),
                        "hotkey invocation during a modal guard preserves selection and invocation identity");
                    return true;
                });
            }
            finally{_files=selectionBefore;}

            const string sentinelMessage="popup-smoke-expected-interaction-exception";
            bool interactionExceptionObserved=false;
            try
            {
                WithPopupInteraction(()=>WithPopupInteraction<bool>(()=>
                {
                    Require(_popupInteractionDepth==2,"nested interaction increments guard depth");
                    throw new InvalidOperationException(sentinelMessage);
                }));
            }
            catch(InvalidOperationException exception)when(exception.Message==sentinelMessage){interactionExceptionObserved=true;}
            Require(interactionExceptionObserved&&_popupInteractionDepth==0,"interaction finally restores depth after a nested exception");
            Require(ApplyPopupDismissal(Input(220,center,active:false,suppressed:_popupInteractionDepth>0))==PopupDismissReason.ForegroundChanged,
                "an interaction exception does not leave dismissal permanently suppressed");
            RequireDismissed("interaction exception");

            ArmCompact();
            _pendingPopupInvocation=12345;_selectionLoading=true;
            int pendingInvocationVersion=_invocationVersion;
            SetCompactMode(false);
            Require(_invocationVersion==pendingInvocationVersion+1&&_pendingPopupInvocation==0&&!_selectionLoading,
                "switching to manager invalidates a pending popup invocation");
            Require(ApplyPopupDismissal(Input(10000,away,active:false,compact:false))==PopupDismissReason.None&&WindowState==WindowState.Normal,
                "manager stays open after cancelling a pending popup invocation");

            ArmCompact();
            pendingLoad=new CancellationTokenSource();_loadCancellation=pendingLoad;
            _pendingPopupInvocation=23456;_selectionLoading=true;
            int loadVersionBeforeHide=_loadVersion,invocationVersionBeforeHide=_invocationVersion,jobTaskCountBefore=_jobTasks.Count;
            sentinelJob=new JobEntry(Path.Combine(destination,"diagnostic-job-not-started.png"),new ImageJobOptions{Action=ImageAction.WebP});
            _jobs.Add(sentinelJob);
            HideToTray();
            Require(pendingLoad.IsCancellationRequested&&_loadVersion==loadVersionBeforeHide+1&&
                _invocationVersion==invocationVersionBeforeHide+1&&_pendingPopupInvocation==0&&!_selectionLoading,
                "hiding cancels load and invalidates both pending invocation and load versions");
            Require(!sentinelJob.Cancellation.IsCancellationRequested&&_jobs.Contains(sentinelJob)&&_jobTasks.Count==jobTaskCountBefore,
                "hiding preserves an existing queued job cancellation token");
            RequireDismissed("pending load hide");
            _jobs.Remove(sentinelJob);
            Require(!sentinelJob.Cancellation.IsCancellationRequested,"sentinel job removed before Quit can cancel it");

            File.WriteAllText(Path.Combine(destination,"passed.json"),JsonSerializer.Serialize(new
            {
                Passed=checks.Count,Checks=checks,HiddenWindowIntegration=true,DeterministicClockAndInput=true,
                NativeForegroundSwitchTest=false,NativeInputSynthesized=false,NativeMouseCaptureTest=false,
                CodecJobStarted=false,JobCancellationCheckUsesSentinel=true
            },new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception exception)
        {
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination,"error.txt"),exception.ToString());
        }
        finally
        {
            if(sentinelJob!=null){_jobs.Remove(sentinelJob);sentinelJob.Cancellation.Dispose();}
            _loadCancellation=originalLoadCancellation;
            pendingLoad?.Dispose();
            StopPopupDismissal();
            Quit();
        }
        return Task.CompletedTask;
    }
}
