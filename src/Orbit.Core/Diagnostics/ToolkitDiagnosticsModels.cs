using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

[assembly: InternalsVisibleTo("Orbit.Diagnostics.Tests")]
namespace Orbit.Core.Diagnostics;

public enum ToolkitDiagnosticState { Ready, Missing, Error }
public sealed record ToolkitDiagnosticComponent(string Id, string Name, ToolkitDiagnosticState State, string? Path, string Version, string Detail);
public sealed record ToolkitDiagnosticsReport(DateTimeOffset GeneratedUtc, IReadOnlyList<ToolkitDiagnosticComponent> Components)
{
    public static string ScopeNotice => global::Orbit.Core.Localization.L.Get("Text_299CD28D12BA");
    public string ToText()
    {
        var text = new StringBuilder(global::Orbit.Core.Localization.L.Get("Text_6ED27A3770ED"));
        text.Append("UTC: ").AppendLine(GeneratedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        text.AppendLine(ScopeNotice).AppendLine();
        foreach (var component in Components)
        {
            text.Append('[').Append(component.State switch { ToolkitDiagnosticState.Ready => global::Orbit.Core.Localization.L.Get("Text_B3695D2693F0"), ToolkitDiagnosticState.Missing => global::Orbit.Core.Localization.L.Get("Text_FD7CD80AD29B"), _ => global::Orbit.Core.Localization.L.Get("Text_4E395375DE3E") }).Append("] ").AppendLine(Clean(component.Name));
            text.Append(global::Orbit.Core.Localization.L.Get("Text_03FB8EA87DD6")).AppendLine(Clean(component.Path ?? global::Orbit.Core.Localization.L.Get("Text_4B19FD03E863")));
            if (component.Version.Length != 0) text.Append(global::Orbit.Core.Localization.L.Get("Text_06629EFECAAD")).AppendLine(Clean(component.Version));
            if (component.Detail.Length != 0) text.AppendLine(Clean(component.Detail)); text.AppendLine();
        }
        return text.ToString();
    }
    private static string Clean(string value) => new(value.Select(character => char.IsControl(character) || character is '\u2028' or '\u2029' ? ' ' : character).ToArray());
}

// Test-only specs cannot be supplied through the App's public constructor or serialized UI state.
internal sealed record DiagnosticToolSpec(string Id, string Name, Func<string?> ResolvePath, IReadOnlyList<string> Arguments, string VersionMarker,
    bool IncludeOcrLanguages = false, string? SamePathAs = null);
