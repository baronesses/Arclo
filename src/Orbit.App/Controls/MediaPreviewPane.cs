using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Orbit.App.Controls;

/// <summary>Manual local-file playback. Close before disposing the owning MediaPreviewResult.
/// Diagnostic callers must pass mute:true; loading/preroll never invokes Play.</summary>
public sealed class MediaPreviewPane : UserControl, IDisposable
{
    private readonly MediaElement _media;
    private readonly Slider _seek;
    private readonly TextBlock _status;
    private readonly TextBlock _time;
    private readonly Button _play;
    private readonly Button _pause;
    private readonly Button _stop;
    private readonly DispatcherTimer _timer;
    private readonly Uri _source;
    private bool _closed, _opened, _playing, _updatingSeek, _seeking;
    private double _duration;
    private readonly bool _fullLength;
    private readonly Stopwatch _openingWatch = new();

    public MediaPreviewPane(string localPath, bool isVideo, double duration, bool mute = false, bool fullLength = false)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !double.IsFinite(duration) || duration <= 0 || duration > (fullLength ? 86400 : 30.1))
            throw new ArgumentException(fullLength ? global::Orbit.Core.Localization.L.Get("Text_BC024D3165FC") : global::Orbit.Core.Localization.L.Get("Text_344DCA480298"));
        _source = new Uri(Path.GetFullPath(localPath), UriKind.Absolute);
        if (!_source.IsFile) throw new ArgumentException(global::Orbit.Core.Localization.L.Get("Text_CD586A3DC040"));
        IsVideo = isVideo;_duration = duration;_fullLength = fullLength;
        MinWidth = 180;Height = isVideo ? 285 : 145;Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;VerticalContentAlignment = VerticalAlignment.Stretch;
        var grid = new Grid { Background = Brushes.Transparent, UseLayoutRounding = true };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var stage = new Grid { Background = Ink(isVideo ? "#171B20" : "#F3F5F7"), ClipToBounds = true };
        if (!isVideo) Resource(stage, Panel.BackgroundProperty, "GlassFieldFill");
        _media = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close,
            Stretch = Stretch.Uniform, ScrubbingEnabled = isVideo || !fullLength, Volume = .7, IsMuted = mute, Visibility = isVideo || fullLength ? Visibility.Visible : Visibility.Hidden };
        stage.Children.Add(_media);
        if (!isVideo)
        {
            var caption = new TextBlock { Text = fullLength ? global::Orbit.Core.Localization.L.Get("Text_0BB74ECA90A7") : global::Orbit.Core.Localization.L.Get("Text_616B7000A234"), Foreground = Ink("#202630"), FontFamily = new FontFamily("Segoe UI"), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Resource(caption, TextBlock.ForegroundProperty, "TextBrush");stage.Children.Add(caption);
        }
        var frame = new Border { Background = Ink(isVideo ? "#171B20" : "#F3F5F7"), BorderBrush = Ink("#C8CFD8"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = stage };
        Resource(frame, Border.BorderBrushProperty, "GlassEdge");if (!isVideo) Resource(frame, Border.BackgroundProperty, "GlassFieldFill");grid.Children.Add(frame);
        _seek = new Slider { Minimum = 0, Maximum = duration, Value = 0, SmallChange = .1, LargeChange = 1,
            IsMoveToPointEnabled = true, Height = 28, Margin = new Thickness(3, 2, 3, 0), IsEnabled = false };
        AutomationProperties.SetName(_seek, fullLength ? global::Orbit.Core.Localization.L.Get("Text_5F7B00488EDA") : global::Orbit.Core.Localization.L.Get("Text_F247C6368E88"));AutomationProperties.SetHelpText(_seek, fullLength ? global::Orbit.Core.Localization.L.Get("Text_BC2B0538DC7C") : global::Orbit.Core.Localization.L.Get("Text_842627F3DD45"));
        Grid.SetRow(_seek, 1);grid.Children.Add(_seek);
        var controls = new Grid();controls.ColumnDefinitions.Add(new ColumnDefinition());controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _play = MakeButton("▶", fullLength ? global::Orbit.Core.Localization.L.Get("Text_349326E82560") : global::Orbit.Core.Localization.L.Get("Text_FC6AFE6BA8F5"));_pause = MakeButton("Ⅱ", fullLength ? global::Orbit.Core.Localization.L.Get("Text_BD0BAAC806BA") : global::Orbit.Core.Localization.L.Get("Text_06F26C8D5100"));_stop = MakeButton("■", fullLength ? global::Orbit.Core.Localization.L.Get("Text_D3EC4814A76C") : global::Orbit.Core.Localization.L.Get("Text_64977D7B3FD8"));
        if (fullLength) _play.Width = _pause.Width = _stop.Width = 40;
        buttons.Children.Add(_play);buttons.Children.Add(_pause);buttons.Children.Add(_stop);controls.Children.Add(buttons);
        _time = new TextBlock { Foreground = Ink("#202630"), FontFamily = new FontFamily("Segoe UI"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };Resource(_time, TextBlock.ForegroundProperty, "TextBrush");
        Grid.SetColumn(_time, 1);controls.Children.Add(_time);Grid.SetRow(controls, 2);grid.Children.Add(controls);
        _status = new TextBlock { Text = fullLength ? global::Orbit.Core.Localization.L.Get("Text_6BB52EF0360B") : global::Orbit.Core.Localization.L.Get("Text_8162C32076F6"), Foreground = Ink("#606A76"), FontFamily = new FontFamily("Segoe UI"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };Resource(_status, TextBlock.ForegroundProperty, "GlassMutedText");
        Grid.SetRow(_status, 3);grid.Children.Add(_status);Content = grid;
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += OnTick;_media.MediaOpened += OnOpened;_media.MediaFailed += OnFailed;_media.MediaEnded += OnEnded;
        _play.Click += OnPlay;_pause.Click += OnPause;_stop.Click += OnStop;
        _seek.ValueChanged += OnSeekChanged;_seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnDragStarted));_seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted));
        Loaded += OnLoaded;Unloaded += OnUnloaded;UpdateButtons();UpdateTime();
    }

    public bool IsVideo { get; }
    public bool IsFullLength => _fullLength;
    public bool IsOpened => _opened && !_closed;
    public bool IsPlaying => _playing && !_closed;
    public bool IsClosed => _closed;
    public double DurationSeconds => _duration;
    public string? ErrorMessage { get; private set; }
    public string StatusText => _status.Text;
    public bool IsMuted { get => _media.IsMuted;set => _media.IsMuted = value; }
    public double PositionSeconds { get { if (_closed || !_opened) return 0;try { return Math.Clamp(_media.Position.TotalSeconds, 0, _duration); } catch (InvalidOperationException) { return 0; } } }
    public event EventHandler? Opened;
    public event EventHandler? Failed;
    internal MediaElement Player => _media;
    internal Slider SeekSlider => _seek;

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_closed || _media.Source is not null) return;
        try
        {
            _media.Source = _source;_media.Pause();
            if (_fullLength && !IsVideo && !_opened) { _openingWatch.Restart();_timer.Start(); }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or IOException) { Fail(exception); }
    }
    private void OnOpened(object sender, RoutedEventArgs args)
    {
        if (_closed || (_fullLength && !IsVideo && _opened)) return;
        if (IsVideo && (_media.NaturalVideoWidth <= 0 || _media.NaturalVideoHeight <= 0)) { Fail(new NotSupportedException(global::Orbit.Core.Localization.L.Get("Text_875D42BC6F97")));return; }
        if (_media.NaturalDuration.HasTimeSpan)
        {
            var natural = _media.NaturalDuration.TimeSpan.TotalSeconds;
            if (!double.IsFinite(natural) || natural <= 0 || natural > (_fullLength ? 86400 : 30.1)) { Fail(new NotSupportedException(_fullLength ? global::Orbit.Core.Localization.L.Get("Text_9A4F9E14839D") : global::Orbit.Core.Localization.L.Get("Text_FE63C6C4381B")));return; }
            _duration = natural;
        }
        _seek.Maximum = _duration;_opened = true;_playing = false;ErrorMessage = null;_openingWatch.Stop();
        _status.Text = global::Orbit.Core.Localization.L.Get("Text_41C9917D3BDD");UpdateButtons();UpdateTime();_timer.Start();Opened?.Invoke(this, EventArgs.Empty);
    }
    private void OnFailed(object? sender, ExceptionRoutedEventArgs args) { if (!_closed) Fail(args.ErrorException); }
    private void Fail(Exception exception)
    {
        _timer.Stop();_openingWatch.Stop();_opened = false;_playing = false;
        ErrorMessage = (_fullLength ? global::Orbit.Core.Localization.L.Get("Text_18E05AF4F095") : global::Orbit.Core.Localization.L.Get("Text_9F4450957A0E")) + global::Orbit.Core.Localization.L.Get("Text_28CA5E2246C3") + exception.Message[..Math.Min(exception.Message.Length, 180)];
        _status.Text = ErrorMessage;UpdateButtons();Failed?.Invoke(this, EventArgs.Empty);
    }
    private void OnEnded(object sender, RoutedEventArgs args)
    {
        if (_closed) return;
        _media.Pause();_playing = false;_status.Text = _fullLength ? global::Orbit.Core.Localization.L.Get("Text_479F99215022") : global::Orbit.Core.Localization.L.Get("Text_29020D4D0798");UpdateButtons();UpdateTime();
    }
    private void OnPlay(object sender, RoutedEventArgs args) => Play();
    private void OnPause(object sender, RoutedEventArgs args) => Pause();
    private void OnStop(object sender, RoutedEventArgs args) => Stop();
    public void Play()
    {
        if (!_opened || _closed) return;
        try { if (PositionSeconds >= _duration - .01) _media.Position = TimeSpan.Zero;_media.Play();_playing = true;_status.Text = global::Orbit.Core.Localization.L.Get("Text_35E40B780C0D");UpdateButtons(); }
        catch (InvalidOperationException exception) { Fail(exception); }
    }
    public void Pause()
    {
        if (!_opened || _closed) return;
        try { _media.Pause();_playing = false;_status.Text = global::Orbit.Core.Localization.L.Get("Text_FBC33AF56B4E");UpdateButtons();UpdateTime(); }
        catch (InvalidOperationException exception) { Fail(exception); }
    }
    public void Stop()
    {
        if (!_opened || _closed) return;
        try { _media.Stop();_playing = false;_status.Text = global::Orbit.Core.Localization.L.Get("Text_985C1F5A332F");UpdateButtons();UpdateTime(); }
        catch (InvalidOperationException exception) { Fail(exception); }
    }
    private void OnTick(object? sender, EventArgs args)
    {
        if (_fullLength && !IsVideo && !_opened && !_closed && ErrorMessage is null)
        {
            try
            {
                // A paused audio source can expose real metadata before MediaPrerolled/MediaOpened.
                // Enable manual commands only after Windows confirms usable audio; never Play here.
                var natural = _media.NaturalDuration;
                if (natural.HasTimeSpan && double.IsFinite(natural.TimeSpan.TotalSeconds)
                    && natural.TimeSpan.TotalSeconds is > 0 and <= 86400 && _media.HasAudio && _media.CanPause)
                    OnOpened(_media, new RoutedEventArgs());
                else if (_openingWatch.Elapsed >= TimeSpan.FromSeconds(15))
                    Fail(new NotSupportedException(global::Orbit.Core.Localization.L.Get("Text_3C92E0D496B7")));
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or IOException) { Fail(exception); }
        }
        UpdateTime();
    }
    private void UpdateTime()
    {
        var position = _seeking ? _seek.Value : PositionSeconds;
        if (!_seeking) { _updatingSeek = true;_seek.Value = position;_updatingSeek = false; }
        _time.Text = Clock(position) + " / " + Clock(_duration);
    }
    private static string Clock(double seconds) => seconds >= 3600 ? ((long)(seconds / 3600)).ToString(CultureInfo.InvariantCulture) + ":" + TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture) : TimeSpan.FromSeconds(seconds).ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);
    private void UpdateButtons() { _play.IsEnabled = _opened && !_closed && !_playing;_pause.IsEnabled = _opened && !_closed && _playing;_stop.IsEnabled = _opened && !_closed;_seek.IsEnabled = _opened && !_closed; }
    private void OnSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> args) { if (!_updatingSeek && !_seeking) Seek(args.NewValue); }
    private void OnDragStarted(object sender, DragStartedEventArgs args) { if (ReferenceEquals(sender, _seek) || _seek.IsAncestorOf(args.OriginalSource as DependencyObject)) _seeking = true; }
    private void OnDragCompleted(object sender, DragCompletedEventArgs args) { _seeking = false;Seek(_seek.Value); }
    private void Seek(double seconds)
    {
        if (!_opened || _closed || !double.IsFinite(seconds)) return;
        try { _media.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, _duration));UpdateTime(); }
        catch (InvalidOperationException exception) { Fail(exception); }
    }
    private void OnUnloaded(object sender, RoutedEventArgs args) => Close();
    public void Close()
    {
        if (!Dispatcher.CheckAccess()) { if (!Dispatcher.HasShutdownStarted) Dispatcher.Invoke(Close);return; }
        if (_closed) return;
        _closed = true;_opened = false;_playing = false;_seeking = false;_timer.Stop();_openingWatch.Stop();_timer.Tick -= OnTick;
        Loaded -= OnLoaded;Unloaded -= OnUnloaded;_media.MediaOpened -= OnOpened;_media.MediaFailed -= OnFailed;_media.MediaEnded -= OnEnded;
        _play.Click -= OnPlay;_pause.Click -= OnPause;_stop.Click -= OnStop;_seek.ValueChanged -= OnSeekChanged;
        _seek.RemoveHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnDragStarted));_seek.RemoveHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted));
        if (Mouse.Captured is Visual captured && IsAncestorOf(captured)) Mouse.Capture(null);
        try { _media.IsMuted = true;_media.Volume = 0;_media.Close();_media.Source = null; } catch (InvalidOperationException) { }
        _status.Text = _fullLength ? global::Orbit.Core.Localization.L.Get("Text_AF102B787A1A") : global::Orbit.Core.Localization.L.Get("Text_BBAB243D6F75");UpdateButtons();
    }
    public void Dispose() => Close();

    private static Button MakeButton(string caption, string accessibleName)
    {
        var button = new Button { Content = caption, Width = 47, Height = 28, Margin = new Thickness(0, 0, 6, 0), FontFamily = new FontFamily("Segoe UI"), FontSize = 14, Foreground = Ink("#202630"), Background = Ink("#FFFFFF"), BorderBrush = Ink("#C8CFD8"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, ToolTip = accessibleName };
        Resource(button, Control.ForegroundProperty, "TextBrush");Resource(button, Control.BackgroundProperty, "GlassControlFill");Resource(button, Control.BorderBrushProperty, "GlassEdge");
        AutomationProperties.SetName(button, accessibleName);
        var border = new FrameworkElementFactory(typeof(Border)) { Name = "MediaButtonBorder" };border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Background)) { RelativeSource = RelativeSource.TemplatedParent });border.SetBinding(Border.BorderBrushProperty, new Binding(nameof(BorderBrush)) { RelativeSource = RelativeSource.TemplatedParent });border.SetBinding(Border.BorderThicknessProperty, new Binding(nameof(BorderThickness)) { RelativeSource = RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };hover.Setters.Add(new Setter(Border.BackgroundProperty, Themed("GlassControlHover", "#F0F3F6"), "MediaButtonBorder"));hover.Setters.Add(new Setter(Control.ForegroundProperty, Themed("GlassSelectionText", "#202630")));template.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };pressed.Setters.Add(new Setter(Border.BackgroundProperty, Themed("GlassSelection", "#D7E0EB"), "MediaButtonBorder"));template.Triggers.Add(pressed);
        var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };focus.Setters.Add(new Setter(Border.BorderBrushProperty, Themed("GlassFocusEdge", "#63758A"), "MediaButtonBorder"));focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "MediaButtonBorder"));template.Triggers.Add(focus);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };disabled.Setters.Add(new Setter(OpacityProperty, .42));template.Triggers.Add(disabled);button.Template = template;return button;
    }
    private static Brush Ink(string hex) { var value = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));value.Freeze();return value; }
    private static void Resource(FrameworkElement target, DependencyProperty property, string key) { if (Application.Current?.TryFindResource(key) is Brush) target.SetResourceReference(property, key); }
    private static object Themed(string key, string fallback) => Application.Current?.TryFindResource(key) is Brush ? new DynamicResourceExtension(key) : Ink(fallback);
}
