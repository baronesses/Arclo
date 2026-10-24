using System.Globalization;
using System.Resources;

namespace Orbit.Core.Localization;

/// <summary>Keyed UI resources. User names, paths, file data and operation identifiers never pass through this API.</summary>
public static class L
{
    private static readonly ResourceManager Resources = new("Orbit.Core.Localization.Strings", typeof(L).Assembly);
    private static readonly CultureInfo InitialUiCulture = CultureInfo.CurrentUICulture;
    private static string _language = "auto";
    private static CultureInfo _culture = CultureInfo.GetCultureInfo(ResolveLanguage("auto", InitialUiCulture));

    public static string Language => _language;
    public static string EffectiveLanguage => _culture.TwoLetterISOLanguageName;
    public static CultureInfo Culture => _culture;
    public static event EventHandler? LanguageChanged;

    public static string NormalizeLanguage(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        "ru" => "ru", "en" => "en", _ => "auto"
    };

    public static string ResolveLanguage(string? language, CultureInfo? systemCulture = null)
    {
        var normalized = NormalizeLanguage(language);
        if (normalized != "auto") return normalized;
        return (systemCulture ?? InitialUiCulture).TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";
    }

    public static void SetLanguage(string? language)
    {
        string normalized = NormalizeLanguage(language);
        var culture = CultureInfo.GetCultureInfo(ResolveLanguage(normalized, InitialUiCulture));
        bool changed = normalized != _language || culture.Name != _culture.Name;
        _language = normalized;
        _culture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        if (changed) LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Resources.GetString(key, _culture) ?? key;
    }

    public static string Format(string key, params object?[] args) => string.Format(_culture, Get(key), args);

    /// <summary>Original Russian resource for keyed UI metadata; this never translates arbitrary user text.</summary>
    public static string SourceText(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Resources.GetString(key, CultureInfo.GetCultureInfo("ru")) ?? key;
    }
}
