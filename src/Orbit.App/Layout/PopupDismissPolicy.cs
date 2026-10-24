using System;
using System.Windows;

namespace Orbit.App.Layout;

public enum PopupContentMode { Radial, Card }
public enum PopupDismissReason { None, ForegroundChanged, CursorLeft }

/// <summary>Physical screen pixels, current presentation state, and a caller-supplied monotonic clock.</summary>
public readonly record struct PopupDismissInput(
    TimeSpan Now,
    Rect BoundsPhysical,
    Point CursorPhysical,
    double DpiScale,
    PopupContentMode Mode,
    bool IsVisible,
    bool IsCompact,
    bool IsActive,
    bool IsTemporarilySuppressed = false);

/// <summary>
/// Pure popup lifetime policy. It reads no native/window/cursor state and installs no hooks.
/// The owner polls Evaluate and hides its popup when the returned reason is not None.
/// </summary>
public sealed class PopupDismissPolicy
{
    private readonly TimeSpan _showGrace;
    private readonly TimeSpan _leaveDelay;
    private readonly double _toleranceLogical;
    private TimeSpan? _shownAt;
    private TimeSpan? _outsideSince;
    private TimeSpan? _lastTick;
    private PopupContentMode? _lastMode;
    private bool _wasSuppressed;

    public PopupDismissPolicy(TimeSpan? showGrace = null, TimeSpan? leaveDelay = null, double toleranceLogical = 45)
    {
        _showGrace = showGrace ?? TimeSpan.FromMilliseconds(220);
        _leaveDelay = leaveDelay ?? TimeSpan.FromMilliseconds(300);
        if (_showGrace < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(showGrace));
        if (_leaveDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaveDelay));
        if (!double.IsFinite(toleranceLogical) || toleranceLogical < 0) throw new ArgumentOutOfRangeException(nameof(toleranceLogical));
        _toleranceLogical = toleranceLogical;
    }

    /// <summary>Call for each new popup invocation, even when reusing an already-visible window.</summary>
    public void OnShown(TimeSpan now)
    {
        if (now < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(now));
        _shownAt = now;
        _outsideSince = null;
        _lastTick = now;
        _lastMode = null;
        _wasSuppressed = false;
    }

    /// <summary>Call on hide/exit; an ineligible Evaluate call also resets the session.</summary>
    public void Reset()
    {
        _shownAt = null;
        _outsideSince = null;
        _lastTick = null;
        _lastMode = null;
        _wasSuppressed = false;
    }

    public PopupDismissReason Evaluate(PopupDismissInput input)
    {
        if (input.Now < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(input));
        if (!input.IsVisible || !input.IsCompact)
        {
            Reset();
            return PopupDismissReason.None;
        }

        // Stopwatch.Elapsed is expected. A regressing test/system clock safely starts a new grace period.
        if (_shownAt is null || (_lastTick is { } last && input.Now < last)) OnShown(input.Now);
        _lastTick = input.Now;

        if (input.IsTemporarilySuppressed)
        {
            _wasSuppressed = true;
            _outsideSince = null;
            _lastMode = input.Mode;
            return PopupDismissReason.None;
        }

        if (_wasSuppressed)
        {
            // Allow focus to return after closing a dialog or releasing capture; no old leave timer survives.
            _shownAt = input.Now;
            _outsideSince = null;
            _wasSuppressed = false;
        }

        if (_lastMode != input.Mode) _outsideSince = null;
        _lastMode = input.Mode;

        if (input.Now - _shownAt!.Value < _showGrace)
        {
            _outsideSince = null;
            return PopupDismissReason.None;
        }

        if (!input.IsActive)
        {
            _outsideSince = null;
            return PopupDismissReason.ForegroundChanged;
        }

        if (!TryIsInside(input, out var inside) || inside)
        {
            // A transient missing native window rectangle never triggers a cursor-based dismissal.
            _outsideSince = null;
            return PopupDismissReason.None;
        }

        _outsideSince ??= input.Now;
        return input.Now - _outsideSince.Value >= _leaveDelay
            ? PopupDismissReason.CursorLeft
            : PopupDismissReason.None;
    }

    private bool TryIsInside(PopupDismissInput input, out bool inside)
    {
        inside = false;
        var bounds = input.BoundsPhysical;
        if (bounds.IsEmpty || !Finite(bounds.Left) || !Finite(bounds.Top) || !Positive(bounds.Width) || !Positive(bounds.Height)
            || !Finite(bounds.Right) || !Finite(bounds.Bottom) || !Finite(input.CursorPhysical.X) || !Finite(input.CursorPhysical.Y)
            || !Positive(input.DpiScale) || !Enum.IsDefined(input.Mode)) return false;

        var margin = _toleranceLogical * input.DpiScale;
        if (!Finite(margin)) return false;
        if (input.Mode == PopupContentMode.Card)
        {
            var left = bounds.Left - margin;
            var top = bounds.Top - margin;
            var right = bounds.Right + margin;
            var bottom = bounds.Bottom + margin;
            if (!Finite(left) || !Finite(top) || !Finite(right) || !Finite(bottom)) return false;
            inside = input.CursorPhysical.X >= left && input.CursorPhysical.X <= right
                && input.CursorPhysical.Y >= top && input.CursorPhysical.Y <= bottom;
            return true;
        }

        // The radial menu remains circular when its host has been capped to a non-square working area.
        var radius = Math.Min(bounds.Width, bounds.Height) / 2 + margin;
        if (!Positive(radius)) return false;
        var dx = input.CursorPhysical.X - (bounds.Left + bounds.Width / 2);
        var dy = input.CursorPhysical.Y - (bounds.Top + bounds.Height / 2);
        if (Math.Abs(dx) > radius || Math.Abs(dy) > radius) return true;
        // Normalize before squaring to avoid overflow with large virtual-screen coordinates.
        inside = (dx / radius) * (dx / radius) + (dy / radius) * (dy / radius) <= 1;
        return true;
    }

    private static bool Finite(double value) => double.IsFinite(value);
    private static bool Positive(double value) => Finite(value) && value > 0;
}
