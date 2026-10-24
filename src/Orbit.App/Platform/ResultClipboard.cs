using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using FormsClipboard = System.Windows.Forms.Clipboard;
using FormsDataObject = System.Windows.Forms.DataObject;
using FormsTextDataFormat = System.Windows.Forms.TextDataFormat;

namespace Orbit.App.Platform;

public sealed record ResultClipboardResult(bool Success, string? Error, int CopiedCount = 0, bool Cancelled = false);

/// <summary>A single clipboard publication attempt. Inject a capture sink in diagnostics; never save/restore the user's clipboard.</summary>
public interface IResultClipboardSink
{
    void SetDataObject(FormsDataObject payload, bool copy);
}

internal interface IResultClipboardPathValidator
{
    SelectedPathsResult Validate(IReadOnlyList<string> paths, CancellationToken cancellationToken);
}

/// <summary>Explicit copy commands only. Validate the complete selection before touching the clipboard; never cut or move sources.</summary>
public sealed class ResultClipboard
{
    public const int MaximumPaths = 100;
    public const int MaximumAttempts = 4;
    public const string PreferredDropEffectFormat = "Preferred DropEffect";
    private const int MaximumPathLength = 32768;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(3);
    private readonly Dispatcher _dispatcher;
    private readonly IResultClipboardSink _sink;
    private readonly IResultClipboardPathValidator _pathValidator;
    private readonly TimeSpan _validationTimeout;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _validationGate = new(1, 1);

    /// <summary>Construction performs no clipboard or filesystem I/O. The dispatcher must belong to the WPF STA thread.</summary>
    public ResultClipboard(Dispatcher ownerDispatcher, IResultClipboardSink? sink = null)
        : this(ownerDispatcher, sink, FilesystemPathValidator.Instance, ValidationTimeout) { }

    // Source-linked diagnostics can simulate an uninterruptible filesystem query without using a network path.
    internal ResultClipboard(Dispatcher ownerDispatcher, IResultClipboardSink? sink,
        IResultClipboardPathValidator pathValidator, TimeSpan validationTimeout)
    {
        if (validationTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(validationTimeout));
        _dispatcher = ownerDispatcher ?? throw new ArgumentNullException(nameof(ownerDispatcher));
        _sink = sink ?? SystemClipboardSink.Instance;
        _pathValidator = pathValidator ?? throw new ArgumentNullException(nameof(pathValidator));
        _validationTimeout = validationTimeout;
        UsesSystemClipboard = sink is null;
    }

    /// <summary>Diagnostic UI must refuse a default instance even after an explicit diagnostic button click.</summary>
    public bool UsesSystemClipboard { get; }

    public Task<ResultClipboardResult> CopyFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        => CopyAsync(paths, pathText: false, cancellationToken);

    public Task<ResultClipboardResult> CopyPathTextAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        => CopyAsync(paths, pathText: true, cancellationToken);

    private async Task<ResultClipboardResult> CopyAsync(IReadOnlyList<string> paths, bool pathText, CancellationToken cancellationToken)
    {
        bool entered = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (paths is null || paths.Count == 0) return Failure(global::Orbit.Core.Localization.L.Get("Text_3132ED5F6C26"));
            int count = paths.Count;
            if (count > MaximumPaths) return Failure(global::Orbit.Core.Localization.L.Format("Text_DA7469AA82AD",(object?)(MaximumPaths)));
            var requested = new string[count];
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = paths[index];
                if (path is null || path.Length > MaximumPathLength) return Failure(global::Orbit.Core.Localization.L.Get("Text_B07B5AD7E1AA"));
                requested[index] = path;
            }
            if (_dispatcher.Thread.GetApartmentState() != ApartmentState.STA)
                return Failure(global::Orbit.Core.Localization.L.Get("Text_56CDC9A72631"));
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            for (int attempt = 0; attempt < MaximumAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
                    return Failure(global::Orbit.Core.Localization.L.Get("Text_791C4DBC4C69"));
                // Recheck after each busy delay too: a deleted result must not be published by a later retry.
                if (!await _validationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                    return Failure(global::Orbit.Core.Localization.L.Get("Text_40DCE0113A2B"));
                var selection = await ValidateWithRetainedWorkerAsync(requested, cancellationToken).ConfigureAwait(false);
                if (!selection.Success || selection.RejectedCount > 0)
                    return Failure(selection.Error ?? global::Orbit.Core.Localization.L.Get("Text_C42A0AC7F69E"));
                try
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var payload = CreatePayload(selection.Paths, pathText);
                        _sink.SetDataObject(payload, copy: true);
                    }, DispatcherPriority.Normal, cancellationToken).Task.ConfigureAwait(false);
                    return new ResultClipboardResult(true, null, selection.Paths.Count);
                }
                catch (ExternalException)
                {
                    if (attempt == MaximumAttempts - 1)
                        return Failure(global::Orbit.Core.Localization.L.Get("Text_6CB2623CCEC8"));
                    // Yield instead of sleeping inside a native clipboard API or on the WPF dispatcher.
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
            return Failure(global::Orbit.Core.Localization.L.Get("Text_45972801DFF7"));
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested
                ? new ResultClipboardResult(false, global::Orbit.Core.Localization.L.Get("Text_16E7F29AA79D"), Cancelled: true)
                : Failure(global::Orbit.Core.Localization.L.Get("Text_791C4DBC4C69"));
        }
        catch (TimeoutException) { return Failure(global::Orbit.Core.Localization.L.Get("Text_C95B84099131")); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException
            or ArgumentException or NotSupportedException or InvalidOperationException or ThreadStateException)
        { return Failure(global::Orbit.Core.Localization.L.Get("Text_35FEEF7E2BDE")); }
        finally { if (entered) _operationGate.Release(); }
    }

    private async Task<SelectedPathsResult> ValidateWithRetainedWorkerAsync(string[] requested, CancellationToken cancellationToken)
    {
        var validationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<SelectedPathsResult> worker;
        try { worker = Task.Run(() => _pathValidator.Validate(requested, validationCancellation.Token), CancellationToken.None); }
        catch
        {
            validationCancellation.Dispose();_validationGate.Release();throw;
        }
        try { return await worker.WaitAsync(_validationTimeout, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            validationCancellation.Cancel();
            throw;
        }
        finally
        {
            // File.Exists on an offline share cannot be forcibly cancelled. Return to the UI now, but
            // retain the single worker slot until that query actually exits; never start more behind it.
            _ = worker.ContinueWith(completed =>
            {
                _ = completed.Exception;
                validationCancellation.Dispose();
                _validationGate.Release();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static FormsDataObject CreatePayload(IReadOnlyList<string> paths, bool pathText)
    {
        var payload = new FormsDataObject();
        if (pathText)
        {
            payload.SetText(string.Join(Environment.NewLine, paths), FormsTextDataFormat.UnicodeText);
        }
        else
        {
            var files = new StringCollection();
            files.AddRange(paths.ToArray());
            payload.SetFileDropList(files);
            // CFSTR_PREFERREDDROPEFFECT is a DWORD in HGLOBAL. A MemoryStream preserves the raw bytes,
            // rather than publishing a serialized managed integer. DROPEFFECT_COPY = 1; never MOVE = 2.
            payload.SetData(PreferredDropEffectFormat, autoConvert: false, new MemoryStream(new byte[] { 1, 0, 0, 0 }, writable: false));
        }
        return payload;
    }

    private static ResultClipboardResult Failure(string message) => new(false, message);

    private sealed class FilesystemPathValidator : IResultClipboardPathValidator
    {
        public static readonly FilesystemPathValidator Instance = new();
        public SelectedPathsResult Validate(IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            var accepted = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int rejected = 0;string? firstError = null;
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SelectedPathPolicy.TryNormalizeExisting(path, out var normalized, out var error))
                {
                    if (seen.Add(normalized)) accepted.Add(normalized);
                }
                else { rejected++;firstError ??= error; }
                cancellationToken.ThrowIfCancellationRequested();
            }
            return new SelectedPathsResult(accepted.AsReadOnly(), accepted.Count > 0 ? null : firstError ?? global::Orbit.Core.Localization.L.Get("Text_3132ED5F6C26"), rejected);
        }
    }

    private sealed class SystemClipboardSink : IResultClipboardSink
    {
        public static readonly SystemClipboardSink Instance = new();
        public void SetDataObject(FormsDataObject payload, bool copy)
            // Disable Forms' synchronous retry loop; ResultClipboard owns cancellable async retries.
            => FormsClipboard.SetDataObject(payload, copy, retryTimes: 0, retryDelay: 0);
    }
}
