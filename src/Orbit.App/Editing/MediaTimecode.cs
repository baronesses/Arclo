using System;
using System.Globalization;
using System.Linq;

namespace Orbit.App.Editing;

internal static class MediaTimecode
{
    public static bool TryParse(string? value,out double seconds)
    {
        seconds=0;
        if(string.IsNullOrWhiteSpace(value))return false;
        var parts=value.Trim().Replace(',','.').Split(':');
        if(parts.Length is <1 or >3)return false;
        double total=0;
        for(int i=0;i<parts.Length;i++){
            var part=parts[i];
            if(part.Length==0||part.Any(c=>!char.IsAsciiDigit(c)&&c!='.')||
                (i<parts.Length-1&&part.Contains('.'))||
                !double.TryParse(part,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var number)||
                !double.IsFinite(number)||number<0)return false;
            if(parts.Length>1&&i>0&&number>=60)return false;
            total=total*60+number;
            if(!double.IsFinite(total)||total>int.MaxValue)return false;
        }
        seconds=total;return true;
    }

    public static string Format(double seconds)
    {
        if(!double.IsFinite(seconds)||seconds<0||seconds>int.MaxValue)throw new ArgumentOutOfRangeException(nameof(seconds));
        // Floor rather than round: a default end must never extend past the inspected duration.
        long milliseconds=(long)Math.Floor(seconds*1000);
        var time=TimeSpan.FromMilliseconds(milliseconds);
        return FormattableString.Invariant($"{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}");
    }
}
