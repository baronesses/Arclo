using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Orbit.App.Controls;
using Orbit.App.Appearance;
using Orbit.App.Selection;
using Orbit.App.Platform;
using Orbit.App.Layout;
using Orbit.Core;
using Orbit.Core.Documents;
using Orbit.Core.Downloads;
using Orbit.App.History;
using Orbit.Core.Presets;
using ImageMetadata = Orbit.Core.ToolkitMetadata;

namespace Orbit.App;

public sealed partial class MainWindow : Window
{
    private readonly ToolkitEngine _engine=new();
    private readonly SelectionContentCache _selectionCache=new();
    private readonly RadialMenu _radial=new();
    private readonly Border _panel;
    private readonly StackPanel _panelContent=new();
    private readonly ColumnDefinition _sideColumn=new(){Width=new GridLength(0)};
    private readonly TextBlock _status=new(){Text=global::Orbit.Core.Localization.L.Get("Text_0241F96AE439"),FontSize=13,TextTrimming=TextTrimming.CharacterEllipsis};
    private readonly TextBlock _selectionInfo=new(){Text=global::Orbit.Core.Localization.L.Get("Text_92CD4B251C15"),FontSize=12,Foreground=Ink("#777A9E"),TextTrimming=TextTrimming.CharacterEllipsis};
    private readonly Button _jobsButton;
    private readonly Border _toast;
    private readonly TextBlock _toastText=new(){TextWrapping=TextWrapping.Wrap,FontSize=13};
    private readonly List<JobEntry> _jobs=new();
    private readonly List<Task> _jobTasks=new();
    private readonly List<Task> _loadTasks=new();
    private readonly SemaphoreSlim _queueGate=new(1,1);
    private readonly DispatcherTimer _inboxTimer=new(){Interval=TimeSpan.FromMilliseconds(700)};
    private readonly DispatcherTimer _popupDismissTimer=new(){Interval=TimeSpan.FromMilliseconds(80)};
    private readonly PopupDismissPolicy _popupDismiss=new();
    private readonly Stopwatch _popupClock=Stopwatch.StartNew();
    private int _popupInteractionDepth;
    private TimeSpan _lastPopupDragOver=TimeSpan.MinValue;
    private readonly string[] _args;
    private WindowsBridge? _bridge;
    private List<string> _files=new();
    private ImageMetadata? _metadata;
    private ImageSource? _preview;
    private CancellationTokenSource? _loadCancellation;
    private int _loadVersion;
    private int _keyboardIndex=-1;
    private bool _closing;
    private bool _jobsVisible;
    private bool _compact;
    private string? _outputDirectory;
    private readonly Grid _root;
    private readonly Border _shell;
    private readonly WindowBackdrop _backdrop;
    private readonly FrameworkElement _header;
    private readonly FrameworkElement _footer;
    private readonly Button _viewButton;
    private Task _loadTask=Task.CompletedTask;
    private readonly Grid _body;
    private readonly ColumnDefinition _radialColumn=new();
    private readonly Viewbox _radialView;
    private readonly FrameworkElement _ambient;
    private readonly ImageSource? _emptyPreview;
    private HotkeyInvocation? _popupAnchor;
    private int _popupSession;
    private int _invocationVersion;
    private int _pendingPopupInvocation;
    private bool _selectionLoading;
    private string? _lastPopupOutput;
    private double _compactSize=380;
    private ToolkitKind _selectionKind=ToolkitKind.Image;
    private IReadOnlyList<ToolkitMetadata> _selectionMetadata=Array.Empty<ToolkitMetadata>();

    public MainWindow(string[] args)
    {
        _args=args;LoadPreferences();
        Title="Arclo · File Toolkit";Width=960;Height=720;MinWidth=Math.Min(800,SystemParameters.WorkArea.Width-20);MinHeight=Math.Min(650,SystemParameters.WorkArea.Height-20);
        WindowStartupLocation=WindowStartupLocation.CenterScreen;WindowStyle=WindowStyle.None;
        ResizeMode=ResizeMode.CanResizeWithGrip;AllowsTransparency=true;Background=Brushes.Transparent;
        var work=SystemParameters.WorkArea;Width=Math.Min(Width,work.Width-40);Height=Math.Min(Height,work.Height-40);
        FontFamily=new FontFamily("Segoe UI");SetResourceReference(ForegroundProperty,"TextBrush");AllowDrop=true;UseLayoutRounding=true;
        _root=new Grid{ClipToBounds=true};
        _root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(76)});
        _root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
        _root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(82)});
        _shell=new Border{CornerRadius=new CornerRadius(12),BorderThickness=new Thickness(1),Child=_root};
        Content=_shell;
        _backdrop=new WindowBackdrop(this);
        var ambient=new Grid{IsHitTestVisible=false};
        _ambient=ambient;
        Grid.SetRowSpan(ambient,3);_root.Children.Add(ambient);
        var header=new Grid{Margin=new Thickness(30,15,20,0)};
        header.ColumnDefinitions.Add(new ColumnDefinition());header.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var wordmark=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Cursor=Cursors.SizeAll};
        var brand=new StackPanel{VerticalAlignment=VerticalAlignment.Center};
        var lettering=new TextBlock{Text="Arclo",FontSize=25,FontWeight=FontWeights.SemiBold,Foreground=Ink("#E9F2FF"),HorizontalAlignment=HorizontalAlignment.Left};
        brand.Children.Add(lettering);
        brand.Children.Add(new TextBlock{Text="FILE TOOLKIT  /  0.34",FontSize=9,Foreground=Ink("#A2B3CF"),Margin=new Thickness(0,3,0,0)});
        wordmark.Children.Add(brand);wordmark.MouseLeftButtonDown+=(_,e)=>{if(e.ClickCount==1&&e.ButtonState==MouseButtonState.Pressed)DragPopupWindow();};header.Children.Add(wordmark);
        var toolbar=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
        var filesButton=Btn(Orbit.Core.Localization.L.Get("Header.Files"),(_,_)=>ChooseFiles());filesButton.Tag="Header.Files";filesButton.ToolTip=Orbit.Core.Localization.L.Get("Header.Files.Help");toolbar.Children.Add(filesButton);
        var folders=Btn(Orbit.Core.Localization.L.Get("Header.Folders"),(_,_)=>ChooseFolders());folders.Tag="Header.Folders";folders.ToolTip=Orbit.Core.Localization.L.Get("Header.Folders.Help");toolbar.Children.Add(folders);
        _jobsButton=Btn(global::Orbit.Core.Localization.L.Get("Text_4993C1326F23"),(_,_)=>ShowJobs());toolbar.Children.Add(_jobsButton);
        _viewButton=Btn("◌",(_,_)=>ToggleCompact());_viewButton.Tag="Header.Compact";_viewButton.ToolTip=Orbit.Core.Localization.L.Get("Header.Compact.Help");toolbar.Children.Add(_viewButton);
        var more=Btn("⋯",(_,_)=>ShowMoreTools());more.Tag="Header.More";more.ToolTip=Orbit.Core.Localization.L.Get("Header.More.Help");toolbar.Children.Add(more);
        toolbar.Children.Add(CreateMinimizeButton());
        Grid.SetColumn(toolbar,1);header.Children.Add(toolbar);_root.Children.Add(header);_header=header;
        var body=new Grid{Margin=new Thickness(10,0,10,0)};_body=body;
        body.ColumnDefinitions.Add(_radialColumn);body.ColumnDefinitions.Add(_sideColumn);
        var radialView=new Viewbox{Child=_radial,Stretch=Stretch.Uniform,MaxWidth=440,Margin=new Thickness(12)};_radialView=radialView;body.Children.Add(radialView);
        _panel=new Border{BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Padding=new Thickness(18),Margin=new Thickness(12,12,12,12),Visibility=Visibility.Collapsed,VerticalAlignment=VerticalAlignment.Stretch,MaxHeight=690};
        var scroll=new ScrollViewer{Content=_panelContent,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};_panel.Child=scroll;Grid.SetColumn(_panel,1);body.Children.Add(_panel);Grid.SetRow(body,1);_root.Children.Add(body);
        var footer=new Grid{Margin=new Thickness(32,7,30,18)};
        footer.ColumnDefinitions.Add(new ColumnDefinition());footer.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var statusStack=new StackPanel{VerticalAlignment=VerticalAlignment.Center};statusStack.Children.Add(_status);_selectionInfo.Margin=new Thickness(0,7,0,0);statusStack.Children.Add(_selectionInfo);footer.Children.Add(statusStack);
        var hint=new StackPanel{VerticalAlignment=VerticalAlignment.Center};_shortcutHint.Text=ShortcutDisplay;hint.Children.Add(_shortcutHint);_invocationHelp=new TextBlock{Text=Orbit.Core.Localization.L.Get("Footer.Invoke"),FontSize=11,Foreground=Ink("#656B8C"),Margin=new Thickness(0,6,0,0)};hint.Children.Add(_invocationHelp);Grid.SetColumn(hint,1);footer.Children.Add(hint);Grid.SetRow(footer,2);_root.Children.Add(footer);_footer=footer;
        _toast=new Border{BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Padding=new Thickness(18,12,18,12),Margin=new Thickness(35,0,35,90),MaxWidth=680,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Bottom,Visibility=Visibility.Collapsed,IsHitTestVisible=false,Child=_toastText};Grid.SetRowSpan(_toast,3);_root.Children.Add(_toast);
        _shell.MouseLeftButtonDown+=(_,e)=>{if(_compact&&ReferenceEquals(e.OriginalSource,_shell))DragPopupWindow();};
        _emptyPreview=_radial.Preview.Source;
        _radial.CenterSelected+=RadialCenterSelected;
        _radial.CommandSelected+=async command=>{try{await RunRadialCommandAsync(command);}catch(OperationCanceledException){}catch(Exception ex){Toast(ex.Message);}};
        _radial.SettingsSelected+=()=>{if(_selectionLoading)InvalidatePendingInvocation();ShowSettings();};
        _radial.ActionSelected+=HandleAction;
        _radial.ActionHovered+=action=>{if(!_radialWorkspaceVisible&&_files.Count>0&&_lastPopupOutput==null&&!_jobs.Any(j=>!j.Finished&&j.PopupSession==_popupSession))_radial.CenterHint.Text=action.HasValue?ActionName(action.Value):global::Orbit.Core.Localization.L.Get("Text_B19EB1785C37");};
        PreviewDragOver+=DragOverFiles;PreviewDrop+=DropFiles;
        PreviewKeyDown+=KeyboardControl;
        Loaded+=OnLoaded;
        SizeChanged+=(_,_)=>RefreshPopupBackdrop();
        _popupDismissTimer.Tick+=(_,_)=>CheckPopupDismissal();
        Activated+=(_,_)=>ArmPopupDismissal();
        Deactivated+=(_,_)=>Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(CheckPopupDismissal));
        IsVisibleChanged+=(_,_)=>{if(IsVisible)ArmPopupDismissal();else{SuspendRadialWorkspace();StopPopupDismissal();}};
        StateChanged+=(_,_)=>{if(WindowState==WindowState.Minimized){SuspendRadialWorkspace();StopPopupDismissal();}else{if(_panel.Visibility!=Visibility.Visible)TryRestoreRadialWorkspace();ArmPopupDismissal();}};
        Closing+=(_,e)=>{if(!_closing){e.Cancel=true;HideToTray();}};
        MouseRightButtonUp+=(_,e)=>{if(_compact){if(_panel.Visibility==Visibility.Visible)ClosePanel();else HideToTray();e.Handled=true;}};
        ApplyGlassAppearance();InitializeLocalization();
        if(InputArguments(args).Any()&&!IsDiagnosticRun) {
            _popupAnchor=NativeScreen.GetCursorInvocation();_popupSession++;SetCompactMode(true);
        }
    }
    private static SolidColorBrush Ink(string s)=>UiThemeBrushes.Resolve(s);
    private static Button Btn(string text,RoutedEventHandler handler) {var b=new Button{Content=text,Margin=new Thickness(3,3,3,3)};b.Click+=handler;return b;}
    private static TextBlock Text(string text,int size=13,string color="#B5B5D0")=>new(){Text=text,FontSize=size,Foreground=Ink(color),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,5,0,7)};
    private static string ActionName(ImageAction a)=>a switch {
        ImageAction.DownloadAudio=>global::Orbit.Core.Localization.L.Get("Text_69904711DED6"),ImageAction.DownloadVideo=>global::Orbit.Core.Localization.L.Get("Text_050E9BC4694D"),
        ImageAction.Archive7z=>global::Orbit.Core.Localization.L.Get("Text_571F13BF0489"),ImageAction.Extract7z=>global::Orbit.Core.Localization.L.Get("Text_27F8FBB2D20C"),ImageAction.SevenZipList=>global::Orbit.Core.Localization.L.Get("Text_43F088E8D7A7"),
        ImageAction.SevenZipExtractSelected=>global::Orbit.Core.Localization.L.Get("Text_8E6361F1E2CA"),ImageAction.TarExtractSelected=>global::Orbit.Core.Localization.L.Get("Text_C33F192AD7B0"),
        ImageAction.ImageWatermark=>global::Orbit.Core.Localization.L.Get("Text_2F86E281ECE0"),
        ImageAction.AudioTags=>global::Orbit.Core.Localization.L.Get("Text_E96DF688DD94"),
        ImageAction.ArchiveTar=>global::Orbit.Core.Localization.L.Get("Text_4BE039315043"),ImageAction.ExtractTar=>global::Orbit.Core.Localization.L.Get("Text_485AE2DC1D37"),ImageAction.TarList=>global::Orbit.Core.Localization.L.Get("Text_334553B38AAC"),
        ImageAction.AudioTrackExtract=>global::Orbit.Core.Localization.L.Get("Text_FA1849E17A5B"),ImageAction.AudioCover=>global::Orbit.Core.Localization.L.Get("Text_E903BD6CE24C"),
        ImageAction.AudioNormalize=>global::Orbit.Core.Localization.L.Get("Text_9E34ACAA5368"),ImageAction.ImageChain=>global::Orbit.Core.Localization.L.Get("Text_8ED6B7086B36"),
        ImageAction.PdfEmbeddedImages=>global::Orbit.Core.Localization.L.Get("Text_B7BF29AC4363"),ImageAction.PdfOptimize=>global::Orbit.Core.Localization.L.Get("Text_1D5FC6C94768"),
        ImageAction.AudioExport=>global::Orbit.Core.Localization.L.Get("Text_B41D2EDFA4DE"),ImageAction.VideoFrames=>global::Orbit.Core.Localization.L.Get("Text_ACEB512C28A7"),
        ImageAction.PdfRotate=>global::Orbit.Core.Localization.L.Get("Text_8C6F88468799"),ImageAction.PdfSplit=>global::Orbit.Core.Localization.L.Get("Text_C77DC8EC63A1"),ImageAction.ZipExtractSelected=>global::Orbit.Core.Localization.L.Get("Text_6F7F48F0AC2E"),
        ImageAction.VideoGif=>global::Orbit.Core.Localization.L.Get("Text_17447D4759DA"),ImageAction.VideoExport=>global::Orbit.Core.Localization.L.Get("Text_3F01DBCFE9B5"),ImageAction.AudioAdjust=>global::Orbit.Core.Localization.L.Get("Text_0778DED80983"),
        ImageAction.ImagesToPdf=>global::Orbit.Core.Localization.L.Get("Text_59B928F728DE"),ImageAction.ContactSheet=>global::Orbit.Core.Localization.L.Get("Text_BB910CF02A32"),ImageAction.RemoveImageMetadata=>global::Orbit.Core.Localization.L.Get("Text_97469A5BB72B"),
        ImageAction.OcrText=>global::Orbit.Core.Localization.L.Get("Text_9B45DFE62C68"),ImageAction.VideoTrim=>global::Orbit.Core.Localization.L.Get("Text_A6AAFD249FE2"),ImageAction.AudioTrim=>global::Orbit.Core.Localization.L.Get("Text_9EF1B0F1709E"),ImageAction.ImageTransform=>global::Orbit.Core.Localization.L.Get("Text_BBBE7EFE973C"),
        ImageAction.Jpg=>global::Orbit.Core.Localization.L.Get("Text_25A8405C27D2"),ImageAction.Png=>global::Orbit.Core.Localization.L.Get("Text_A72C4B709951"),ImageAction.Bmp=>global::Orbit.Core.Localization.L.Get("Text_B6B198ECAC82"),ImageAction.Tiff=>global::Orbit.Core.Localization.L.Get("Text_836992CCD918"),ImageAction.Gif=>global::Orbit.Core.Localization.L.Get("Text_7DCC41FE9998"),ImageAction.Ico=>global::Orbit.Core.Localization.L.Get("Text_4496956019EE"),ImageAction.Svg=>global::Orbit.Core.Localization.L.Get("Text_B713B19E32CE"),ImageAction.WebP=>global::Orbit.Core.Localization.L.Get("Text_D650F3D53021"),ImageAction.Avif=>global::Orbit.Core.Localization.L.Get("Text_F089F0A2B161"),ImageAction.Resize=>global::Orbit.Core.Localization.L.Get("Text_3837BA14575C"),ImageAction.Crop=>global::Orbit.Core.Localization.L.Get("Text_1FE990F27562"),ImageAction.Compress=>global::Orbit.Core.Localization.L.Get("Text_99E06697F4C9"),
        ImageAction.VideoMp4=>global::Orbit.Core.Localization.L.Get("Text_D6EED8ED502C"),ImageAction.VideoWebm=>global::Orbit.Core.Localization.L.Get("Text_E159C317E850"),ImageAction.ExtractMp3=>global::Orbit.Core.Localization.L.Get("Text_4B3DA071A449"),ImageAction.VideoFrame=>global::Orbit.Core.Localization.L.Get("Text_B22B35FB7F30"),ImageAction.VideoCompress=>global::Orbit.Core.Localization.L.Get("Text_83263619573D"),
        ImageAction.AudioMp3=>global::Orbit.Core.Localization.L.Get("Text_381983CAAF3F"),ImageAction.AudioWav=>global::Orbit.Core.Localization.L.Get("Text_BE4B71DC6A23"),ImageAction.AudioFlac=>global::Orbit.Core.Localization.L.Get("Text_27DB2F62FA8F"),ImageAction.AudioCompress=>global::Orbit.Core.Localization.L.Get("Text_17048DF6AE00"),
        ImageAction.PdfPng=>global::Orbit.Core.Localization.L.Get("Text_2E2D6D868837"),ImageAction.PdfJpg=>global::Orbit.Core.Localization.L.Get("Text_801D2B160D3C"),ImageAction.PdfText=>global::Orbit.Core.Localization.L.Get("Text_4DF9BE0D3339"),
        ImageAction.ArchiveZip=>global::Orbit.Core.Localization.L.Get("Text_6A338F324967"),ImageAction.ExtractZip=>global::Orbit.Core.Localization.L.Get("Text_AC16FE4DCBC6"),ImageAction.ArchiveList=>global::Orbit.Core.Localization.L.Get("Text_844F16C32505"),ImageAction.FileHash=>"SHA-256",ImageAction.PdfMerge=>global::Orbit.Core.Localization.L.Get("Text_195C2182B05D"),ImageAction.PdfExtractPages=>global::Orbit.Core.Localization.L.Get("Text_2C00D29E7BBE"),ImageAction.Rename=>global::Orbit.Core.Localization.L.Get("Text_194306E20807"),ImageAction.MoreTools=>global::Orbit.Core.Localization.L.Get("Text_F44AC1FA3391"),_=>global::Orbit.Core.Localization.L.Get("Text_B36B5B02D98A")
    };
    private async void OnLoaded(object sender,RoutedEventArgs e)
    {
        ApplyGlassAppearance();
        SystemParameters.StaticPropertyChanged+=SystemAppearanceChanged;
        bool capture=IsDiagnosticRun;
        var personalization=LoadPersonalizationAsync();_ = TrackResultTask(personalization);await personalization;
        if(_closing)return;
        await LoadProductivityAsync();
        if(_closing)return;
        if(!capture) {
            _bridge=new WindowsBridge(this,ShowOrbit,Quit,InvokeAtCursor,_shortcut,ShowJobsFromTray,ToggleQueuePause,_queueRunControl.IsPaused);UpdateQueueControls();
            _inboxTimer.Tick+=CheckInbox;_inboxTimer.Start();
        }
        var paths=InputArguments(_args).ToArray();
        if(paths.Length>0){
            if(_compact&&!IsDiagnosticRun){int startupVersion=_invocationVersion;Hide();await PreparePopupBackdropAsync(_popupAnchor??NativeScreen.GetCursorInvocation());if(_closing||startupVersion!=_invocationVersion)return;Show();}
            await LoadFilesAsync(paths);
        }
        if(_compact&&_popupAnchor.HasValue)PlacePopup(new Size(_compactSize,_compactSize));
        ArmPopupDismissal();
        if(SystemParameters.ClientAreaAnimation)BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(180)));
        if(_args.Contains("--capture")) {
            await Task.Delay(450);UpdateLayout();
            var index=Array.IndexOf(_args,"--capture");var path=_args.Length>index+1?_args[index+1]:"orbit-preview.png";
            Capture(path);Quit();
        }
        if(_args.Contains("--ui-smoke"))await RunUiSmokeAsync();
        if(_args.Contains("--toolkit-smoke"))await RunToolkitSmokeAsync();
        if(_args.Contains("--popup-smoke"))await RunPopupSmokeAsync();
        if(_args.Contains("--glass-smoke"))await RunGlassSmokeAsync();
        if(_args.Contains("--themes-smoke"))await RunThemeSmokeAsync();
        if(_args.Contains("--selection-latency-smoke"))await RunSelectionLatencySmokeAsync();
        if(_args.Contains("--folder-smoke"))await RunFolderSmokeAsync();
        if(_args.Contains("--systems-smoke"))await RunSystemsSmokeAsync();
        if(_args.Contains("--editing-smoke"))await RunEditingSmokeAsync();
        if(_args.Contains("--productivity-smoke"))await RunProductivitySmokeAsync();
        if(_args.Contains("--results-smoke"))await RunResultsSmokeAsync();
        if(_args.Contains("--image-tools-smoke"))await RunImageToolsSmokeAsync();
        if(_args.Contains("--media-tools-smoke"))await RunMediaToolsSmokeAsync();
        if(_args.Contains("--personalization-smoke"))await RunPersonalizationSmokeAsync();
        if(_args.Contains("--document-tools-smoke"))await RunDocumentToolsSmokeAsync();
        if(_args.Contains("--workflows-smoke"))await RunWorkflowsSmokeAsync();
        if(_args.Contains("--pdf-utilities-smoke"))await RunPdfUtilitiesSmokeAsync();
        if(_args.Contains("--media-export-smoke"))await RunMediaExportSmokeAsync();
        if(_args.Contains("--image-editing-smoke"))await RunImageEditingSmokeAsync();
        if(_args.Contains("--media-tracks-smoke"))await RunMediaTracksSmokeAsync();
        if(_args.Contains("--archive-integrity-smoke"))await RunArchiveIntegritySmokeAsync();
        if(_args.Contains("--audio-metadata-smoke"))await RunAudioMetadataSmokeAsync();
        if(_args.Contains("--usability-smoke"))await RunUsabilitySmokeAsync();
        if(_args.Contains("--sevenzip-smoke"))await RunSevenZipSmokeAsync();
        if(_args.Contains("--sevenzip-password-smoke"))await RunSevenZipPasswordSmokeAsync();
        if(_args.Contains("--archive-selection-smoke"))await RunArchiveSelectionSmokeAsync();
        if(_args.Contains("--watermark-smoke"))await RunWatermarkSmokeAsync();
        if(_args.Contains("--queue-smoke"))await RunQueueSmokeAsync();
        if(_args.Contains("--download-smoke"))await RunDownloadSmokeAsync();
        if(_args.Contains("--download-flow-smoke"))await RunDownloadFlowSmokeAsync();
        if(_args.Contains("--radial-workspace-smoke"))await RunRadialWorkspaceSmokeAsync();
        if(_args.Contains("--trim-timeline-smoke"))await RunTrimTimelineSmokeAsync();
        if(_args.Contains("--standalone-smoke"))await RunStandaloneSmokeAsync();
        if(_args.Contains("--localization-smoke"))await RunLocalizationSmokeAsync();
        if(_args.Contains("--background")&&!IsDiagnosticRun&&paths.Length==0){HideToTray();BeginAnimation(OpacityProperty,null);Opacity=1;}
        if(ShouldShowWelcome())ShowWelcome(firstRun:true);
    }
    private void CheckInbox(object? sender,EventArgs e) {
        if(_popupInteractionDepth>0||Mouse.Captured!=null)return;
        var inbox=Path.Combine(Path.GetTempPath(),"OrbitToolkit-inbox");if(!Directory.Exists(inbox))return;
        foreach(var file in Directory.EnumerateFiles(inbox,"*.txt")) {try {var paths=File.ReadAllLines(file);File.Delete(file);if(paths.Length>0)_=OpenExplicitFilesAsync(paths);else ShowOrbit();} catch(IOException){} }
    }
    private static IEnumerable<string> InputArguments(string[] args) {for(int i=0;i<args.Length;i++){if(args[i] is "--capture" or "--toolkit-smoke"){i++;continue;}if(args[i].StartsWith("--",StringComparison.Ordinal))continue;if(File.Exists(args[i])||Directory.Exists(args[i]))yield return Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[i]));}}
    private void ShowOrbit(){InvalidatePendingInvocation();SetCompactMode(false);Show();WindowState=WindowState.Normal;if(!IsDiagnosticRun)Activate();ResumeDownloadWorkspace();if(_panel.Visibility!=Visibility.Visible)TryRestoreRadialWorkspace();}
    private void InvalidatePendingInvocation(){++_invocationVersion;++_loadVersion;_pendingPopupInvocation=0;_loadCancellation?.Cancel();_selectionLoading=false;}
    private async void InvokeAtCursor(HotkeyInvocation invocation) => await InvokeAtCursorCoreAsync(invocation);
    private async Task InvokeAtCursorCoreAsync(HotkeyInvocation invocation)
    {
        if(_closing||_popupInteractionDepth>0||Mouse.Captured!=null)return;
        var ownHandle=new WindowInteropHelper(this).Handle;
        if(_compact&&IsVisible&&invocation.ForegroundWindow==ownHandle) {
            ++_invocationVersion;++_loadVersion;_loadCancellation?.Cancel();_selectionLoading=false;HideToTray();return;
        }
        var explicitFiles=invocation.ForegroundWindow==ownHandle?_files.ToArray():Array.Empty<string>();
        int version=++_invocationVersion;
        _pendingPopupInvocation=version;StopPopupDismissal();
        ++_popupSession;_popupAnchor=invocation;++_loadVersion;_loadCancellation?.Cancel();ClearSelection();_selectionLoading=true;
        bool browser=IsBrowserInvocation(invocation.ForegroundWindow);
        var browserPending=browser?ReadBrowserUrlAsync(invocation.ForegroundWindow):Task.FromResult(new BrowserUrlResult(null,null));
        var pending=browser?Task.FromResult(new ExplorerSelectionResult(Array.Empty<string>(),null)):explicitFiles.Length>0?Task.FromResult(new ExplorerSelectionResult(explicitFiles,null)):ReadInvocationSelectionAsync(invocation.ForegroundWindow);
        bool wasVisible=IsVisible;
        if(wasVisible)Hide();
        ShowActivated=false;
        SetCompactMode(true);
        await PreparePopupBackdropAsync(invocation);
        if(version!=_invocationVersion||_closing)return;
        _radial.FileName.Text=browser?global::Orbit.Core.Localization.L.Get("Text_50FF0609E55A"):global::Orbit.Core.Localization.L.Get("Text_9B49438AF0C2");_radial.CenterHint.Text=global::Orbit.Core.Localization.L.Get("Text_7E4F3BAF839F");_radial.Metadata.Text="";
        Show();WindowState=WindowState.Normal;PlacePopup(new Size(_compactSize,_compactSize));
        // Keep Explorer foreground until its captured selection has passed the native identity checks.
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Render);
        Task contextPending=browser?browserPending:pending;
        var completed=await Task.WhenAny(contextPending,Task.Delay(1800));
        var selection=completed==pending?await pending:new ExplorerSelectionResult(Array.Empty<string>(),global::Orbit.Core.Localization.L.Get("Text_04BDD411964A"));
        var browserResult=browser&&completed==browserPending?await browserPending:new BrowserUrlResult(null,global::Orbit.Core.Localization.L.Get("Text_CE11CCD4340B"));
        if(version!=_invocationVersion||_closing)return;
        _pendingPopupInvocation=0;
        var foreground=InvocationForeground();
        if(foreground!=invocation.ForegroundWindow&&foreground!=ownHandle){HideToTray();ShowActivated=true;return;}
        if(!IsDiagnosticRun)Activate();ShowActivated=true;ArmPopupDismissal();
        if(browser){PublishBrowserContext(browserResult);return;}
        if(selection.Files.Count==0) {
            _selectionLoading=false;_radial.FileName.Text=global::Orbit.Core.Localization.L.Get("Text_8D9E0F78B4C7");_radial.CenterHint.Text=global::Orbit.Core.Localization.L.Get("Text_356F285F615A");_radial.Metadata.Text=ShortcutDisplay;
            Toast(selection.Error??global::Orbit.Core.Localization.L.Get("Text_90CFD5ACC1AB"));return;
        }
        await LoadFilesAsync(selection.Files);
    }
    private static Task<ExplorerSelectionResult> ReadExplorerSelectionAsync(IntPtr foreground)
    {
        var completion=new TaskCompletionSource<ExplorerSelectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try {completion.TrySetResult(ExplorerSelection.ReadSelectedFiles(foreground));}catch(Exception ex){App.Log(ex);completion.TrySetResult(new ExplorerSelectionResult(Array.Empty<string>(),global::Orbit.Core.Localization.L.Get("Text_D6FF02DEE8EC")));}}){IsBackground=true,Name="Arclo Explorer selection"};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return completion.Task;
    }
    private async Task OpenExplicitFilesAsync(IEnumerable<string> paths)
    {
        int version=++_invocationVersion;_pendingPopupInvocation=0;++_popupSession;_popupAnchor=NativeScreen.GetCursorInvocation();
        if(IsVisible)Hide();ClearSelection();ShowActivated=false;SetCompactMode(true);
        await PreparePopupBackdropAsync(_popupAnchor.Value);
        if(_closing||version!=_invocationVersion)return;
        Show();WindowState=WindowState.Normal;PlacePopup(new Size(_compactSize,_compactSize));ShowActivated=true;if(!IsDiagnosticRun)Activate();await LoadFilesAsync(paths);
    }
    private void ClearSelection()
    {
        ResetRadialWorkspace();
        _resumeDownloadWorkspaceJob=null;
        _browserContext=false;_browserUrl=null;
        _selectionPreviewReady=false;
        ++_renamePreviewVersion;
        ClosePanel();
        ++_toastVersion;_toast.Visibility=Visibility.Collapsed;
        _files=new();_metadata=null;_preview=null;_lastPopupOutput=null;_selectionMetadata=Array.Empty<ToolkitMetadata>();
        _radial.Preview.Source=_emptyPreview;_radial.FileName.Text=global::Orbit.Core.Localization.L.Get("Text_EB04A911B31C");_radial.CenterHint.Text=global::Orbit.Core.Localization.L.Get("Text_1B7751B7F1DE");_radial.Metadata.Text=global::Orbit.Core.Localization.L.Get("Text_59EA9C226FAB");
        _radial.CenterCard.ToolTip=global::Orbit.Core.Localization.L.Get("Text_E3663341C82C");_radial.SetActions(ContextActions(ToolkitKind.Image,true));_radial.SetAvailable(false);
    }
    private void HideToTray(){PreserveDownloadWorkspaceForHide();SuspendRadialWorkspace();StopComparison();StopPopupDismissal();InvalidatePendingInvocation();if(_bridge?.TrayAvailable==true)Hide();else WindowState=WindowState.Minimized;}
    private async void Quit() {
        if(_closing)return;_closing=true;SuspendRadialWorkspace();SystemParameters.StaticPropertyChanged-=SystemAppearanceChanged;IsEnabled=false;_inboxTimer.Stop();StopPopupDismissal();_loadCancellation?.Cancel();
        foreach(var j in _jobs)j.Cancellation.Cancel();_renameCancellation?.Cancel();_bridge?.Dispose();_browserReader.Dispose();_backdrop.Dispose();_popupSampler.Dispose();_popupFrameCancellation?.Cancel();_resultToolsCancellation.Cancel();StopComparison();
        try {await Task.WhenAll(_jobTasks.Concat(_loadTasks).Concat(_resultToolTasks).Append(_renameWork));}catch(Exception ex){App.Log(ex);}
        await FlushHistoryAsync();
        Close();Application.Current.Shutdown();
    }
    private void ChooseFiles() {
        InvalidatePendingInvocation();
        var d=new OpenFileDialog{Title=global::Orbit.Core.Localization.L.Get("Text_635FB0F9249B"),Multiselect=true,Filter=global::Orbit.Core.Localization.L.Get("Text_B5309D63EE25")};
        if(WithPopupInteraction(()=>d.ShowDialog(this))==true){++_popupSession;_=LoadFilesAsync(d.FileNames);}
    }
    private Task<bool> LoadFilesAsync(IEnumerable<string> paths,bool preservePanelChanges=false) {if(_closing)return Task.FromResult(false);_loadTasks.RemoveAll(t=>t.IsCompleted);var task=LoadFilesCoreAsync(paths,preservePanelChanges);_loadTask=task;_loadTasks.Add(task);return task;}
    private void ChooseFolders() {
        InvalidatePendingInvocation();
        var dialog=new OpenFolderDialog{Title=global::Orbit.Core.Localization.L.Get("Text_1AD7EE7C16A7"),Multiselect=true};
        if(WithPopupInteraction(()=>dialog.ShowDialog(this))==true){++_popupSession;_=LoadFilesAsync(dialog.FolderNames);}
    }
    private async Task<bool> LoadFilesCoreAsync(IEnumerable<string> paths,bool preservePanelChanges)
    {
        var requested=paths.ToArray();
        if(requested.Length==0)return false;
        int version=++_loadVersion;_loadCancellation?.Cancel();_loadCancellation=new CancellationTokenSource();var ct=_loadCancellation.Token;
        ClearSelection();_selectionLoading=true;int panelVersion=_comparisonVersion;
        _status.Text=global::Orbit.Core.Localization.L.Get("Text_B0E7D4868964");_radial.FileName.Text=requested.Length==1?Path.GetFileName(requested[0]):global::Orbit.Core.Localization.L.Format("Text_5CBEF238F2EA",(object?)(requested.Length));_radial.CenterHint.Text=global::Orbit.Core.Localization.L.Get("Text_62ED32EB225C");_radial.Metadata.Text="";
        try {
            var selection=await Task.Run(()=>SelectedPathPolicy.NormalizeExisting(requested.Select(Path.GetFullPath)),ct);
            if(version!=_loadVersion||_closing)return false;
            if(selection.RejectedCount>0||!selection.Success)throw new InvalidOperationException(selection.Error??global::Orbit.Core.Localization.L.Get("Text_AAB54222FF3B"));
            var files=selection.Paths.ToList();
            _radial.FileName.Text=files.Count==1?Path.GetFileName(files[0]):global::Orbit.Core.Localization.L.Format("Text_5CBEF238F2EA",(object?)(files.Count));
            _radial.CenterHint.Text=global::Orbit.Core.Localization.L.Get("Text_B0E7D4868964");_radial.Metadata.Text="";
            var previewTask=LoadSelectionPreviewAsync(files[0],version,ct);_loadTasks.Add(previewTask);
            var metadata=await InspectSelectionAsync(files[0],ct);
            var members=new List<ToolkitMetadata>{metadata};
            foreach(var file in files.Skip(1))members.Add(await InspectSelectionAsync(file,ct));
            if(version!=_loadVersion||_closing||preservePanelChanges&&panelVersion!=_comparisonVersion){await ObserveSelectionPreviewAsync(previewTask);return false;}
            ImageSource image=_preview??FilePreview(metadata);
            _files=files;_metadata=metadata;_preview=image;_selectionMetadata=members;
            _selectionKind=members.Select(m=>m.Kind).Distinct().Count()==1?metadata.Kind:ToolkitKind.Mixed;
            _radial.SetActions(ContextActions(_selectionKind,members.All(m=>m.HasAudio)));
            bool containsFolders=members.Any(m=>m.Kind==ToolkitKind.Folder);
            _radial.Preview.Source=image;_radial.FileName.Text=files.Count==1?Path.GetFileName(files[0]):global::Orbit.Core.Localization.L.Format("Text_5CBEF238F2EA",(object?)(files.Count));
            _radial.CenterHint.Text=global::Orbit.Core.Localization.L.Get("Text_B19EB1785C37");_radial.Metadata.Text=files.Count==1?ShortMetadata(metadata):containsFolders?global::Orbit.Core.Localization.L.Get("Text_06D95661FA47"):FormatBytes(members.Sum(m=>m.Bytes))+" · "+(_selectionKind==ToolkitKind.Mixed?global::Orbit.Core.Localization.L.Get("Text_D5D52DE6FD70"):metadata.Format);
            _radial.SetAvailable(true);UpdatePreviewActionAvailability();_status.Text=files.Count==1?Path.GetFileName(files[0]):global::Orbit.Core.Localization.L.Format("Text_54F49F23798C",(object?)(files.Count));
            _selectionInfo.Text=containsFolders?global::Orbit.Core.Localization.L.Get("Text_D41B859C0E85"):files.Count==1?metadata.Description+" · "+FormatBytes(metadata.Bytes):global::Orbit.Core.Localization.L.Format("Text_75F4A132E1D2",(object?)(files.Count))+FormatBytes(members.Sum(m=>m.Bytes))+global::Orbit.Core.Localization.L.Get("Text_503B4E98893B");
            ClosePanel();_selectionLoading=false;
            await ObserveSelectionPreviewAsync(previewTask);
            return version==_loadVersion&&!_closing;
        } catch(OperationCanceledException ex) {if(IsDiagnosticRun)App.Log(new InvalidOperationException($"Diagnostic load cancelled: requested={version}; current={_loadVersion}; closing={_closing}; window={WindowState}",ex));}
        catch(Exception ex) {if(IsDiagnosticRun)App.Log(ex);if(version==_loadVersion){_loadCancellation?.Cancel();ClearSelection();_status.Text=global::Orbit.Core.Localization.L.Get("Text_D9061A9464C9");Toast(ex.Message);}}
        finally {if(version==_loadVersion)_selectionLoading=false;}
        return false;
    }
    private void DragOverFiles(object sender,DragEventArgs e)
    {
        _lastPopupDragOver=_popupClock.Elapsed;
        e.Effects=e.Data.GetDataPresent(DataFormats.FileDrop)&&(e.AllowedEffects&DragDropEffects.Copy)!=0?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;
        if(e.Effects==DragDropEffects.Copy)_radial.CenterHint.Text=global::Orbit.Core.Localization.L.Get("Text_4440D7D34758");
    }
    private async void DropFiles(object sender,DragEventArgs e)
    {
        _lastPopupDragOver=TimeSpan.MinValue;
        e.Handled=true;e.Effects=DragDropEffects.None;
        if(!e.Data.GetDataPresent(DataFormats.FileDrop)||(e.AllowedEffects&DragDropEffects.Copy)==0)return;
        if(e.Data.GetData(DataFormats.FileDrop) is not string[] files)return;
        InvalidatePendingInvocation();++_popupSession;
        // The app only returns Copy to OLE. It never requests or performs a source move.
        e.Effects=DragDropEffects.Copy;
        var action=_radialWorkspaceVisible?null:DropActionAt(e.GetPosition(_radial));
        if(await LoadFilesAsync(files)&&action.HasValue)HandleAction(action.Value);
    }
    private ImageAction? DropActionAt(Point point)=>_radialView.IsVisible?_radial.ActionAt(point):null;
    private void KeyboardControl(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.V&&Keyboard.Modifiers==ModifierKeys.Control&&!IsClipboardTextTarget()){e.Handled=true;_radialWorkspaceTask=TrackResultTask(PasteIntoRadialAsync());}
        else if(e.Key==Key.Escape){if(_panel.Visibility==Visibility.Visible)ClosePanel();else if(_radialWorkspaceVisible&&_radialConversionMenu)RadialCenterSelected();else if(_compact)HideToTray();else {_radial.CenterHint.Text=_files.Count>0?global::Orbit.Core.Localization.L.Get("Text_B19EB1785C37"):global::Orbit.Core.Localization.L.Get("Text_3DDFE21A9EE0");}e.Handled=true;}
        else if((e.Key==Key.Right||e.Key==Key.Left)&&!_panel.IsKeyboardFocusWithin){int count=_radial.ActionCount;_keyboardIndex=(_keyboardIndex+(e.Key==Key.Right?1:count-1)+count)%count;_radial.SelectByKeyboard(_keyboardIndex);e.Handled=true;}
        else if(e.Key==Key.O&&Keyboard.Modifiers==ModifierKeys.Control){ChooseFiles();e.Handled=true;}
        else if(e.Key==Key.O&&Keyboard.Modifiers==(ModifierKeys.Control|ModifierKeys.Shift)){ChooseFolders();e.Handled=true;}
        else if(e.Key==Key.K&&Keyboard.Modifiers==ModifierKeys.Control){ShowMoreTools();e.Handled=true;}
    }
    private void OpenPanel(string title,string subtitle) {
        _languageSettingsVisible=false;
        SuspendRadialWorkspace();StopComparison();_personalizationStateChanged=null;_jobsVisible=false;_recentVisible=false;
        if(_compact){_radialView.Visibility=Visibility.Collapsed;_radialColumn.Width=new GridLength(0);_sideColumn.Width=new GridLength(1,GridUnitType.Star);_panel.Margin=new Thickness(12,42,12,12);PlacePopup(new Size(_compactSize,600));}
        else _sideColumn.Width=new GridLength(360);
        _panel.Visibility=Visibility.Visible;_panelContent.Children.Clear();
        if(_panel.Child is ScrollViewer panelScroll)panelScroll.ScrollToTop();
        ArmPopupDismissal();
        var line=new DockPanel();var close=Btn("‹",(_,_)=>ClosePanel());close.ToolTip=global::Orbit.Core.Localization.L.Get("Text_6C7992DCA6BE");close.Padding=new Thickness(7,2,7,2);DockPanel.SetDock(close,Dock.Right);line.Children.Add(close);line.Children.Add(Text(title,21,"#F0EDFF"));_panelContent.Children.Add(line);
        _panelContent.Children.Add(Text(subtitle,12,"#9293B7"));
    }
    private void ClosePanel(){_languageSettingsVisible=false;StopComparison();_personalizationStateChanged=null;_jobsVisible=false;_recentVisible=false;_panel.Visibility=Visibility.Collapsed;_sideColumn.Width=new GridLength(0);_radialColumn.Width=new GridLength(1,GridUnitType.Star);_radialView.Visibility=Visibility.Visible;_panel.Margin=new Thickness(12);if(_compact)PlacePopup(new Size(_compactSize,_compactSize));TryRestoreRadialWorkspace();ArmPopupDismissal();}
    private void HandleAction(ImageAction action) {
        if(TryRadialFormat(action))return;
        if(!_radial.Children.OfType<SectorButton>().Any(b=>b.Action==action))return;
        DispatchAction(action);
    }
    private void DispatchAction(ImageAction action) {
        if(action==ImageAction.MoreTools){ShowMoreTools();return;}
        if(action is ImageAction.DownloadAudio or ImageAction.DownloadVideo){StartBrowserDownload(action==ImageAction.DownloadAudio?DownloadMode.AudioMp3:DownloadMode.VideoMp4);return;}
        if(_selectionLoading)return;
        if(_files.Count==0){if(_compact)Toast(global::Orbit.Core.Localization.L.Get("Text_28B5F4BFC8B2"));else ChooseFiles();return;}
        if(action==ImageAction.Rename){ShowRename();return;}
        if(action==ImageAction.PdfMerge){ShowPdfMerge();return;}
        if(action==ImageAction.PdfExtractPages){ShowPdfPages();return;}
        if(action==ImageAction.PdfRotate){ShowPdfRotation();return;}
        if(action==ImageAction.PdfSplit){ShowPdfSplit();return;}
        if(action is ImageAction.PdfText or ImageAction.PdfEmbeddedImages or ImageAction.PdfOptimize){ShowPdfUtility(action);return;}
        if(action==ImageAction.ZipExtractSelected){ShowZipBrowser();return;}
        if(action==ImageAction.OcrText){ShowOcr();return;}
        if(action==ImageAction.ImageTransform){ShowImageTransform();return;}
        if(action is ImageAction.ImagesToPdf or ImageAction.ContactSheet){ShowImageCollection(action);return;}
        if(action==ImageAction.RemoveImageMetadata){ShowMetadataCleaner();return;}
        if(action==ImageAction.VideoGif){ShowVideoGif();return;}
        if(action==ImageAction.VideoExport){ShowVideoExport();return;}
        if(action==ImageAction.AudioAdjust){ShowAudioAdjust();return;}
        if(action==ImageAction.AudioNormalize){ShowAudioNormalize();return;}
        if(action==ImageAction.AudioExport){ShowAudioExport();return;}
        if(action==ImageAction.VideoFrames){ShowVideoFrames();return;}
        if(action==ImageAction.AudioTrackExtract){ShowMediaTracks(true);return;}
        if(action==ImageAction.AudioCover){ShowAudioCover();return;}
        if(action==ImageAction.AudioTags){ShowAudioTags();return;}
        if(action==ImageAction.ArchiveTar){ShowTarCreate();return;}
        if(action==ImageAction.Archive7z){ShowSevenZipCreate();return;}
        if(action==ImageAction.SevenZipExtractSelected){ShowSevenZipBrowser();return;}
        if(action==ImageAction.TarExtractSelected){ShowTarBrowser();return;}
        if(action==ImageAction.ImageWatermark){ShowWatermarkEditor();return;}
        if(action is ImageAction.Extract7z or ImageAction.SevenZipList){ShowSevenZipAction(action);return;}
        if(action is ImageAction.ExtractTar or ImageAction.TarList){ShowTarAction(action);return;}
        if(action==ImageAction.ImageChain){ShowImageChains();return;}
        if(action is ImageAction.VideoTrim or ImageAction.AudioTrim){ShowMediaTrim(action);return;}
        if(action==ImageAction.FileInfo){ShowFileInfo();return;}
        if(_compact&&action is not(ImageAction.Crop or ImageAction.Resize)) {
            _lastPopupOutput=null;
            var options=new ImageJobOptions{Action=action,Quality=action==ImageAction.Avif?65:85,OutputDirectory=_outputDirectory};
            QueueSelected(options);
            return;
        }
        if(action is ImageAction.Png or ImageAction.Bmp or ImageAction.Tiff or ImageAction.Gif or ImageAction.Ico or ImageAction.Svg){ShowAction(action);return;}
        if((int)action>(int)ImageAction.Compress){ShowToolkitAction(action);return;}
        ShowAction(action);
    }
    private IReadOnlyList<RadialAction> ContextActions(ToolkitKind kind,bool allHaveAudio)
        =>_radialLayout.Apply(LayoutContext(kind),DefaultContextActions(kind,allHaveAudio),
            a=>kind==ToolkitKind.Video&&!allHaveAudio&&a.Action==ImageAction.FileInfo?ImageAction.ExtractMp3:a.Action);
    private IReadOnlyList<RadialAction> DefaultContextActions(ToolkitKind kind,bool allHaveAudio)
    {
        static RadialAction A(ImageAction action,string label,string help)=>new(action,label,help);
        return kind switch {
            ToolkitKind.Image=>new[]{A(ImageAction.Jpg,"JPG",global::Orbit.Core.Localization.L.Get("Text_B8ABDBEA46BB")),A(ImageAction.Png,"PNG",global::Orbit.Core.Localization.L.Get("Text_77DCE0C3031B")),A(ImageAction.Crop,global::Orbit.Core.Localization.L.Get("Text_1FE990F27562"),global::Orbit.Core.Localization.L.Get("Text_AEE22D5289F8")),A(ImageAction.Compress,global::Orbit.Core.Localization.L.Get("Text_A3A634E3ED93"),global::Orbit.Core.Localization.L.Get("Text_87DC87604061")),A(ImageAction.Resize,global::Orbit.Core.Localization.L.Get("Text_572095D4A787"),global::Orbit.Core.Localization.L.Get("Text_C26BC4F49248")),A(ImageAction.MoreTools,global::Orbit.Core.Localization.L.Get("Text_2071FB990504"),global::Orbit.Core.Localization.L.Get("Text_E5152A08207C"))},
            ToolkitKind.Video=>new[]{A(ImageAction.VideoMp4,"MP4","H.264 + AAC"),A(ImageAction.VideoWebm,"WebM","VP9 + Opus"),allHaveAudio?A(ImageAction.ExtractMp3,global::Orbit.Core.Localization.L.Get("Text_997CA89C20F2"),global::Orbit.Core.Localization.L.Get("Text_01C92F6A58C5")):A(ImageAction.FileInfo,global::Orbit.Core.Localization.L.Get("Text_BD38D43CF415"),global::Orbit.Core.Localization.L.Get("Text_2EF16B159E96")),A(ImageAction.VideoFrame,global::Orbit.Core.Localization.L.Get("Text_E0569CFCABE6"),global::Orbit.Core.Localization.L.Get("Text_05C913BE3B1A")),A(ImageAction.VideoCompress,global::Orbit.Core.Localization.L.Get("Text_A3A634E3ED93"),"H.264 · CRF28 · AAC128"),A(ImageAction.ArchiveZip,"ZIP",global::Orbit.Core.Localization.L.Get("Text_613A35C2DDB2"))},
            ToolkitKind.Audio=>new[]{A(ImageAction.AudioMp3,"MP3",global::Orbit.Core.Localization.L.Get("Text_F36447AEC31A")),A(ImageAction.AudioWav,"WAV","PCM WAV"),A(ImageAction.AudioFlac,"FLAC",global::Orbit.Core.Localization.L.Get("Text_B7AFFC3E8EF6")),A(ImageAction.AudioCompress,"MP3 128",global::Orbit.Core.Localization.L.Get("Text_3038818FB75F")),A(ImageAction.ArchiveZip,"ZIP",global::Orbit.Core.Localization.L.Get("Text_613A35C2DDB2")),A(ImageAction.FileInfo,global::Orbit.Core.Localization.L.Get("Text_BD38D43CF415"),global::Orbit.Core.Localization.L.Get("Text_F58611FA8E30"))},
            ToolkitKind.Pdf=>PdfActions(),
            ToolkitKind.Zip=>new[]{A(ImageAction.ExtractZip,global::Orbit.Core.Localization.L.Get("Text_F754A3CE0FDD"),global::Orbit.Core.Localization.L.Get("Text_016AE87A1B22")),A(ImageAction.ArchiveList,global::Orbit.Core.Localization.L.Get("Text_93FD1E0F9FEB"),global::Orbit.Core.Localization.L.Get("Text_5A04C4A649DD")),A(ImageAction.FileHash,"SHA-256",global::Orbit.Core.Localization.L.Get("Text_60736AAA0B11")),A(ImageAction.FileInfo,global::Orbit.Core.Localization.L.Get("Text_BD38D43CF415"),global::Orbit.Core.Localization.L.Get("Text_53B1A2A4487B"))},
            ToolkitKind.Tar=>new[]{A(ImageAction.ExtractTar,global::Orbit.Core.Localization.L.Get("Text_F754A3CE0FDD"),global::Orbit.Core.Localization.L.Get("Text_558BDD729980")),A(ImageAction.TarList,global::Orbit.Core.Localization.L.Get("Text_93FD1E0F9FEB"),global::Orbit.Core.Localization.L.Get("Text_D0B2713B1417")),A(ImageAction.FileHash,"SHA-256",global::Orbit.Core.Localization.L.Get("Text_34A976EA1CFE")),A(ImageAction.FileInfo,global::Orbit.Core.Localization.L.Get("Text_BD38D43CF415"),global::Orbit.Core.Localization.L.Get("Text_2335A28457B8"))},
            ToolkitKind.SevenZip=>new[]{A(ImageAction.Extract7z,global::Orbit.Core.Localization.L.Get("Text_F754A3CE0FDD"),global::Orbit.Core.Localization.L.Get("Text_D1BC5ECEBA85")),A(ImageAction.SevenZipList,global::Orbit.Core.Localization.L.Get("Text_93FD1E0F9FEB"),global::Orbit.Core.Localization.L.Get("Text_C11F7DA14745")),A(ImageAction.FileHash,"SHA-256",global::Orbit.Core.Localization.L.Get("Text_34A976EA1CFE")),A(ImageAction.FileInfo,global::Orbit.Core.Localization.L.Get("Text_BD38D43CF415"),global::Orbit.Core.Localization.L.Get("Text_BDF2F67FE2FB"))},
            ToolkitKind.Folder=>new[]{A(ImageAction.ArchiveZip,"ZIP",global::Orbit.Core.Localization.L.Get("Text_256876B52D13")),A(ImageAction.FileHash,"SHA-256",global::Orbit.Core.Localization.L.Get("Text_F8AFDB280DDC")),A(ImageAction.FileInfo,global::Orbit.Core.Localization.L.Get("Text_BD38D43CF415"),global::Orbit.Core.Localization.L.Get("Text_A635BA6BF876"))},
            _=>new[]{A(ImageAction.ArchiveZip,"ZIP",global::Orbit.Core.Localization.L.Get("Text_B77B4613AB3B")),A(ImageAction.FileHash,"SHA-256",global::Orbit.Core.Localization.L.Get("Text_06A8D16F6EF7")),A(ImageAction.FileInfo,global::Orbit.Core.Localization.L.Get("Text_BD38D43CF415"),global::Orbit.Core.Localization.L.Get("Text_501852E66AAF"))}
        };
    }
    private IReadOnlyList<RadialAction> PdfActions()
    {
        return new[]{new RadialAction(ImageAction.PdfPng,"PNG",global::Orbit.Core.Localization.L.Get("Text_3149110F7598")),new(ImageAction.PdfJpg,"JPG",global::Orbit.Core.Localization.L.Get("Text_3149110F7598")),new(ImageAction.PdfExtractPages,global::Orbit.Core.Localization.L.Get("Text_9D8A501639D6"),global::Orbit.Core.Localization.L.Get("Text_7950B444B8E6")),new(ImageAction.PdfMerge,global::Orbit.Core.Localization.L.Get("Text_8F27F05CA87A"),global::Orbit.Core.Localization.L.Get("Text_681D68D405B4")),new(ImageAction.ArchiveZip,"ZIP",global::Orbit.Core.Localization.L.Get("Text_49A13151E8D4")),new(ImageAction.MoreTools,global::Orbit.Core.Localization.L.Get("Text_2071FB990504"),global::Orbit.Core.Localization.L.Get("Text_0782FBCA3A23"))};
    }
    private static string ShortMetadata(ToolkitMetadata metadata)=>metadata.Kind switch {
        ToolkitKind.Image=>$"{metadata.Width} × {metadata.Height} · {FormatBytes(metadata.Bytes)}",
        ToolkitKind.Video=>metadata.DurationDisplay+" · "+FormatBytes(metadata.Bytes),
        ToolkitKind.Audio=>metadata.DurationDisplay+" · "+FormatBytes(metadata.Bytes),
        ToolkitKind.Pdf=>global::Orbit.Core.Localization.L.Format("Text_3D0E62FF95EA",(object?)(metadata.Pages),(object?)(FormatBytes(metadata.Bytes))),ToolkitKind.Zip or ToolkitKind.Tar or ToolkitKind.SevenZip=>global::Orbit.Core.Localization.L.Format("Text_A7497F12733E",(object?)(metadata.Entries),(object?)(FormatBytes(metadata.Bytes))),
        ToolkitKind.Folder=>global::Orbit.Core.Localization.L.Get("Text_07D01FB0AE92"),
        _=>metadata.Format+" · "+FormatBytes(metadata.Bytes)
    };
    private static ImageSource FilePreview(ToolkitMetadata metadata)
    {
        if(metadata.Kind==ToolkitKind.Folder){
            var folder=new DrawingGroup();using(var dc=folder.Open()){
                dc.DrawGeometry(RadialMenu.Brush("#D5DDE6"),new Pen(Ink("#E8E4FF"),1.3),Geometry.Parse("M 5,23 L 5,15 Q 5,10 11,10 L 32,10 Q 35,10 39,15 L 44,22 L 72,22 Q 78,22 78,28 L 78,83 Q 78,90 70,90 L 11,90 Q 5,90 5,83 Z"));
                dc.DrawGeometry(RadialMenu.Brush("#E7ECF2"),new Pen(Ink("#B9C4F5"),1),Geometry.Parse("M 5,33 L 73,33 Q 79,33 78,39 L 73,85 Q 72,90 67,90 L 11,90 Q 5,90 5,84 Z"));
                dc.DrawLine(new Pen(Ink("#A9EAF2FF"),1),new Point(12,37),new Point(69,37));
            }folder.Freeze();return new DrawingImage(folder);
        }
        var drawing=new DrawingGroup();using(var d=drawing.Open()) {
            d.DrawGeometry(RadialMenu.Brush("#DDE4EC"),new Pen(Ink("#B8A6FF"),1.2),Geometry.Parse("M 8,3 L 57,3 78,24 78,91 Q 78,98 71,98 L 8,98 Q 2,98 2,91 L 2,10 Q 2,3 8,3 Z"));
            d.DrawGeometry(RadialMenu.Brush("#BBC8D6"),null,Geometry.Parse("M 57,3 L 57,24 78,24 Z"));
            var label=metadata.Kind==ToolkitKind.Audio?"♫":metadata.Format.Length>8?metadata.Format[..8]:metadata.Format;
            var text=new FormattedText(label,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),metadata.Kind==ToolkitKind.Audio?38:17,RadialMenu.Brush("#435166"),1);
            d.DrawText(text,new Point(40-text.Width/2,47));
        }drawing.Freeze();return new DrawingImage(drawing);
    }
    private static bool IsMediaAction(ImageAction action)=>action is ImageAction.ImageWatermark or ImageAction.AudioTags or ImageAction.AudioTrackExtract or ImageAction.AudioCover or ImageAction.AudioExport or ImageAction.VideoFrames or ImageAction.AudioNormalize or ImageAction.ImageChain or ImageAction.VideoMp4 or ImageAction.VideoWebm or ImageAction.ExtractMp3 or ImageAction.VideoFrame or ImageAction.VideoCompress or ImageAction.AudioMp3 or ImageAction.AudioWav or ImageAction.AudioFlac or ImageAction.AudioCompress or ImageAction.VideoTrim or ImageAction.AudioTrim or ImageAction.VideoGif or ImageAction.VideoExport or ImageAction.AudioAdjust;
    private void ShowFileInfo()
    {
        bool folders=_selectionMetadata.Any(m=>m.Kind==ToolkitKind.Folder);
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_BD38D43CF415"),global::Orbit.Core.Localization.L.Format("Text_5CBEF238F2EA",(object?)(_files.Count))+(folders?global::Orbit.Core.Localization.L.Get("Text_958DB6DE7644"):" · "+FormatBytes(_selectionMetadata.Sum(m=>m.Bytes))));
        foreach(var metadata in _selectionMetadata.Take(30)) {
            _panelContent.Children.Add(Text(Path.GetFileName(metadata.Path),15,"#ECE5FF"));_panelContent.Children.Add(Text(metadata.Description,12));
            _panelContent.Children.Add(Text(metadata.Kind==ToolkitKind.Folder?global::Orbit.Core.Localization.L.Get("Text_AF5F02B45212"):FormatBytes(metadata.Bytes),12));_panelContent.Children.Add(Text(metadata.Path,11,"#858BAB"));
        }
        _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_FDEF576093E1"),(_,_)=>{QueueSelected(new ImageJobOptions{Action=ImageAction.FileHash,OutputDirectory=_outputDirectory});if(_compact)ClosePanel();else ShowJobs();}));
        _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_6A338F324967"),(_,_)=>{QueueSelected(new ImageJobOptions{Action=ImageAction.ArchiveZip,OutputDirectory=_outputDirectory});if(_compact)ClosePanel();else ShowJobs();}));
    }
    private void ShowToolkitAction(ImageAction action)
    {
        OpenPanel(ActionName(action),_files.Count==1?Path.GetFileName(_files[0]):global::Orbit.Core.Localization.L.Format("Text_FFB793B7F6C5",(object?)(_files.Count)));
        _panelContent.Children.Add(Text(action switch {
            ImageAction.VideoMp4=>global::Orbit.Core.Localization.L.Get("Text_3928DF57D0AF"),ImageAction.VideoWebm=>global::Orbit.Core.Localization.L.Get("Text_E086E78A3167"),
            ImageAction.ExtractMp3=>global::Orbit.Core.Localization.L.Get("Text_7A6CB0B31FD5"),ImageAction.VideoFrame=>global::Orbit.Core.Localization.L.Get("Text_2057225AA684"),
            ImageAction.VideoCompress=>global::Orbit.Core.Localization.L.Get("Text_75D58629F663"),
            ImageAction.AudioWav=>global::Orbit.Core.Localization.L.Get("Text_BCF47C04D296"),ImageAction.AudioFlac=>global::Orbit.Core.Localization.L.Get("Text_2A9E4C7EDE4E"),
            ImageAction.AudioCompress=>global::Orbit.Core.Localization.L.Get("Text_AC671E75AA8D"),
            ImageAction.PdfPng or ImageAction.PdfJpg=>global::Orbit.Core.Localization.L.Get("Text_E1957F382AE4"),ImageAction.PdfText=>global::Orbit.Core.Localization.L.Get("Text_8ED4D2165DEE"),
            ImageAction.ArchiveZip=>global::Orbit.Core.Localization.L.Get("Text_10D6143654FA"),ImageAction.ExtractZip=>global::Orbit.Core.Localization.L.Get("Text_3E5BBACBBE1D"),
            ImageAction.ArchiveList=>global::Orbit.Core.Localization.L.Get("Text_A05A2917E851"),ImageAction.FileHash=>global::Orbit.Core.Localization.L.Get("Text_12C0CF9E844F"),_=>global::Orbit.Core.Localization.L.Get("Text_BE5FED988702")
        },13));
        var bitrate=new ComboBox{ItemsSource=new[]{128,192,256,320},SelectedItem=192,Margin=new Thickness(0,3,0,15)};
        var dpi=new ComboBox{ItemsSource=new[]{96,150,200,300},SelectedItem=150,Margin=new Thickness(0,3,0,15)};
        var crf=new Slider{Minimum=16,Maximum=36,Value=24,TickFrequency=1,IsSnapToTickEnabled=true};
        if(action is ImageAction.AudioMp3 or ImageAction.ExtractMp3){_panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_6DA541E41F56")));_panelContent.Children.Add(bitrate);}
        if(action is ImageAction.PdfPng or ImageAction.PdfJpg){_panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_0ACF7FC34B48")));_panelContent.Children.Add(dpi);}
        if(action is ImageAction.VideoMp4 or ImageAction.VideoWebm){var label=Text(global::Orbit.Core.Localization.L.Get("Text_6E516AD6FCBD"));crf.ValueChanged+=(_,_)=>label.Text=global::Orbit.Core.Localization.L.Format("Text_F1B42D60C95C",(object?)((int)crf.Value));_panelContent.Children.Add(label);_panelContent.Children.Add(crf);}
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_3F6856200257"),12,"#B4A5F3"));_panelContent.Children.Add(Text(_outputDirectory??global::Orbit.Core.Localization.L.Get("Text_ACF73D932083"),11,"#7F83A3"));
        var run=new Button{Content=global::Orbit.Core.Localization.L.Get("Text_50799CFD526A"),Background=Ink("#6552CF"),BorderBrush=Ink("#9A82F4"),Margin=new Thickness(0,12,0,0),Padding=new Thickness(12,13,12,13)};
        GlassAccent(run);
        run.Click+=(_,_)=>{QueueSelected(new ImageJobOptions{Action=action,OutputDirectory=_outputDirectory,AudioBitrateKbps=(int)bitrate.SelectedItem,PdfDpi=(int)dpi.SelectedItem,VideoCrf=(int)crf.Value});if(_compact)ClosePanel();else ShowJobs();};_panelContent.Children.Add(run);
    }
    private void ShowAction(ImageAction action)
    {
        if(action==ImageAction.Crop&&!_selectionPreviewReady){Toast(global::Orbit.Core.Localization.L.Get("Text_55B88949B7C8"));return;}
        if(action==ImageAction.Resize){ShowImageResize();return;}
        if(action==ImageAction.Crop){ShowImageCrop();return;}
        if(action==ImageAction.Jpg){ShowJpegExport();return;}
        if(_metadata==null)return;
        if(action==ImageAction.Crop&&_files.Count!=1){Toast(global::Orbit.Core.Localization.L.Get("Text_B5A615184F2C"));return;}
        OpenPanel(ActionName(action),_files.Count==1?Path.GetFileName(_files[0]):global::Orbit.Core.Localization.L.Format("Text_5F89964AC3D8",(object?)(_files.Count)));
        var quality=new Slider{Minimum=1,Maximum=100,Value=action==ImageAction.Avif?65:85,TickFrequency=1,IsSnapToTickEnabled=true};
        var qualityLabel=Text(global::Orbit.Core.Localization.L.Get("Text_8DF377E07519"),13,"#E2DDF9");
        if(action is ImageAction.Jpg or ImageAction.WebP or ImageAction.Avif or ImageAction.Compress) {
            _panelContent.Children.Add(qualityLabel);_panelContent.Children.Add(quality);
            quality.ValueChanged+=(_,_)=>qualityLabel.Text=global::Orbit.Core.Localization.L.Format("Text_5E6B1783F1C4",(object?)((int)quality.Value));qualityLabel.Text=global::Orbit.Core.Localization.L.Format("Text_5E6B1783F1C4",(object?)((int)quality.Value));
            _panelContent.Children.Add(Text(action==ImageAction.Jpg?global::Orbit.Core.Localization.L.Get("Text_724CFF601E1E"):action==ImageAction.Compress?global::Orbit.Core.Localization.L.Get("Text_6F14C84E501C"):global::Orbit.Core.Localization.L.Get("Text_06D0D158E89B"),12,"#878BAE"));
        }
        _panelContent.Children.Add(new Border{Height=1,Background=Ink("#34364C"),Margin=new Thickness(0,18,0,12)});
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_7BCA630249B3"),12,"#B4A5F3"));
        _panelContent.Children.Add(Text(_outputDirectory??global::Orbit.Core.Localization.L.Get("Text_73B5339021B9"),11,"#7F83A3"));
        var run=new Button{Content=_files.Count>1?global::Orbit.Core.Localization.L.Format("Text_0F1617B0745D",(object?)(_files.Count)):global::Orbit.Core.Localization.L.Get("Text_590F13962F91"),Background=Ink("#6552CF"),BorderBrush=Ink("#9A82F4"),Margin=new Thickness(0,12,0,0),Padding=new Thickness(12,13,12,13)};
        GlassAccent(run);
        run.Click+=(_,_)=> {
            try {
                var options=new ImageJobOptions{Action=action,Quality=(int)quality.Value,OutputDirectory=_outputDirectory};
                QueueSelected(options);
                if(_compact)ClosePanel();else ShowJobs();
            }catch(Exception ex){Toast(ex.Message);}
        };
        _panelContent.Children.Add(run);
    }
    private static int? ParseDimension(string s) {if(string.IsNullOrWhiteSpace(s))return null;if(!int.TryParse(s,out int n)||n<1||n>100000)throw new InvalidOperationException(global::Orbit.Core.Localization.L.Get("Text_9A063ABC1D74"));return n;}
    private void QueueSelected(ImageJobOptions options)
    {
        if(_selectionLoading||_files.Count==0)return;
        if(options.Action is ImageAction.Archive7z or ImageAction.ArchiveTar or ImageAction.ArchiveZip or ImageAction.FileHash or ImageAction.PdfMerge or ImageAction.ImagesToPdf or ImageAction.ContactSheet){Enqueue(_files[0],options,_files.ToArray());return;}
        foreach(var file in _files.ToArray())Enqueue(file,options);
    }
    private void Enqueue(string path,ImageJobOptions options,IReadOnlyList<string>? inputPaths=null,SevenZipPassword? password=null)
    {
        if(_closing){password?.Dispose();return;}_jobTasks.RemoveAll(task=>task.IsCompleted);var job=new JobEntry(path,options){PopupSession=_compact?_popupSession:null,InputPaths=(inputPaths??new[]{path}).ToArray(),Password=password,State=_queueRunControl.IsPaused?global::Orbit.Core.Localization.L.Get("Text_AA8D8A7478F9"):global::Orbit.Core.Localization.L.Get("Text_B22A2454BD69")};_jobs.Add(job);ScheduleHistorySave();UpdateJobsButton();_jobTasks.Add(RunJobAsync(job));RefreshJobs();
    }
    private async Task RunJobAsync(JobEntry job)
    {
        try {
            await _queueGate.WaitAsync(job.Cancellation.Token);
            try {
                while(_queueRunControl.IsPaused)await _queueRunControl.WaitAsync(job.Cancellation.Token);
                job.Cancellation.Token.ThrowIfCancellationRequested();
                job.State=job.Download is null?global::Orbit.Core.Localization.L.Get("Text_79E1B445ED55"):global::Orbit.Core.Localization.L.Get("Text_7BDE70AD20CD");job.HistoryStatus=HistoryStatus.Running;ScheduleHistorySave();UpdateJobsButton();RefreshJobs();_status.Text=$"{ActionName(job.Options.Action)} · {JobCaption(job)}";
                if(IsCurrentPopupJob(job)){_lastPopupOutput=null;_radial.CenterHint.Text=global::Orbit.Core.Localization.L.Get("Text_79E1B445ED55");_radial.Metadata.Text=ActionName(job.Options.Action);}
                IProgress<double>? progress=IsMediaAction(job.Options.Action)?new Progress<double>(fraction=>{
                    if(job.Finished)return;job.Progress=Math.Clamp(fraction,0,1);RefreshJobs();
                    if(IsCurrentPopupJob(job))_radial.CenterHint.Text=global::Orbit.Core.Localization.L.Format("Text_8B17F4302151",(object?)(job.Progress));
                }):null;
                if(IsDiagnosticRun&&_queueExecutionBarrier is { } barrier)await barrier(job.Cancellation.Token);
                job.Result=job.Download is not null?await ExecuteDownloadAsync(job):job.Password is { } password
                    ?await _engine.ExecuteWithSevenZipPasswordAsync(job.InputPaths,job.Options,password,progress,job.Cancellation.Token)
                    :await _engine.ExecuteAsync(job.InputPaths,job.Options,progress,job.Cancellation.Token);
                job.Detail=ResultDetail(job.Result);
                if(job.Download is not null||job.DownloadWorkspaceEdit){job.State=global::Orbit.Core.Localization.L.Get("Text_E17B93CA1700");UpdateJobsButton();RefreshJobs();await CopyDownloadedFileAsync(job);}
                job.State=global::Orbit.Core.Localization.L.Get("Text_EF05D57959CF");job.HistoryStatus=HistoryStatus.Succeeded;
                _status.Text=global::Orbit.Core.Localization.L.Get("Text_EDFC3C11FB7B")+Path.GetFileName(job.Result.OutputPath);if((!_compact||IsCurrentPopupJob(job))&&!IsDownloadViewCurrent(job)&&!IsRadialJobViewCurrent(job))Toast(global::Orbit.Core.Localization.L.Get("Text_EDAB3F735581")+Path.GetFileName(job.Result.OutputPath));
                if(IsCurrentPopupJob(job)){_lastPopupOutput=job.Result.OutputPath;_radial.CenterHint.Text=global::Orbit.Core.Localization.L.Get("Text_EDFC3C11FB7B")+job.Result.Format;_radial.Metadata.Text=FormatBytes(job.Result.Bytes)+global::Orbit.Core.Localization.L.Get("Text_189237B1F547");_radial.CenterCard.ToolTip=global::Orbit.Core.Localization.L.Get("Text_A220F4D38EBC");}
            } finally {_queueGate.Release();}
        }catch(OperationCanceledException){job.State=global::Orbit.Core.Localization.L.Get("Text_23A2A9BFCF74");job.HistoryStatus=HistoryStatus.Cancelled;job.Detail=job.Download is null?global::Orbit.Core.Localization.L.Get("Text_C156C6A5E54E"):global::Orbit.Core.Localization.L.Get("Text_3880929CCCAE");if(IsCurrentPopupJob(job))_radial.CenterHint.Text=global::Orbit.Core.Localization.L.Get("Text_23A2A9BFCF74");}
        catch(Exception ex){job.State=global::Orbit.Core.Localization.L.Get("Text_BF22DFEEBA8B");job.HistoryStatus=HistoryStatus.Failed;job.Detail=ex.Message;App.Log(ex);if((!_compact||IsCurrentPopupJob(job))&&!IsDownloadViewCurrent(job)&&!IsRadialJobViewCurrent(job))Toast(ex.Message);if(IsCurrentPopupJob(job))_radial.CenterHint.Text=global::Orbit.Core.Localization.L.Get("Text_DEA70993E963");}
        finally{job.Password?.Dispose();job.Password=null;job.Finished=true;job.FinishedUtc=DateTimeOffset.UtcNow;RememberCompletedRecipe(job);PruneHistoryEntries();ScheduleHistorySave();UpdateJobsButton();RefreshJobs();}
    }
    private bool IsCurrentPopupJob(JobEntry job)=>!_radialWorkspaceVisible&&_compact&&job.PopupSession==_popupSession&&(job.Download is{} download?_browserContext&&download.Url==_browserUrl:_files.Contains(job.Path,StringComparer.OrdinalIgnoreCase));
    private void ShowJobs(){OpenPanel(global::Orbit.Core.Localization.L.Get("Text_03CC1C2D5AC2"), global::Orbit.Core.Localization.L.Get("Text_0A36506FD34F"));AddQueueControls();AddJobFilters();_jobsVisible=true;RenderJobs();}
    private void RefreshJobs(){UpdateQueueControls();if(_jobsVisible)RenderJobs();else if(_recentVisible)RenderRecentResults();RefreshDownloadView();RefreshRadialWorkspace();}
    private void RenderJobs()
    {
        UpdateQueueControls();if(_jobsCards==null)return;_jobsCards.Children.Clear();
        if(_historyWarning!=null)_jobsCards.Children.Add(Text(_historyWarning,12,"#EDACB9"));
        var filtered=FilteredJobs().OrderBy(j=>j.Finished).ThenBy(j=>!j.Finished&&j.HistoryStatus!=HistoryStatus.Running).ThenBy(j=>j.Finished?0L:j.CreatedUtc.UtcTicks).ThenByDescending(j=>j.CreatedUtc).ToArray();
        _jobsCards.Children.Add(Text(global::Orbit.Core.Localization.L.Format("Text_FD449C4B3E51",(object?)(filtered.Length),(object?)(_jobs.Count),(object?)(Math.Min(40,filtered.Length))),11));
        if(filtered.Length==0){_jobsCards.Children.Add(Text(_jobs.Count==0?global::Orbit.Core.Localization.L.Get("Text_8E6AD9C7A8D6"):global::Orbit.Core.Localization.L.Get("Text_5A911F22655B"),13));if(_historyWarning!=null)_jobsCards.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_3F70F0AD6764"),async(_,_)=>await ClearCompletedHistoryAsync()));return;}
        foreach(var job in filtered.Take(40)) {
            var stack=new StackPanel();stack.Children.Add(Text(JobCaption(job),13,"#E2DDF9"));
            stack.Children.Add(Text(ActionName(job.Options.Action)+" · "+job.State,12,job.HistoryStatus==HistoryStatus.Succeeded?"#8ED8BC":job.HistoryStatus==HistoryStatus.Failed?"#F2A6B3":"#B5A7FF"));
            if(job.Options.SevenZipPasswordProtected)stack.Children.Add(Text(job.Options.Action==ImageAction.Archive7z
                ?"AES-256 · "+(job.Options.SevenZipEncryptHeaders?global::Orbit.Core.Localization.L.Get("Text_874D762515F6"):global::Orbit.Core.Localization.L.Get("Text_37900AF7F1B8")):global::Orbit.Core.Localization.L.Get("Text_85A7549EA32A"),11));
            if(job.Options.Action is ImageAction.SevenZipExtractSelected or ImageAction.TarExtractSelected)stack.Children.Add(Text(global::Orbit.Core.Localization.L.Format("Text_928E18C06B79",(object?)(job.Options.ArchiveSelectedCount)),11));
            if(job.Options.Action==ImageAction.AudioTrackExtract)stack.Children.Add(Text(global::Orbit.Core.Localization.L.Format("Text_CE3D7FCA8134",(object?)(job.Options.AudioTrackStreamIndex),(object?)(job.Options.AudioTrackFormat))+
                (job.Options.AudioTrackFormat=="MP3"?global::Orbit.Core.Localization.L.Format("Text_14727EBE1884",(object?)(job.Options.AudioTrackBitrateKbps)):""),11));
            if(job.Options.Action is ImageAction.Archive7z or ImageAction.ImageWatermark or ImageAction.Jpg or ImageAction.Resize or ImageAction.AudioExport or ImageAction.AudioTags or ImageAction.VideoExport or ImageAction.AudioNormalize or ImageAction.ImageChain or ImageAction.PdfText or ImageAction.PdfEmbeddedImages or ImageAction.PdfOptimize&&PresetCatalog.CanSave(job.Options))stack.Children.Add(Text(PresetCatalog.DescribeOptions(job.Options),11));
            if(!string.IsNullOrEmpty(job.Detail))stack.Children.Add(Text(job.Detail,11,"#8B91B0"));
            stack.Children.Add(Text(job.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm"),10,"#8995AF"));
            var actions=new WrapPanel();
            if(job.Result!=null) {
                actions.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_EA1F40824607"),async(_,_)=>await OpenKnownResultAsync(job.Result.OutputPath,false)));
                actions.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_A99564C5702F"),async(_,_)=>await OpenKnownResultAsync(job.Result.OutputPath,true)));
                AddResultToolButtons(actions,job);
            } else if(!job.Finished) {
                stack.Children.Add(new ProgressBar{IsIndeterminate=job.HistoryStatus==HistoryStatus.Running&&job.Progress==null&&!job.Cancellation.IsCancellationRequested,Maximum=1,Value=job.Progress??0,Height=3,Margin=new Thickness(0,6,0,10),Foreground=Ink("#9980F6"),Background=Ink("#252841")});
                actions.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_7C47F7292BA4"),(_,_)=>{if(job.Finished)return;job.State=global::Orbit.Core.Localization.L.Get("Text_CCF5E8C11886");job.Cancellation.Cancel();UpdateJobsButton();RefreshJobs();}));
            } else actions.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_5189135A6110"),async(_,_)=>await TrackResultTask(RetryHistoryJobAsync(job))));
            if(PresetCatalog.CanSave(job.Options))actions.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_BAFD153F2688"),(_,_)=>ShowSavePreset(job)));
            stack.Children.Add(actions);
            var card=new Border{Child=stack,Padding=new Thickness(14,8,14,12),Margin=new Thickness(0,7,0,3),CornerRadius=new CornerRadius(8),BorderThickness=new Thickness(1)};
            card.SetResourceReference(Border.BackgroundProperty,"GlassControlFill");card.SetResourceReference(Border.BorderBrushProperty,"GlassEdge");_jobsCards.Children.Add(card);
        }
        var recent=Btn(global::Orbit.Core.Localization.L.Get("Text_A0705C8C4AC6"),(_,_)=>ShowRecentResults());recent.Margin=new Thickness(0,12,0,0);_jobsCards.Children.Add(recent);
        var clear=Btn(global::Orbit.Core.Localization.L.Get("Text_5F18F972105A"),async(_,_)=>await ClearCompletedHistoryAsync(FilteredJobs().Where(j=>j.Finished).Select(j=>j.Id).ToHashSet()));clear.IsEnabled=filtered.Any(j=>j.Finished);clear.ToolTip=global::Orbit.Core.Localization.L.Get("Text_2879A2ECA585");clear.Margin=new Thickness(0,8,0,0);_jobsCards.Children.Add(clear);
    }
    private static void OpenResult(string path,bool folder) {try {if(folder){var p=new ProcessStartInfo("explorer.exe");p.ArgumentList.Add("/select,");p.ArgumentList.Add(path);Process.Start(p);}else Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}catch(Exception ex){MessageBox.Show(ex.Message,"Arclo");}}
    private void ShowSettings()=>ShowSettings(false);
    private void ShowSettings(bool showThemes)
    {
        OpenPanel(global::Orbit.Core.Localization.L.Get("Text_985B5E0F2CCF"),"Arclo 0.34");
        _languageSettingsVisible=true;AddLanguageSettings();
        _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_67A2DA6CBE6D"),(_,_)=>ShowRadialLayout()));
        _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_AB8DD9BCBC4E"),(_,_)=>ShowComponents()));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_1731619EFA3A"),15,"#E9F2FF"));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_63F5FB28F995"),12,"#BAC6DE"));
        var transparency=new CheckBox{Content=global::Orbit.Core.Localization.L.Get("Text_8E195BBED62C"),IsChecked=_reducedTransparency};
        transparency.Click+=(_,_)=>{_reducedTransparency=transparency.IsChecked==true;ApplyGlassAppearance();SavePreferences();};_panelContent.Children.Add(transparency);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_4926592D51D5"),15,"#E9E3FF"));
        var sizes=new ComboBox{Tag="CompactSize",ItemsSource=new[]{320,360,380,440,520},SelectedItem=(int)_compactSize,Margin=new Thickness(0,2,0,12)};
        sizes.SelectionChanged+=(_,_)=>{if(sizes.SelectedItem is int value){_compactSize=value;SavePreferences();}};_panelContent.Children.Add(sizes);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_A77D5C4ECCAA"),15,"#E9E3FF"));
        var folderText=Text(_outputDirectory??global::Orbit.Core.Localization.L.Get("Text_D09E9A7BCFBC"),12);
        _panelContent.Children.Add(folderText);
        _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_582804FBABC5"),(_,_)=>{var d=new OpenFolderDialog{Title=global::Orbit.Core.Localization.L.Get("Text_4D815CE9C7EE")};if(WithPopupInteraction(()=>d.ShowDialog(this))==true){_outputDirectory=d.FolderName;folderText.Text=d.FolderName;SavePreferences();}}));
        _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_D09E9A7BCFBC"),(_,_)=>{_outputDirectory=null;folderText.Text=global::Orbit.Core.Localization.L.Get("Text_D09E9A7BCFBC");SavePreferences();}));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_BF9318C0D91D"),12));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_17A28AFA2723"),15,"#E9E3FF"));
        _panelContent.Children.Add(Text(ShortcutDisplay+(_bridge?.HotkeyRegistered==true?global::Orbit.Core.Localization.L.Get("Text_3436E0593FE8"):global::Orbit.Core.Localization.L.Get("Text_EC0123D32E22")),12));
        AddStartupSettings();
        var shell=new CheckBox{Content=global::Orbit.Core.Localization.L.Get("Text_E733B51ACDB0"),IsChecked=ShellIntegration.IsEnabled()};
        shell.Click+=(_,_)=>{try {if(shell.IsChecked==true)ShellIntegration.Enable(Environment.ProcessPath!);else ShellIntegration.Disable();Toast(global::Orbit.Core.Localization.L.Get("Text_36B6EE115B93"));}catch(Exception ex){shell.IsChecked=ShellIntegration.IsEnabled();Toast(ex.Message);}};
        _panelContent.Children.Add(shell);
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_CE769A1B1B29"),15,"#E9E3FF"));
        _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_BFB35ECBEC39"),(_,_)=>ShowWelcome()));
        _panelContent.Children.Add(Btn(global::Orbit.Core.Localization.L.Get("Text_70DB6B6CEC79"),(_,_)=>ShowDiagnostics()));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_0FFCD31FC254"),12));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_1A6C3DA1AAF5"),15,"#E9E3FF"));
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_4D766B8751FD"),12));
        AddHistorySettings();
        _panelContent.Children.Add(Text(global::Orbit.Core.Localization.L.Get("Text_C0512F22E990"),11,"#A2B3CF"));
        WrapSettingsTabs(showThemes);
    }
    private async void ToggleCompact() {
        if(_compact){SetCompactMode(false);return;}
        var invocation=NativeScreen.GetCursorInvocation();_popupAnchor=invocation;++_popupSession;
        int version=_invocationVersion;
        Hide();ShowActivated=false;SetCompactMode(true);await PreparePopupBackdropAsync(invocation);
        if(_closing||version!=_invocationVersion)return;
        Show();PlacePopup(new Size(_compactSize,_compactSize));ShowActivated=true;if(!IsDiagnosticRun)Activate();
    }
    private void SetCompactMode(bool compact) {
        StopPopupDismissal();
        if(!compact){++_invocationVersion;if(_pendingPopupInvocation!=0){_pendingPopupInvocation=0;_selectionLoading=false;}}
        _compact=compact;MinWidth=MinHeight=100;
        ClosePanel();
        _header.Visibility=_compact?Visibility.Collapsed:Visibility.Visible;_footer.Visibility=_compact?Visibility.Collapsed:Visibility.Visible;
        _root.RowDefinitions[0].Height=new GridLength(_compact?0:76);_root.RowDefinitions[2].Height=new GridLength(_compact?0:82);
        _body.Margin=new Thickness(_compact?0:10,0,_compact?0:10,0);_radialView.Margin=_compact?new Thickness(0):new Thickness(12);
        _ambient.Visibility=_compact?Visibility.Collapsed:Visibility.Visible;
        ShowInTaskbar=!_compact;ResizeMode=_compact?ResizeMode.NoResize:ResizeMode.CanResizeWithGrip;
        _toast.Visibility=Visibility.Collapsed;
        _toast.Margin=_compact?new Thickness(15,0,15,4):new Thickness(35,0,35,90);_toast.Padding=_compact?new Thickness(10,7,10,7):new Thickness(18,12,18,12);_toastText.FontSize=_compact?11:13;
        _shell.BorderThickness=new Thickness(_compact?0:1);ApplyGlassAppearance();
        if(_compact){Topmost=true;PlacePopup(new Size(_compactSize,_compactSize));}
        else {
            _popupAnchor=null;MinWidth=Math.Min(800,SystemParameters.WorkArea.Width-20);MinHeight=Math.Min(650,SystemParameters.WorkArea.Height-20);
            Width=Math.Min(960,SystemParameters.WorkArea.Width-40);Height=Math.Min(720,SystemParameters.WorkArea.Height-40);Topmost=false;
            Left=SystemParameters.WorkArea.Left+(SystemParameters.WorkArea.Width-Width)/2;Top=SystemParameters.WorkArea.Top+(SystemParameters.WorkArea.Height-Height)/2;
        }
        ArmPopupDismissal();
    }
    private bool IsDiagnosticRun=>_args.Contains("--capture")||_args.Contains("--ui-smoke")||_args.Contains("--toolkit-smoke")||_args.Contains("--popup-smoke")||_args.Contains("--themes-smoke")||_args.Contains("--selection-latency-smoke")||_args.Contains("--glass-smoke")||_args.Contains("--folder-smoke")||_args.Contains("--systems-smoke")||_args.Contains("--editing-smoke")||_args.Contains("--productivity-smoke")||_args.Contains("--results-smoke")||_args.Contains("--image-tools-smoke")||_args.Contains("--media-tools-smoke")||_args.Contains("--personalization-smoke")||_args.Contains("--document-tools-smoke")||_args.Contains("--workflows-smoke")||_args.Contains("--pdf-utilities-smoke")||_args.Contains("--media-export-smoke")||_args.Contains("--image-editing-smoke")||_args.Contains("--media-tracks-smoke")||_args.Contains("--archive-integrity-smoke")||_args.Contains("--audio-metadata-smoke")||_args.Contains("--usability-smoke")||_args.Contains("--sevenzip-smoke")||_args.Contains("--sevenzip-password-smoke")||_args.Contains("--archive-selection-smoke")||_args.Contains("--watermark-smoke")||_args.Contains("--queue-smoke")||_args.Contains("--download-smoke")||_args.Contains("--download-flow-smoke")||_args.Contains("--radial-workspace-smoke")||_args.Contains("--trim-timeline-smoke")||_args.Contains("--standalone-smoke")||_args.Contains("--localization-smoke");
    private void ArmPopupDismissal()
    {
        if(_closing||_pendingPopupInvocation!=0||!IsLoaded||!_compact||!IsVisible||WindowState==WindowState.Minimized||IsDiagnosticRun)return;
        _popupDismiss.OnShown(_popupClock.Elapsed);_popupDismissTimer.Start();
    }
    private void StopPopupDismissal()
    {
        _popupDismissTimer.Stop();_popupDismiss.Reset();_lastPopupDragOver=TimeSpan.MinValue;
    }
    private void CheckPopupDismissal()
    {
        if(_closing||!_compact||!IsVisible||WindowState==WindowState.Minimized){StopPopupDismissal();return;}
        if(IsDiagnosticRun||_pendingPopupInvocation!=0)return;
        try {
            var snapshot=NativeScreen.GetCursorInvocation();
            var handle=new WindowInteropHelper(this).Handle;
            if(!GetWindowRect(handle,out var bounds))return;
            bool ownForeground=snapshot.ForegroundWindow==handle||IsOwnedPopup(snapshot.ForegroundWindow,handle);
            var now=_popupClock.Elapsed;
            bool dragging=_lastPopupDragOver!=TimeSpan.MinValue&&now-_lastPopupDragOver<TimeSpan.FromMilliseconds(180);
            // Native dialogs, crop/slider captures, window dragging and OLE drops must finish normally.
            bool guarded=_popupInteractionDepth>0||Mouse.Captured!=null||dragging;
            var mode=_panel.Visibility==Visibility.Visible?PopupContentMode.Card:PopupContentMode.Radial;
            double scale=VisualTreeHelper.GetDpi(this).DpiScaleX;
            ApplyPopupDismissal(new PopupDismissInput(now,new Rect(bounds.Left,bounds.Top,bounds.Right-bounds.Left,bounds.Bottom-bounds.Top),new Point(snapshot.CursorX,snapshot.CursorY),scale,mode,true,true,ownForeground,guarded));
        }catch(System.ComponentModel.Win32Exception){/* A cursor snapshot can be unavailable during a desktop switch. */}
    }
    private PopupDismissReason ApplyPopupDismissal(PopupDismissInput input)
    {
        var reason=_popupDismiss.Evaluate(input);
        if(reason!=PopupDismissReason.None)HideToTray();
        return reason;
    }
    private static bool IsOwnedPopup(IntPtr candidate,IntPtr owner)
    {
        // Walk ownership instead of focus: WPF menus and native file dialogs have their own HWND.
        for(int depth=0;candidate!=IntPtr.Zero&&depth<12;depth++){
            candidate=GetWindow(candidate,4); // GW_OWNER
            if(candidate==owner)return true;
        }
        return false;
    }
    private T WithPopupInteraction<T>(Func<T> interaction)
    {
        ++_popupInteractionDepth;
        try{return interaction();}
        finally{--_popupInteractionDepth;if(_popupInteractionDepth==0)ArmPopupDismissal();}
    }
    private void DragPopupWindow(){WithPopupInteraction(()=>{DragMove();return true;});if(_compact){_popupBackdropFrame=null;RefreshPopupBackdrop();}}
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window,uint command);
    private void PlacePopup(Size desired)
    {
        if(!_compact)return;
        var anchor=_popupAnchor??=NativeScreen.GetCursorInvocation();var monitor=NativeScreen.GetWorkAreaAt(anchor.CursorX,anchor.CursorY);
        var placement=PopupPlacementMath.CenterAtCursor(new Point(anchor.CursorX,anchor.CursorY),new Rect(monitor.Left,monitor.Top,monitor.Right-monitor.Left,monitor.Bottom-monitor.Top),desired,monitor.DpiX/96d,monitor.DpiY/96d);
        Width=placement.WidthLogical;Height=placement.HeightLogical;
        var handle=new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(handle,new IntPtr(-1),(int)Math.Round(placement.LeftPhysical),(int)Math.Round(placement.TopPhysical),(int)Math.Round(placement.WidthPhysical),(int)Math.Round(placement.HeightPhysical),0x0010);
        RefreshPopupBackdrop();
    }
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window,IntPtr insertAfter,int x,int y,int width,int height,uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct NativeBounds{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window,out NativeBounds bounds);
    private int _toastVersion;
    private async void Toast(string message) {int version=++_toastVersion;_toastText.Text=message.Replace("Ctrl+Alt+Space",ShortcutDisplay,StringComparison.Ordinal).Replace(global::Orbit.Core.Localization.L.Get("Text_3A55DE5DF026"),ShortcutDisplay,StringComparison.Ordinal);_toast.Visibility=Visibility.Visible;await Task.Delay(5500);if(version==_toastVersion)_toast.Visibility=Visibility.Collapsed;}
    public static string FormatBytes(long bytes)=>bytes>=1048576?global::Orbit.Core.Localization.L.Format("Text_B769B4FA24A4",(object?)(bytes/1048576d)):bytes>=1024?global::Orbit.Core.Localization.L.Format("Text_FFB1D6C9E862",(object?)(bytes/1024d)):global::Orbit.Core.Localization.L.Format("Text_F5E015ED8F06",(object?)(bytes));
    private void Capture(string path) {var bmp=new RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,PixelFormats.Pbgra32);bmp.Render(this);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bmp));Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);using var stream=File.Create(path);encoder.Save(stream);}
    private async Task RunUiSmokeAsync()
    {
        try {
            var destination=Path.Combine(AppContext.BaseDirectory,"ui-smoke");Directory.CreateDirectory(destination);
            File.Delete(Path.Combine(destination,"passed.txt"));
            await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);await Task.Delay(200);Capture(Path.Combine(destination,"01-main.png"));
            if(_files.Count==0)throw new InvalidOperationException("Smoke test requires an image argument.");
            ShowAction(ImageAction.Crop);await Task.Delay(200);Capture(Path.Combine(destination,"02-crop.png"));
            ShowAction(ImageAction.Resize);await Task.Delay(200);Capture(Path.Combine(destination,"03-resize.png"));
            ShowSettings();await Task.Delay(200);Capture(Path.Combine(destination,"04-settings.png"));
            ShowJobs();await Task.Delay(200);Capture(Path.Combine(destination,"05-jobs.png"));
            _outputDirectory=destination;
            var webp=_radial.Children.OfType<SectorButton>().Single(b=>b.Action==ImageAction.Png);
            webp.Focus();webp.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(200);Capture(Path.Combine(destination,"06-webp.png"));
            var create=_panelContent.Children.OfType<Button>().Single(b=>Equals(b.Content,"Создать файл"));
            create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.WhenAll(_jobTasks);
            if(_jobs.Count!=1||_jobs[0].Result==null||!File.Exists(_jobs[0].Result!.OutputPath))throw new InvalidOperationException("UI to engine queue test failed");
            await Task.Delay(200);Capture(Path.Combine(destination,"07-result.png"));
            ClosePanel();ToggleCompact();_toast.Visibility=Visibility.Collapsed;await Task.Delay(200);Capture(Path.Combine(destination,"08-compact.png"));
            if(Math.Abs(ActualWidth-_compactSize)>1||Math.Abs(ActualHeight-_compactSize)>1)throw new InvalidOperationException("Compact mode minimum size prevented small popup");
            int before=_jobs.Count;webp.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.WhenAll(_jobTasks);
            if(_jobs.Count!=before+1||_jobs[^1].Result==null||_panel.Visibility!=Visibility.Collapsed||!_compact)throw new InvalidOperationException("One-click popup conversion did not remain compact");
            _toast.Visibility=Visibility.Collapsed;await Task.Delay(200);Capture(Path.Combine(destination,"09-compact-result.png"));
            ShowAction(ImageAction.Crop);await Task.Delay(200);
            if(!_compact||ActualWidth>_compactSize+1||_radialView.Visibility!=Visibility.Collapsed)throw new InvalidOperationException("Popup parameters expanded the manager");
            Capture(Path.Combine(destination,"10-compact-crop.png"));ClosePanel();
            VerifyPlacementMath();
            var selectionTests=ExplorerSelection.RunSafetySelfTests();
            if(selectionTests.Any(t=>!t.Passed))throw new InvalidOperationException("Explorer selection safety regression");
            var anchor=_popupAnchor!.Value;var monitor=NativeScreen.GetWorkAreaAt(anchor.CursorX,anchor.CursorY);
            var expected=PopupPlacementMath.CenterAtCursor(new Point(anchor.CursorX,anchor.CursorY),new Rect(monitor.Left,monitor.Top,monitor.Width,monitor.Height),new Size(_compactSize,_compactSize),monitor.ScaleX,monitor.ScaleY);
            GetWindowRect(new WindowInteropHelper(this).Handle,out var bounds);
            if(Math.Abs(bounds.Left-expected.LeftPhysical)>2||Math.Abs(bounds.Top-expected.TopPhysical)>2)throw new InvalidOperationException("Native popup was not placed at captured cursor");
            ShowAction(ImageAction.Crop);
            bool loaded=await LoadFilesAsync(new[]{Path.Combine(destination,"not-an-existing-image.png")});
            if(loaded||_files.Count!=0||_metadata!=null||_lastPopupOutput!=null)throw new InvalidOperationException("Failed new selection left an old actionable file");
            if(_panel.Visibility!=Visibility.Collapsed)throw new InvalidOperationException("Old crop card remained active after a new failed selection");
            before=_jobs.Count;HandleAction(ImageAction.Png);if(_jobs.Count!=before)throw new InvalidOperationException("Empty selection converted an old file");
            File.WriteAllText(Path.Combine(destination,"passed.txt"),$"PASS: full UI-to-PNG;{_compactSize}DIP popup;one-click PNG without manager;compact crop card;native cursor placement;8 placement/DPI cases;6 selection identity cases;failed selection clears old file. Internal WPF events/simulations, not a physical Explorer hotkey test.");
        }catch(Exception ex){File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"ui-smoke-error.txt"),ex.ToString());}
        Quit();
    }
    private async Task RunToolkitSmokeAsync()
    {
        var destination=Path.Combine(AppContext.BaseDirectory,"toolkit-smoke");Directory.CreateDirectory(destination);
        File.Delete(Path.Combine(destination,"passed.json"));
        var checks=new List<string>();
        void Require(bool condition,string name){if(!condition)throw new InvalidOperationException(name);checks.Add(name);}
        try {
            var index=Array.IndexOf(_args,"--toolkit-smoke");var fixture=Path.GetFullPath(_args[index+1]);
            _outputDirectory=destination;
            async Task Load(string name,ToolkitKind kind,int? count=null) {
                Require(await LoadFilesAsync(new[]{Path.Combine(fixture,name)}),"loaded "+name);
                Require(_selectionKind==kind,"context kind "+name);
                Require(_toast.Visibility==Visibility.Collapsed,"no stale result notification "+name);
                if(count.HasValue)Require(_radial.ActionCount==count.Value,"sector count "+name);
                if(!_compact)ToggleCompact();await Task.Delay(120);Capture(Path.Combine(destination,name.Replace('.','-')+".png"));
            }
            async Task<ImageJobResult> Execute(ImageAction action) {
                int before=_jobs.Count;HandleAction(action);await Task.WhenAll(_jobTasks);
                Require(_jobs.Count==before+1,"one queued action "+action);
                var result=_jobs[^1].Result;Require(result!=null,"real result "+action);
                Require(File.Exists(result!.OutputPath)||Directory.Exists(result.OutputPath),"existing output "+action);
                Require(_compact&&_panel.Visibility==Visibility.Collapsed&&ActualWidth<=_compactSize+1,"stays compact "+action);return result;
            }
            await Load("video.mp4",ToolkitKind.Video,6);
            Require(new ToolkitMetadata("test","MP4",0,0,1,ToolkitKind.Video,3665).DurationDisplay=="1:01:05","hour-long duration does not wrap minutes");
            Require(new ToolkitMetadata("test","MP4",0,0,1,ToolkitKind.Video,86405).DurationDisplay=="24:00:05","day-long duration keeps total hours");
            Require(_radial.Children.OfType<SectorButton>().Any(b=>b.Action==ImageAction.ExtractMp3),"video with audio exposes MP3");
            var audio=await Execute(ImageAction.ExtractMp3);Require(audio.OutputPath.EndsWith(".mp3",StringComparison.OrdinalIgnoreCase),"MP4-to-MP3 routing");
            await Load("silent.mp4",ToolkitKind.Video,6);
            Require(!_radial.Children.OfType<SectorButton>().Any(b=>b.Action==ImageAction.ExtractMp3),"silent video hides audio extraction");
            await Load("audio.wav",ToolkitKind.Audio,6);var flac=await Execute(ImageAction.AudioFlac);Require(flac.OutputPath.EndsWith(".flac",StringComparison.OrdinalIgnoreCase),"WAV-to-FLAC routing");
            await Load("document.pdf",ToolkitKind.Pdf,6);var pages=await Execute(ImageAction.PdfPng);Require(Directory.GetFiles(pages.OutputPath,"*.png").Length==2,"PDF-to-PNG returns both pages");
            await Load("archive.zip",ToolkitKind.Zip,4);var extracted=await Execute(ImageAction.ExtractZip);Require(File.ReadAllText(Path.Combine(extracted.OutputPath,"sample.txt"))=="Arclo fixture","ZIP extraction content");
            await Load("sample.txt",ToolkitKind.Other,3);var hash=await Execute(ImageAction.FileHash);
            var expected=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture,"sample.txt")))).ToLowerInvariant();
            Require(File.ReadAllText(hash.OutputPath).Contains(expected,StringComparison.Ordinal),"SHA-256 report matches bytes");
            Require(await LoadFilesAsync(new[]{Path.Combine(fixture,"sample.txt"),Path.Combine(fixture,"video.mp4")}),"mixed files loaded");
            Require(_selectionKind==ToolkitKind.Mixed&&_radial.ActionCount==3,"mixed selection only common actions");
            var archive=await Execute(ImageAction.ArchiveZip);using(var zip=System.IO.Compression.ZipFile.OpenRead(archive.OutputPath))Require(zip.Entries.Count==2,"mixed selection creates one ZIP with both files");
            HandleAction(ImageAction.FileInfo);Require(_panel.Visibility==Visibility.Visible&&_compact,"info card stays compact");await Task.Delay(150);Capture(Path.Combine(destination,"mixed-info.png"));
            ClosePanel();
            File.WriteAllText(Path.Combine(destination,"passed.json"),System.Text.Json.JsonSerializer.Serialize(new{Passed=checks.Count,Checks=checks,PhysicalHotkeyTest=false},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){File.WriteAllText(Path.Combine(destination,"error.txt"),ex.ToString());}
        Quit();
    }
    private static void VerifyPlacementMath()
    {
        static void Require(bool ok,string name){if(!ok)throw new InvalidOperationException("Placement regression: "+name);}
        var a=PopupPlacementMath.CenterAtCursor(new Point(1000,500),new Rect(0,0,1920,1080),new Size(440,440),1,1);
        Require(a.LeftPhysical==780&&a.TopPhysical==280,"center at100%");
        var b=PopupPlacementMath.CenterAtCursor(new Point(1500,800),new Rect(0,0,3000,2000),new Size(440,440),1.5,1.5);
        Require(b.WidthPhysical==660&&b.LeftPhysical==1170,"150% scales size, not cursor");
        var c=PopupPlacementMath.CenterAtCursor(new Point(-1200,300),new Rect(-1920,-200,1920,1080),new Size(440,440),1.25,1.25);
        Require(c.LeftPhysical==-1475&&c.TopPhysical==25,"negative-origin monitor");
        var d=PopupPlacementMath.CenterAtCursor(new Point(0,0),new Rect(0,0,1920,1080),new Size(440,440),1,1);
        Require(d.LeftPhysical==8&&d.TopPhysical==8,"top-left clamp");
        var e=PopupPlacementMath.CenterAtCursor(new Point(1920,1080),new Rect(0,0,1920,1040),new Size(440,440),1,1);
        Require(e.PhysicalBounds.Right==1912&&e.PhysicalBounds.Bottom==1032,"bottom-right taskbar clamp");
        var f=PopupPlacementMath.CenterAtCursor(new Point(0,0),new Rect(0,0,300,200),new Size(440,600),2,2);
        Require(f.WidthPhysical==268&&f.HeightPhysical==168,"small work area fitting");
        var g=PopupPlacementMath.CenterAtCursor(new Point(2500,500),new Rect(1920,0,2560,1440),new Size(440,440),2,2);
        Require(g.LeftPhysical==2060&&g.WidthPhysical==880,"second mixed-DPI monitor");
        var h=PopupPlacementMath.CenterAtCursor(new Point(900,500),new Rect(0,0,1920,1080),new Size(440,600),1,1);
        Require(h.LeftPhysical==680&&h.TopPhysical==200,"card shares cursor anchor");
    }
    private sealed class JobEntry(string path,ImageJobOptions options)
    {
        public bool DownloadWorkspaceEdit{get;init;}
        public bool? AutomaticCopySucceeded{get;set;}
        public double? DownloadSpeed{get;set;}
        public double? DownloadEta{get;set;}
        public DownloadRequest? Download{get;init;}
        public SevenZipPassword? Password{get;set;}
        public string Path {get;}=path;public ImageJobOptions Options{get;}=options;
        public CancellationTokenSource Cancellation{get;}=new();public string State{get;set;}=global::Orbit.Core.Localization.L.Get("Text_B22A2454BD69");public string Detail{get;set;}="";public bool Finished{get;set;}public ImageJobResult? Result{get;set;}
        public int? PopupSession{get;init;}
        public IReadOnlyList<string> InputPaths{get;init;}=Array.Empty<string>();
        public double? Progress{get;set;}
        public Guid Id{get;init;}=Guid.NewGuid();
        public DateTimeOffset CreatedUtc{get;init;}=DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedUtc{get;set;}
        public Orbit.App.History.HistoryStatus HistoryStatus{get;set;}=Orbit.App.History.HistoryStatus.Queued;
    }
}


