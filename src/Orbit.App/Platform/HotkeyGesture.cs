using System;
using System.Collections.Generic;
using System.Globalization;

namespace Orbit.App.Platform;

[Flags]
public enum HotkeyModifiers : uint { None = 0, Alt = 1, Control = 2, Shift = 4 }

/// <summary>Serializable global shortcut; ToString is its stable settings representation.</summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey)
{
    public static HotkeyGesture Default => new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x20);
    public string Display => ToString().Replace("+", " + ", StringComparison.Ordinal);

    public static HotkeyGesture Parse(string text)
    {
        if (!TryParse(text, out var gesture, out var error)) throw new ArgumentException(error, nameof(text));
        return gesture;
    }

    public static bool TryParse(string? text, out HotkeyGesture gesture) => TryParse(text, out gesture, out _);

    public static bool TryParse(string? text, out HotkeyGesture gesture, out string? error)
    {
        gesture = default;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return Fail(global::Orbit.Core.Localization.L.Get("Text_479A314C8305"), out error);
        var modifiers = HotkeyModifiers.None;
        uint key = 0;
        var hasKey = false;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0) return Fail(global::Orbit.Core.Localization.L.Get("Text_839039794435"), out error);
            var modifier = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => HotkeyModifiers.Control,
                "ALT" => HotkeyModifiers.Alt,
                "SHIFT" => HotkeyModifiers.Shift,
                _ => HotkeyModifiers.None
            };
            if (modifier != HotkeyModifiers.None)
            {
                if ((modifiers & modifier) != 0) return Fail(global::Orbit.Core.Localization.L.Get("Text_84969B302E4D"), out error);
                modifiers |= modifier;
                continue;
            }
            if (part.Equals("WIN", StringComparison.OrdinalIgnoreCase) || part.Equals("WINDOWS", StringComparison.OrdinalIgnoreCase))
                return Fail(global::Orbit.Core.Localization.L.Get("Text_6C633A79A355"), out error);
            if (hasKey || !TryParseKey(part, out key)) return Fail(global::Orbit.Core.Localization.L.Get("Text_8D33FFE0AED1"), out error);
            hasKey = true;
        }
        var candidate = new HotkeyGesture(modifiers, key);
        if (!hasKey) return Fail(global::Orbit.Core.Localization.L.Get("Text_455BA5492782"), out error);
        if (!candidate.TryValidate(out error)) return false;
        gesture = candidate;
        return true;
    }

    public bool TryValidate(out string? error)
    {
        error = null;
        if (Modifiers == HotkeyModifiers.None || (Modifiers & ~(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift)) != 0)
            return Fail(global::Orbit.Core.Localization.L.Get("Text_0AF274A52105"), out error);
        if (!SupportedKey(VirtualKey)) return Fail(global::Orbit.Core.Localization.L.Get("Text_B773AAD32E11"), out error);
        if (VirtualKey == 0x7B) return Fail(global::Orbit.Core.Localization.L.Get("Text_3345CF252AC7"), out error);
        if (VirtualKey == 0x73 && (Modifiers & HotkeyModifiers.Alt) != 0 && (Modifiers & HotkeyModifiers.Control) == 0)
            return Fail(global::Orbit.Core.Localization.L.Get("Text_F8B04C5C0460"), out error);
        if (VirtualKey == 0x20 && (Modifiers & HotkeyModifiers.Alt) != 0 && (Modifiers & HotkeyModifiers.Control) == 0)
            return Fail(global::Orbit.Core.Localization.L.Get("Text_117CA9433D54"), out error);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & HotkeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((Modifiers & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((Modifiers & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
        parts.Add(VirtualKey switch
        {
            0x20 => "Space",
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)VirtualKey).ToString(),
            >= 0x70 and <= 0x87 => "F" + (VirtualKey - 0x6F).ToString(CultureInfo.InvariantCulture),
            _ => "Unknown"
        });
        return string.Join("+", parts);
    }

    private static bool TryParseKey(string text, out uint key)
    {
        key = 0;
        var upper = text.ToUpperInvariant();
        if (upper is "SPACE" or "ПРОБЕЛ") { key = 0x20; return true; }
        if (upper.Length == 1 && (upper[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) { key = upper[0]; return true; }
        if (upper.Length is >= 2 and <= 3 && upper[0] == 'F'
            && int.TryParse(upper.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var function)
            && function is >= 1 and <= 24 && upper == "F" + function.ToString(CultureInfo.InvariantCulture))
        { key = (uint)(0x6F + function); return true; }
        return false;
    }

    private static bool SupportedKey(uint key) => key == 0x20 || key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x70 and <= 0x87;
    private static bool Fail(string message, out string? error) { error = message; return false; }
}

/// <summary>Separates global registration from its state transitions so tests never reserve real user shortcuts.</summary>
internal interface IHotkeyRegistrar
{
    bool Register(IntPtr hwnd, int id, uint modifiers, uint virtualKey, out int error);
    bool Unregister(IntPtr hwnd, int id, out int error);
}

internal sealed class HotkeyRegistration : IDisposable
{
    private const uint NoRepeat = 0x4000;
    private readonly IHotkeyRegistrar _registrar;
    private readonly Dictionary<int, HotkeyGesture> _ownedIds = new();
    private IntPtr _handle;
    private int _nextId = 0x4F52;
    private bool _disposed;
    public HotkeyGesture Current { get; private set; }
    public bool Enabled { get; private set; } = true;
    public int? ActiveId { get; private set; }
    public bool IsRegistered => ActiveId.HasValue;
    public int ErrorCode { get; private set; }

    public HotkeyRegistration(IHotkeyRegistrar registrar, HotkeyGesture initial)
    {
        if (!initial.TryValidate(out var error)) throw new ArgumentException(error, nameof(initial));
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        Current = initial;
    }

    public void Attach(IntPtr handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (handle == IntPtr.Zero) throw new ArgumentException(global::Orbit.Core.Localization.L.Get("Text_1B7E2DC12662"), nameof(handle));
        if (_handle != IntPtr.Zero && _handle != handle) throw new InvalidOperationException(global::Orbit.Core.Localization.L.Get("Text_2B1516517E6B"));
        _handle = handle;
        if (Enabled && !IsRegistered) RegisterCurrent();
    }

    public void SetEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Enabled == enabled) return;
        Enabled = enabled;
        ErrorCode = 0;
        if (!enabled)
        {
            var previous = ActiveId;
            ActiveId = null; // Stop dispatching even if native cleanup fails.
            if (previous.HasValue) Release(previous.Value);
        }
        else if (_handle != IntPtr.Zero)
        {
            // A failed native unregister can leave our own paused combination reserved.
            // Reuse that known registration instead of mistaking it for another app's conflict.
            foreach (var retained in _ownedIds)
            {
                if (retained.Value != Current) continue;
                ActiveId = retained.Key;
                return;
            }
            RegisterCurrent();
        }
    }

    public bool TryChange(HotkeyGesture gesture, out string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!gesture.TryValidate(out error)) return false;
        if (gesture == Current && (!Enabled || IsRegistered)) { ErrorCode = 0; return true; }
        if (_handle == IntPtr.Zero) { Current = gesture; ErrorCode = 0; return true; }
        var candidateId = NextId();
        if (!_registrar.Register(_handle, candidateId, (uint)gesture.Modifiers | NoRepeat, gesture.VirtualKey, out var nativeError))
        {
            ErrorCode = nativeError;
            error = global::Orbit.Core.Localization.L.Get("Text_BEE2315550E2") + gesture.Display + global::Orbit.Core.Localization.L.Get("Text_F3EA60DC9E50");
            return false;
        }
        _ownedIds.Add(candidateId, gesture);
        var oldId = ActiveId;
        // Commit only after the new native registration succeeded; stale WM_HOTKEY IDs stop dispatching here.
        Current = gesture;
        ActiveId = Enabled ? candidateId : null;
        ErrorCode = 0;
        if (oldId.HasValue) Release(oldId.Value);
        if (!Enabled) Release(candidateId); // A paused edit validates availability without enabling the shortcut.
        error = null;
        return true;
    }

    public bool ShouldDispatch(int id) => !_disposed && Enabled && ActiveId == id;

    private void RegisterCurrent() => TryChange(Current, out _);
    private int NextId()
    {
        for (var count = 0; count < 0xBFFF; count++)
        {
            var id = _nextId++;
            if (_nextId > 0xBFFF) _nextId = 0x4F52;
            if (!_ownedIds.ContainsKey(id)) return id;
        }
        throw new InvalidOperationException(global::Orbit.Core.Localization.L.Get("Text_AAFAC591C837"));
    }
    private void Release(int id)
    {
        if (_registrar.Unregister(_handle, id, out _)) _ownedIds.Remove(id);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ActiveId = null;
        foreach (var id in new List<int>(_ownedIds.Keys)) Release(id);
    }
}
