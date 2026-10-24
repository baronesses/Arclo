using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Orbit.App.Appearance;

/// <summary>Updates legacy tool-card ink without touching image pixels or freezing shared brushes.</summary>
internal static class UiThemeBrushes
{
    private static OrbitTheme _theme;
    private static readonly List<(string Role,WeakReference<SolidColorBrush> Brush)> Active=new();
    public static SolidColorBrush Resolve(string value)
    {
        string role=Role(value);
        if(Active.Count>=1024&&Active.Count%256==0)
            Active.RemoveAll(entry=>!entry.Brush.TryGetTarget(out var old)||old.IsFrozen);
        var brush=new SolidColorBrush(ColorFor(role));
        Active.Add((role,new WeakReference<SolidColorBrush>(brush)));
        return brush;
    }
    public static void Apply(OrbitTheme theme)
    {
        _theme=theme;
        for(int index=Active.Count-1;index>=0;index--){
            var entry=Active[index];
            if(!entry.Brush.TryGetTarget(out var brush)||brush.IsFrozen){Active.RemoveAt(index);continue;}
            brush.Color=ColorFor(entry.Role);
        }
    }
    private static string Role(string value)=>value.ToUpperInvariant() switch {
        "#E2DDF9" or "#E2EAF6" or "#E8E4FF" or "#E9E3FF" or "#E9F2FF" or "#ECE5FF" or "#EDF3FF" or "#F0E8FF" or "#F0EDFF" or "#F5F8FF"=>"text",
        "#656B8C" or "#777A9E" or "#7F83A3" or "#858BAB" or "#878BAE" or "#8995AF" or "#8B91B0" or "#9293B7" or "#9CAAC1" or "#A2B3CF" or "#B5B5D0" or "#B8C7DD" or "#BAC6DE" or "#C6D8EF"=>"muted",
        "#8ED8BC" or "#A7D8C8" or "#B9E1D6"=>"success",
        "#EDACB9" or "#F2A6B3"=>"error",
        "#E3CCA3"=>"warning",
        "#9980F6" or "#9A82F4" or "#9A88FF" or "#B4A5F3" or "#B5A7FF" or "#B8A4FA" or "#B8A6FF" or "#C3B8EF"=>"accent",
        "#6552CF"=>"button",
        "#111E33" or "#202D45" or "#34425D" or "#E015192E"=>"surface",
        "#252841" or "#4B5F84"=>"track",
        "#34364C" or "#65819C" or "#6A9CAEC9" or "#9EE6EEFF" or "#9BE8F4FF" or "#244E658C" or "#A9EAF2FF" or "#B9C4F5"=>"edge",
        _=>value
    };
    private static Color ColorFor(string role)
    {
        if(SystemParameters.HighContrast)
            return (role is "surface" or "track" or "button"?SystemColors.WindowColor:SystemColors.WindowTextColor);
        bool dark=_theme==OrbitTheme.LiquidGlassCrimson;
        string color=role switch {
            "text"=>dark?"#F5F2F6":"#202630", "muted"=>dark?"#CCBBC5":"#606A76",
            "success"=>dark?"#A3E2C6":"#22644A", "error"=>dark?"#FFA7BB":"#A12D44",
            "warning"=>dark?"#F2CE98":"#845B17", "accent"=>dark?"#F19AAF":"#52677F",
            "button"=>dark?"#A5A3072E":"#DDE4EC", "surface"=>dark?"#6602060E":"#F2F5F8",
            "track"=>dark?"#55292A35":"#E4E9EF", "edge"=>dark?"#60E5C3D0":"#C8CFD8", _=>role
        };
        return (Color)ColorConverter.ConvertFromString(color);
    }
}
