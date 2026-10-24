using System;
using System.IO;
using System.Security;

namespace Orbit.App.Settings;

/// <summary>User preferences. Normalize before applying values or persisting a DTO.</summary>
public sealed record OrbitSettings
{
    public double CompactSize { get; init; } = 380;
    public string Theme { get; init; } = "liquid-glass-crimson";
    public int AppearanceVersion { get; init; } = 1;
    public string? OutputDirectory { get; init; }
    public bool ReducedTransparency { get; init; }
    public string Shortcut { get; init; } = "Ctrl+Alt+Space";
    public bool KeepHistory { get; init; } = true;
    public bool WelcomeCompleted { get; init; }
    public string Language { get; init; } = "auto";

    public OrbitSettings Normalize()
    {
        string? directory=null;
        if(!string.IsNullOrWhiteSpace(OutputDirectory))
        {
            try { directory=Path.GetFullPath(OutputDirectory.Trim()); }
            catch(Exception exception)when(exception is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
            {
                // A broken optional path should not prevent the remaining preferences from loading.
            }
        }
        return this with { Language=Language is "auto" or "ru" or "en"?Language:"auto",Theme=Theme is "light" or "liquid-glass-crimson"?Theme:"liquid-glass-crimson",CompactSize=CompactSize is 320 or 360 or 380 or 440 or 520?CompactSize:380,OutputDirectory=directory,Shortcut=string.IsNullOrWhiteSpace(Shortcut)||Shortcut.Length>128?"Ctrl+Alt+Space":Shortcut.Trim() };
    }
}
