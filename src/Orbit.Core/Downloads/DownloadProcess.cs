using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Orbit.Core.Downloads;

internal static class DownloadProcess
{
    internal static async Task<(int ExitCode, string Output, string Error)> RunAsync(string executable, IEnumerable<string> arguments,
        string working, CancellationToken token, Action<string>? line = null, int outputLimit = 4 * 1024 * 1024)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false), WorkingDirectory = working };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["TEMP"] = working;start.Environment["TMP"] = working;
        start.Environment["DENO_DIR"] = Path.Combine(working, "deno-cache");start.Environment["DENO_NO_UPDATE_CHECK"] = "1";
        start.Environment["PYTHONNOUSERSITE"] = "1";start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";start.Environment["PYTHONUTF8"] = "1";
        start.Environment["NO_COLOR"] = "1";
        Process? process = null;OwnedJob? job = null;
        using var failure = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? outputTask = null, errorTask = null;
        try
        {
            var launching = Task.Run(() => Process.Start(start) ?? throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_5AC312EE2218")), CancellationToken.None);
            try { process = await launching.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
            catch
            {
                _ = launching.ContinueWith(t => { if (t.IsCompletedSuccessfully) { Stop(t.Result);t.Result.Dispose(); } else _ = t.Exception; }, TaskScheduler.Default);
                throw;
            }
            job = new OwnedJob(process);
            process.StandardInput.Close();
            var output = new StringBuilder();var error = new StringBuilder();
            outputTask = ReadAsync(process.StandardOutput, output, outputLimit, line, failure);
            errorTask = ReadAsync(process.StandardError, error, 256 * 1024, line, failure);
            await process.WaitForExitAsync(failure.Token).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            return (process.ExitCode, output.ToString(), error.ToString());
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            if (outputTask?.Exception is { } outputError) throw outputError.GetBaseException();
            if (errorTask?.Exception is { } errorError) throw errorError.GetBaseException();
            throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_86D7B81C3122"));
        }
        finally
        {
            failure.Cancel();job?.Dispose();
            if (process is not null)
            {
                Stop(process);
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }
                if (outputTask is not null && errorTask is not null)
                    try { await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
                process.Dispose();
            }
        }
    }

    private static async Task ReadAsync(StreamReader reader, StringBuilder text, int limit, Action<string>? line, CancellationTokenSource failure)
    {
        try
        {
            var buffer = new char[4096];var pending = new StringBuilder();int total = 0;
            while (true)
            {
                int count = await reader.ReadAsync(buffer.AsMemory(), failure.Token).ConfigureAwait(false);
                if (count == 0) break;
                total += count;if (total > limit) throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_DDCD4194D931"));
                text.Append(buffer, 0, count);
                foreach (var c in buffer.AsSpan(0, count))
                {
                    if (c is '\r' or '\n') { if (pending.Length > 0) { line?.Invoke(pending.ToString());pending.Clear(); } }
                    else { pending.Append(c);if (pending.Length > 65536) throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_5FEAC959007E")); }
                }
            }
            if (pending.Length > 0) line?.Invoke(pending.ToString());
        }
        catch { failure.Cancel();throw; }
    }

    private static void Stop(Process process) { try { if (!process.HasExited) process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { } }

    // A kill-on-close job also owns FFmpeg/Deno descendants if the yt-dlp parent exits first.
    private sealed class OwnedJob : IDisposable
    {
        private SafeFileHandle? _handle;
        internal OwnedJob(Process process)
        {
            if (!OperatingSystem.IsWindows()) return;
            _handle = CreateJobObjectW(IntPtr.Zero, null);
            var limits = new JobExtended();limits.Basic.Flags = 0x2000;
            if (_handle.IsInvalid || !SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<JobExtended>())
                || !AssignProcessToJobObject(_handle, process.Handle))
            { _handle.Dispose();_handle = null;Stop(process);throw new DownloadException(global::Orbit.Core.Localization.L.Get("Text_EEE6731C1BD4")); }
        }
        public void Dispose() { _handle?.Dispose();_handle = null; }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime;public uint Flags;public UIntPtr MinimumWorkingSet, MaximumWorkingSet;public uint ActiveProcessLimit;public UIntPtr Affinity;public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct JobExtended { public BasicLimits Basic;public IoCounters Io;public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref JobExtended info, uint length);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    }
}
