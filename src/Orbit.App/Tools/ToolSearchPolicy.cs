using System;
using System.Globalization;
using System.Text;

namespace Orbit.App.Tools;

/// <summary>Pure, stateless matching for trusted tool labels and keyword aliases.</summary>
public static class ToolSearchPolicy
{
    public const int MaximumQueryLength = 256;

    /// <summary>
    /// Matches every query word against the label or keywords, in any order.
    /// Matching uses ordinal substrings after Unicode/case normalization, never fuzzy matching.
    /// Malformed or oversized queries do not match; empty queries match every tool.
    /// </summary>
    public static bool Match(string? query, string? label, string? keywords)
    {
        if (query is null) return true;
        if (query.Length > MaximumQueryLength || !TryNormalize(query, false, out var normalizedQuery)) return false;
        if (normalizedQuery.Length == 0) return true;

        var searchable = new StringBuilder();
        AppendSearchField(searchable, label);
        AppendSearchField(searchable, keywords);
        var haystack = searchable.ToString();
        foreach (var word in normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!haystack.Contains(word, StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>
    /// Returns FormKC/invariant-uppercase text with ё mapped to е and whitespace,
    /// dots, slashes and dashes collapsed to single spaces. Invalid UTF-16 returns empty.
    /// This method does not impose the query-length limit; Match does.
    /// </summary>
    public static string Normalize(string? text) => TryNormalize(text, false, out var value) ? value : string.Empty;

    private static void AppendSearchField(StringBuilder target, string? text)
    {
        if (!TryNormalize(text, false, out var spaced)) return;
        target.Append(' ').Append(spaced);
        // Preserve both spellings: SHA-256 / SHA256, TAR.GZ / TARGZ, Web-P / WEBP.
        if (TryNormalize(text, true, out var joined) && !string.Equals(spaced, joined, StringComparison.Ordinal))
            target.Append(' ').Append(joined);
    }

    private static bool TryNormalize(string? text, bool joinPunctuation, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(text)) return true;
        // Normalize throws for lone surrogates. Validate once so malformed registry fields
        // can be ignored independently, while a malformed query can fail closed.
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsHighSurrogate(character))
            {
                if (++index >= text.Length || !char.IsLowSurrogate(text[index])) return false;
            }
            else if (char.IsLowSurrogate(character)) return false;
        }

        var canonical = text.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        var result = new StringBuilder(canonical.Length);
        var pendingSpace = false;
        foreach (var character in canonical)
        {
            var punctuation = IsJoiner(character);
            if (punctuation && joinPunctuation) continue;
            if (char.IsWhiteSpace(character) || punctuation)
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace) result.Append(' ');
            result.Append(character == 'Ё' ? 'Е' : character);
            pendingSpace = false;
        }
        normalized = result.ToString();
        return true;
    }

    private static bool IsJoiner(char character) => character is '.' or '/' or '\\' or '-'
        || char.GetUnicodeCategory(character) == UnicodeCategory.DashPunctuation;
}
