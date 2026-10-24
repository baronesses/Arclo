using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Orbit.App.Controls;
using Orbit.Core;

namespace Orbit.App;

public sealed partial class MainWindow
{
    /// <summary>Internal folder integration diagnostic. No synthetic input, registry writes or preference writes.</summary>
    private async Task RunFolderSmokeAsync()
    {
        var destination = Path.Combine(AppContext.BaseDirectory, "folder-smoke");
        var fixtures = Path.Combine(destination, "fixtures");
        var output = Path.Combine(destination, "output");
        var checks = new List<string>();
        void Require(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Folder integration: " + name);
            checks.Add(name);
        }
        static string Digest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        try
        {
            Directory.CreateDirectory(fixtures);
            Directory.CreateDirectory(output);
            File.Delete(Path.Combine(destination, "passed.json"));
            File.Delete(Path.Combine(destination, "error.txt"));
            Require(_args.Contains("--folder-smoke") && IsDiagnosticRun && _bridge is null,
                "isolated folder diagnostic has no global bridge or preferences");
            _outputDirectory = output; // Diagnostic output is intentionally not persisted.
            var folder = Directory.CreateDirectory(Path.Combine(fixtures, "Папка пример.png")).FullName;
            var nested = Directory.CreateDirectory(Path.Combine(folder, "nested"));
            Directory.CreateDirectory(Path.Combine(folder, "пустая папка"));
            var nestedFile = Path.Combine(nested.FullName, "данные.txt");
            var looseFile = Path.Combine(fixtures, "отдельный файл.txt");
            File.WriteAllText(nestedFile, "Orbit folder fixture ✓", new UTF8Encoding(false));
            File.WriteAllText(looseFile, "Orbit loose file fixture", new UTF8Encoding(false));
            var originalNestedHash = Digest(nestedFile);
            var originalLooseHash = Digest(looseFile);
            var sourceEntries = Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(folder, path)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            var rootName = Path.GetFileName(folder);
            var nestedEntry = rootName + "/nested/данные.txt";
            var emptyEntry = rootName + "/пустая папка/";

            Require(InputArguments(new[] { folder }).SequenceEqual(new[] { folder }), "directory command argument is retained");
            Require(await LoadFilesAsync(new[] { folder }), "folder with image-like extension loads successfully");
            Require(_selectionKind == ToolkitKind.Folder && _metadata?.Kind == ToolkitKind.Folder,
                "directory type wins over .png extension");
            Require(_metadata?.Path == folder && _files.SequenceEqual(new[] { folder }), "folder metadata preserves full selected path");
            Require(_preview is not null && _radial.Preview.Source is not null, "folder receives an actual non-null folder preview");
            var actions = _radial.Children.OfType<SectorButton>().Select(button => button.Action).ToArray();
            Require(_radial.ActionCount == 3 && actions.OrderBy(action => (int)action)
                .SequenceEqual(new[] { ImageAction.ArchiveZip, ImageAction.FileHash, ImageAction.FileInfo }.OrderBy(action => (int)action)),
                "folder menu contains exactly ZIP, SHA-256 and information");
            if (!_compact) SetCompactMode(true);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            Capture(Path.Combine(destination, "folder-radial.png"));

            int jobsBefore = _jobs.Count;
            HandleAction(ImageAction.Jpg);
            Require(_jobs.Count == jobsBefore && _compact && _panel.Visibility == Visibility.Collapsed,
                "unsupported JPEG action cannot enqueue or open an image card for a folder");

            async Task<ImageJobResult> Execute(ImageAction action)
            {
                int before = _jobs.Count;
                HandleAction(action);
                await Task.WhenAll(_jobTasks);
                Require(_jobs.Count == before + 1, "exactly one real queued job for " + action);
                var job = _jobs[^1];
                Require(job.Finished && job.Result is not null && File.Exists(job.Result.OutputPath),
                    "job completed with a real output for " + action);
                Require(_compact && _panel.Visibility == Visibility.Collapsed,
                    "folder action remains in compact radial mode for " + action);
                return job.Result!;
            }

            var archiveResult = await Execute(ImageAction.ArchiveZip);
            using (var archive = ZipFile.OpenRead(archiveResult.OutputPath))
            {
                Require(archive.Entries.Any(entry => entry.FullName == nestedEntry), "ZIP preserves folder root and nested relative filename");
                Require(archive.Entries.Any(entry => entry.FullName == emptyEntry && entry.Length == 0), "ZIP retains the empty directory explicitly");
                using var content = new StreamReader(archive.GetEntry(nestedEntry)!.Open(), Encoding.UTF8);
                Require(await content.ReadToEndAsync() == File.ReadAllText(nestedFile), "ZIP nested file contains exact original Unicode text");
            }
            var hashResult = await Execute(ImageAction.FileHash);
            var hashText = File.ReadAllText(hashResult.OutputPath);
            Require(hashResult.Format == "TXT" && hashResult.OutputPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase),
                "folder SHA-256 creates one TXT report");
            Require(hashText.Contains(originalNestedHash + "  " + nestedEntry, StringComparison.Ordinal),
                "SHA-256 matches real source bytes and folder-relative path");
            Require(hashText.Contains("# folder: " + emptyEntry, StringComparison.Ordinal),
                "SHA-256 report documents the empty directory");

            HandleAction(ImageAction.FileInfo);
            Require(_compact && _panel.Visibility == Visibility.Visible && _radialView.Visibility == Visibility.Collapsed,
                "folder information opens a compact card");
            Require(_panelContent.Children.OfType<TextBlock>().Any(text => text.Text.Contains(folder, StringComparison.Ordinal)),
                "folder information card displays the actual full path");
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            Capture(Path.Combine(destination, "folder-info.png"));
            ClosePanel();

            Require(await LoadFilesAsync(new[] { looseFile, folder }), "mixed file and folder selection loads");
            Require(_selectionKind == ToolkitKind.Mixed && _radial.ActionCount == 3, "mixed file-folder menu exposes only the three common actions");
            var mixedArchive = await Execute(ImageAction.ArchiveZip);
            using (var archive = ZipFile.OpenRead(mixedArchive.OutputPath))
                Require(archive.GetEntry(Path.GetFileName(looseFile)) is not null && archive.GetEntry(nestedEntry) is not null
                    && archive.Entries.Count(entry => !entry.FullName.EndsWith('/')) == 2,
                    "mixed selection produces one ZIP containing both roots exactly once");
            HandleAction(ImageAction.FileInfo);
            Require(_compact && _panel.Visibility == Visibility.Visible, "mixed file-folder information remains compact");
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            Capture(Path.Combine(destination, "mixed-folder-info.png"));
            ClosePanel();

            Require(await LoadFilesAsync(new[] { folder, nestedFile }), "parent plus explicitly selected descendant loads");
            var overlapArchive = await Execute(ImageAction.ArchiveZip);
            using (var archive = ZipFile.OpenRead(overlapArchive.OutputPath))
                Require(archive.Entries.Count(entry => !entry.FullName.EndsWith('/')) == 1
                    && archive.Entries.Count(entry => entry.FullName == nestedEntry) == 1,
                    "parent plus descendant selection copies the nested file only once");

            Require(!await LoadFilesAsync(new[] { Path.Combine(fixtures, "несуществующая папка") }), "missing folder reports load failure");
            Require(_files.Count == 0 && _metadata is null && _preview is null && _selectionMetadata.Count == 0,
                "failed folder load clears all previous selection and metadata");
            jobsBefore = _jobs.Count;
            HandleAction(ImageAction.ArchiveZip);
            HandleAction(ImageAction.FileHash);
            Require(_jobs.Count == jobsBefore, "failed folder load cannot archive or hash the old folder");
            Require(Digest(nestedFile) == originalNestedHash && Digest(looseFile) == originalLooseHash,
                "all original file bytes remain unchanged after folder jobs");
            Require(Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(folder, path)).OrderBy(path => path, StringComparer.Ordinal).SequenceEqual(sourceEntries),
                "original folder layout and empty directory remain unchanged");
            File.WriteAllText(Path.Combine(destination, "passed.json"), JsonSerializer.Serialize(new
            {
                Passed = checks.Count,
                Checks = checks,
                PhysicalHotkeyTest = false,
                RegistryWrites = false,
                SyntheticInput = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "error.txt"), exception.ToString());
        }
        finally { Quit(); }
    }
}
