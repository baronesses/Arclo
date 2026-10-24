using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Orbit.App.Platform;

/// <summary>Opt-in Explorer integration for the current user; no administrator rights are required.</summary>
public static class ShellIntegration
{
    private static readonly string[] MenuKeyPaths =
    {
        @"Software\Classes\*\shell\OrbitToolkit",
        @"Software\Classes\Directory\shell\OrbitToolkit"
    };
    private const uint ShcneAssocChanged = 0x08000000;

    public static bool IsEnabled()
    {
        // A legacy file-only registration is intentionally unchecked so Enable upgrades both branches.
        var fileTarget = RegisteredExecutable(MenuKeyPaths[0]);
        var directoryTarget = RegisteredExecutable(MenuKeyPaths[1]);
        return fileTarget is not null && directoryTarget is not null
            && string.Equals(fileTarget, directoryTarget, StringComparison.OrdinalIgnoreCase);
    }

    private static string? RegisteredExecutable(string menuKeyPath)
    {
        using var commandKey = Registry.CurrentUser.OpenSubKey($@"{menuKeyPath}\command");
        if (commandKey?.GetValue(null) is not string command)
        {
            return null;
        }

        const string suffix = "\" \"%1\"";
        if (!command.StartsWith('"') || !command.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }

        var executablePath = command[1..^suffix.Length];
        if (executablePath.Length == 0 || executablePath.Contains('"')
            || !Path.IsPathFullyQualified(executablePath)
            || !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(executablePath)) return null;
        return Path.GetFullPath(executablePath);
    }

    public static void Enable(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        if (exePath.Contains('"'))
        {
            throw new ArgumentException(global::Orbit.Core.Localization.L.Get("Text_B64B7AD0CC7D"), nameof(exePath));
        }

        var fullPath = Path.GetFullPath(exePath);
        if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(global::Orbit.Core.Localization.L.Get("Text_714B0FECD1B7"), nameof(exePath));
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(global::Orbit.Core.Localization.L.Get("Text_43AB37D743D0"), fullPath);
        }

        foreach (var menuKeyPath in MenuKeyPaths)
        {
            using var menuKey = Registry.CurrentUser.CreateSubKey(menuKeyPath, writable: true)
                ?? throw new InvalidOperationException(global::Orbit.Core.Localization.L.Get("Text_DC3A7A03E9ED"));
            using var commandKey = menuKey.CreateSubKey("command", writable: true)
                ?? throw new InvalidOperationException(global::Orbit.Core.Localization.L.Get("Text_4F258B888BC7"));

            // %1 is quoted so spaces and shell metacharacters remain part of the file/folder argument.
            commandKey.SetValue(null, $"\"{fullPath}\" \"%1\"", RegistryValueKind.String);
            menuKey.SetValue(null, global::Orbit.Core.Localization.L.Get("Text_C48D70FED351"), RegistryValueKind.String);
            menuKey.SetValue("Icon", $"\"{fullPath}\",0", RegistryValueKind.String);
            menuKey.SetValue("MultiSelectModel", "Single", RegistryValueKind.String);
        }
        NotifyExplorer();
    }

    public static void Disable()
    {
        foreach (var menuKeyPath in MenuKeyPaths)
            Registry.CurrentUser.DeleteSubKeyTree(menuKeyPath, throwOnMissingSubKey: false);
        NotifyExplorer();
    }

    private static void NotifyExplorer() =>
        SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
