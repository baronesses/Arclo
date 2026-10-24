using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Imaging = System.Drawing.Imaging;

namespace Orbit.App.Platform;

/// <summary>A frozen, blurred desktop sample in physical screen coordinates. It is never persisted by the sampler.</summary>
public sealed record BackdropFrame(BitmapSource Image, Rect ScreenBounds, TimeSpan CaptureDuration);

/// <summary>
/// Samples a popup's future location while the caller keeps that popup hidden. This is a static backdrop,
/// refreshed on the next invocation; it does not hide windows, change capture affinity, or capture to disk.
/// </summary>
public sealed class PopupBackdropSampler : IDisposable
{
    public const int MaximumSampleEdge = 640;
    public const long MaximumCapturePixels = 8_000_000;
    private readonly SemaphoreSlim _workerGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<Rect, CancellationToken, BackdropFrame> _capture;
    private readonly TimeSpan _timeout;
    private int _disposed;
    private long _version;

    public PopupBackdropSampler(TimeSpan? timeout = null) : this(CaptureAndBlur, timeout) { }

    internal PopupBackdropSampler(Func<Rect, CancellationToken, BackdropFrame> capture, TimeSpan? timeout = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _timeout = timeout ?? TimeSpan.FromMilliseconds(120);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public string? UnavailableReason { get; private set; }
    public bool IsBusy => _workerGate.CurrentCount == 0;

    /// <summary>Call before showing the popup. One retained worker bounds timeout/cancellation even if native capture stalls.</summary>
    public async Task<BackdropFrame?> CaptureAsync(Rect physicalScreenBounds, CancellationToken cancellationToken = default)
    {
        long version = Interlocked.Increment(ref _version);
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0) return null;
        if (!ValidBounds(physicalScreenBounds))
        {
            UnavailableReason = global::Orbit.Core.Localization.L.Get("Text_EC9A8048C0C8");
            return null;
        }
        if (!_workerGate.Wait(0))
        {
            UnavailableReason = global::Orbit.Core.Localization.L.Get("Text_D5B83E80C40C");
            return null;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        var worker = Task.Run(() =>
        {
            try { token.ThrowIfCancellationRequested(); return _capture(physicalScreenBounds, token); }
            finally { _workerGate.Release(); }
        }, CancellationToken.None);
        try
        {
            var frame = await worker.WaitAsync(_timeout, token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) != 0 || version != Interlocked.Read(ref _version)) return null;
            UnavailableReason = null;
            return frame;
        }
        catch (TimeoutException)
        {
            linked.Cancel(); Observe(worker);
            UnavailableReason = global::Orbit.Core.Localization.L.Get("Text_308F66A160FC");
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            linked.Cancel(); Observe(worker); return null;
        }
        catch (OperationCanceledException) { linked.Cancel(); Observe(worker); throw; }
        catch (Exception error) when (error is ExternalException or Win32Exception or ArgumentException or InvalidOperationException)
        {
            UnavailableReason = global::Orbit.Core.Localization.L.Get("Text_E593D7FC3C86");
            return null;
        }
    }

    private static bool ValidBounds(Rect bounds) => !bounds.IsEmpty && double.IsFinite(bounds.X) && double.IsFinite(bounds.Y)
        && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) && bounds.Width >= 1 && bounds.Height >= 1
        && bounds.Width <= 4096 && bounds.Height <= 4096 && bounds.Width * bounds.Height <= MaximumCapturePixels
        && bounds.X >= int.MinValue + 4096d && bounds.Y >= int.MinValue + 4096d && bounds.Right <= int.MaxValue - 4096d && bounds.Bottom <= int.MaxValue - 4096d;

    private static BackdropFrame CaptureAndBlur(Rect bounds, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        int left = (int)Math.Floor(bounds.Left), top = (int)Math.Floor(bounds.Top);
        int width = (int)Math.Ceiling(bounds.Right) - left, height = (int)Math.Ceiling(bounds.Bottom) - top;
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || (long)width * height > MaximumCapturePixels) throw new ArgumentException("The rounded sample bounds exceed resource limits.");
        var rectangle = new Drawing.Rectangle(left, top, width, height);
        if (!System.Windows.Forms.SystemInformation.VirtualScreen.Contains(rectangle)) throw new ArgumentException("The sample must lie inside the virtual desktop.");
        token.ThrowIfCancellationRequested();
        // The caller has just hidden the popup. Wait on this worker for the compositor to retire its old surface.
        try { _ = DwmFlush(); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
        token.ThrowIfCancellationRequested();
        using var captured = new Drawing.Bitmap(width, height, Imaging.PixelFormat.Format32bppRgb);
        using (var graphics = Drawing.Graphics.FromImage(captured))
            graphics.CopyFromScreen(left, top, 0, 0, rectangle.Size, Drawing.CopyPixelOperation.SourceCopy);
        token.ThrowIfCancellationRequested();
        double scale = Math.Min(1, MaximumSampleEdge / (double)Math.Max(width, height));
        int sampleWidth = Math.Max(1, (int)Math.Round(width * scale)), sampleHeight = Math.Max(1, (int)Math.Round(height * scale));
        using var sample = new Drawing.Bitmap(sampleWidth, sampleHeight, Imaging.PixelFormat.Format32bppPArgb);
        using (var graphics = Drawing.Graphics.FromImage(sample))
        {
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.DrawImage(captured, new Drawing.Rectangle(0, 0, sampleWidth, sampleHeight));
        }
        var pixels = new byte[sampleWidth * sampleHeight * 4];
        var data = sample.LockBits(new Drawing.Rectangle(0, 0, sampleWidth, sampleHeight), Imaging.ImageLockMode.ReadOnly, Imaging.PixelFormat.Format32bppPArgb);
        try { for (int row = 0; row < sampleHeight; row++) Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * sampleWidth * 4, sampleWidth * 4); }
        finally { sample.UnlockBits(data); }
        Blur(pixels, sampleWidth, sampleHeight, 12, token);
        token.ThrowIfCancellationRequested();
        var image = BitmapSource.Create(sampleWidth, sampleHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, sampleWidth * 4);
        image.Freeze();
        return new BackdropFrame(image, new Rect(left, top, width, height), timer.Elapsed);
    }

    internal static void Blur(byte[] pixels, int width, int height, int radius, CancellationToken token)
    {
        var temporary = new byte[pixels.Length];int diameter = radius * 2 + 1;
        for (int pass = 0; pass < 3; pass++)
        {
            for (int row = 0; row < height; row++)
            {
                token.ThrowIfCancellationRequested();int baseIndex = row * width * 4, blue = 0, green = 0, red = 0;
                for (int offset = -radius; offset <= radius; offset++) { int at = baseIndex + Math.Clamp(offset, 0, width - 1) * 4; blue += pixels[at]; green += pixels[at + 1]; red += pixels[at + 2]; }
                for (int x = 0; x < width; x++)
                {
                    int at = baseIndex + x * 4;temporary[at] = (byte)(blue / diameter);temporary[at + 1] = (byte)(green / diameter);temporary[at + 2] = (byte)(red / diameter);temporary[at + 3] = 255;
                    int remove = baseIndex + Math.Clamp(x - radius, 0, width - 1) * 4, add = baseIndex + Math.Clamp(x + radius + 1, 0, width - 1) * 4;
                    blue += pixels[add] - pixels[remove];green += pixels[add + 1] - pixels[remove + 1];red += pixels[add + 2] - pixels[remove + 2];
                }
            }
            for (int column = 0; column < width; column++)
            {
                token.ThrowIfCancellationRequested();int blue = 0, green = 0, red = 0;
                for (int offset = -radius; offset <= radius; offset++) { int at = (Math.Clamp(offset, 0, height - 1) * width + column) * 4; blue += temporary[at]; green += temporary[at + 1]; red += temporary[at + 2]; }
                for (int y = 0; y < height; y++)
                {
                    int at = (y * width + column) * 4;pixels[at] = (byte)(blue / diameter);pixels[at + 1] = (byte)(green / diameter);pixels[at + 2] = (byte)(red / diameter);pixels[at + 3] = 255;
                    int remove = (Math.Clamp(y - radius, 0, height - 1) * width + column) * 4, add = (Math.Clamp(y + radius + 1, 0, height - 1) * width + column) * 4;
                    blue += temporary[add] - temporary[remove];green += temporary[add + 1] - temporary[remove + 1];red += temporary[add + 2] - temporary[remove + 2];
                }
            }
        }
    }

    private static void Observe(Task task) => _ = task.ContinueWith(done => _ = done.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Increment(ref _version);_lifetime.Cancel();
        // A native capture cannot be interrupted. Its worker retains the gate until it actually exits.
    }
}
