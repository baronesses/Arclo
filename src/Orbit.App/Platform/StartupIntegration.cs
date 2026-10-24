using System;
using System.IO;
using Microsoft.Win32;

namespace Orbit.App.Platform;

/// <summary>Explicit current-user logon registration. Never invoked automatically on application startup.</summary>
public static class StartupIntegration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OrbitToolkit";

    /// <summary>Checks this Run entry's command; Windows may apply its own startup policy separately.</summary>
    public static bool IsEnabled(string? exePath = null)
    {
        var target = exePath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target)) return false;
        using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return MatchesCommand(run?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, target);
    }

    public static void Enable(string exePath)
    {
        var command = BuildCommand(exePath);
        if (!File.Exists(Path.GetFullPath(exePath))) throw new FileNotFoundException(global::Orbit.Core.Localization.L.Get("Text_21349CAEE632"), exePath);
        using var run = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException(global::Orbit.Core.Localization.L.Get("Text_BD2E24662DBE"));
        run.SetValue(ValueName, command, RegistryValueKind.String);
    }

    public static void Disable()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        run?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Pure command construction; tests do not write the registry or execute this command.</summary>
    public static string BuildCommand(string exePath)
    {
        var path = NormalizeExecutable(exePath);
        var command = "\"" + path + "\" --background";
        if (command.Length > 260) throw new ArgumentException(global::Orbit.Core.Localization.L.Get("Text_0D47851E04A2"), nameof(exePath));
        return command;
    }

    public static bool MatchesCommand(string? command, string exePath)
    {
        const string suffix = "\" --background";
        if (string.IsNullOrEmpty(command) || !command.StartsWith('"') || !command.EndsWith(suffix, StringComparison.Ordinal)) return false;
        try
        {
            var registered = NormalizeExecutable(command[1..^suffix.Length]);
            var expected = NormalizeExecutable(exePath);
            return command.Length <= 260 && string.Equals(registered, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }

    private static string NormalizeExecutable(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        if (!Path.IsPathFullyQualified(exePath) || exePath.Contains('"') || exePath.Contains('\0')
            || exePath.Contains('\r') || exePath.Contains('\n') || exePath.StartsWith(@"\\.\", StringComparison.Ordinal)
            || exePath.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new ArgumentException(global::Orbit.Core.Localization.L.Get("Text_BCF2F3763B08"), nameof(exePath));
        var path = Path.GetFullPath(exePath);
        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(global::Orbit.Core.Localization.L.Get("Text_9BF32D8C5F77"), nameof(exePath));
        return path;
    }
}
