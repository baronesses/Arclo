using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Orbit.Core.Documents;

// A private job caps memory before 7zr is allowed to parse any archive headers.
internal sealed class SevenZipNativeProcess : IAsyncDisposable
{
    private static readonly SemaphoreSlim Slots = new(2, 2);
    private readonly Process _process;
    private readonly SafeFileHandle _job;
    internal FileStream Output { get; }
    internal FileStream Error { get; }
    private FileStream? _input;
    private int _disposed;
    private SevenZipNativeProcess(Process process, SafeFileHandle job, FileStream output, FileStream error, FileStream? input)
    { _process = process; _job = job; Output = output; Error = error; _input = input; }

    internal static async Task<SevenZipNativeProcess> StartAsync(string executable, IReadOnlyList<string> arguments, string? directory, CancellationToken token, bool keepInput = false)
    {
        if (!OperatingSystem.IsWindows()) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_58D780F97C46"));
        await Slots.WaitAsync(token).ConfigureAwait(false);
        var task = Task.Run(() => Start(executable, arguments, directory, token, keepInput), CancellationToken.None);
        try { return await task.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
        catch
        {
            _ = task.ContinueWith(async completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion) await completed.Result.DisposeAsync().ConfigureAwait(false);
                else { _ = completed.Exception; Slots.Release(); }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
            throw;
        }
    }

    private static SevenZipNativeProcess Start(string executable, IReadOnlyList<string> arguments, string? directory, CancellationToken token, bool keepInput)
    {
        SafeFileHandle? job = null, outRead = null, outWrite = null, errRead = null, errWrite = null, inRead = null, inWrite = null;
        Process? process = null; ProcessInformation native = default;
        nint attributes = 0, handleList = 0; bool initializedAttributes = false;
        try
        {
            token.ThrowIfCancellationRequested();
            job = CreateJobObjectW(0, null);
            if (job.IsInvalid) throw new Win32Exception();
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x100u | 0x8u | 0x2000u, ActiveProcesses = 1 }, ProcessMemory = (nuint)(512L * 1024 * 1024) };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) throw new Win32Exception();
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
            if (!CreatePipe(out outRead, out outWrite, ref security, 0) || !CreatePipe(out errRead, out errWrite, ref security, 0) || !CreatePipe(out inRead, out inWrite, ref security, 0)) throw new Win32Exception();
            if (!SetHandleInformation(outRead, 1, 0) || !SetHandleInformation(errRead, 1, 0) || !SetHandleInformation(inWrite, 1, 0)) throw new Win32Exception();
            nuint size = 0; InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
            initializedAttributes = true;
            handleList = Marshal.AllocHGlobal(nint.Size * 3);
            Marshal.WriteIntPtr(handleList, 0, inRead.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, nint.Size, outWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, nint.Size * 2, errWrite.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, 0x20002, handleList, (nuint)(nint.Size * 3), 0, 0)) throw new Win32Exception();
            var startup = new StartupEx { Info = new Startup { Size = Marshal.SizeOf<StartupEx>(), Flags = 0x101, Input = inRead.DangerousGetHandle(), Output = outWrite.DangerousGetHandle(), Error = errWrite.DangerousGetHandle() }, Attributes = attributes };
            var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote)));
            if (!CreateProcessW(executable, command, 0, 0, true, 0x08000000u | 0x00000004u | 0x00080000u, 0, directory, ref startup, out native)) throw new Win32Exception();
            if (!AssignProcessToJobObject(job, native.Process)) throw new Win32Exception();
            token.ThrowIfCancellationRequested();
            process = Process.GetProcessById(checked((int)native.ProcessId));
            _ = process.SafeHandle;
            if (ResumeThread(native.Thread) == uint.MaxValue) throw new Win32Exception();
            outWrite.Dispose(); outWrite = null; errWrite.Dispose(); errWrite = null;
            if (!keepInput) { inWrite.Dispose(); inWrite = null; } inRead.Dispose(); inRead = null;
            // Disable managed write buffering: the only password byte[] is explicitly wiped by SendPasswordAsync.
            var result = new SevenZipNativeProcess(process, job, new FileStream(outRead, FileAccess.Read, 65536, false), new FileStream(errRead, FileAccess.Read, 8192, false), keepInput ? new FileStream(inWrite!, FileAccess.Write, 1, false) : null);
            process = null; job = null; outRead = null; errRead = null; inWrite = null;
            return result;
        }
        catch
        {
            if (native.Process != 0) TerminateProcess(native.Process, 1);
            throw;
        }
        finally
        {
            if (native.Thread != 0) CloseHandle(native.Thread);
            if (native.Process != 0) CloseHandle(native.Process);
            if (attributes != 0) { if (initializedAttributes) DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (handleList != 0) Marshal.FreeHGlobal(handleList);
            process?.Dispose(); job?.Dispose(); outRead?.Dispose(); outWrite?.Dispose(); errRead?.Dispose(); errWrite?.Dispose(); inRead?.Dispose(); inWrite?.Dispose();
        }
    }

    internal async Task<int> WaitAsync(CancellationToken token)
    {
        try { await _process.WaitForExitAsync(token).ConfigureAwait(false); return _process.ExitCode; }
        catch { _job.Dispose(); throw; }
    }
    internal async Task SendPasswordAsync(SevenZipPassword password, CancellationToken token)
    {
        var bytes = password.CopyUtf8Line();
        try
        {
            if (_input is null) throw new InvalidOperationException(global::Orbit.Core.Localization.L.Get("Text_A75C9540760F"));
            try { await _input.WriteAsync(bytes, token).ConfigureAwait(false); await _input.FlushAsync(token).ConfigureAwait(false); }
            // An archive without encrypted data may finish without consuming stdin.
            catch (IOException exception) when ((exception.HResult & 0xffff) is 109 or 232) { }
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); _input?.Dispose(); _input = null; }
    }
    internal static async Task<byte[]> ReadBoundedAsync(Stream input, int limit, CancellationToken token)
    {
        using var result = new MemoryStream(); var buffer = new byte[16384];
        while (true)
        {
            var count = await input.ReadAsync(buffer, token).ConfigureAwait(false); if (count == 0) return result.ToArray();
            if (result.Length + count > limit) throw new DocumentProcessingException(global::Orbit.Core.Localization.L.Get("Text_6DA8612DEA2D"));
            result.Write(buffer, 0, count);
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _input?.Dispose(); _input = null;
        _job.Dispose();
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        Output.Dispose(); Error.Dispose(); _process.Dispose(); Slots.Release();
    }
    private static string Quote(string value)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); slashes = 0; result.Append(c);
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public nint Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Startup { public int Size; public nint Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags; public ushort Show, ReservedSize; public nint ReservedData, Input, Output, Error; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupEx { public Startup Info; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long UserTime, JobTime; public uint Flags; public nuint MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(nint attributes, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(nint attributes, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string? directory, ref StartupEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
