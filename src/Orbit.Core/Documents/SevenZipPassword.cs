using System.Security.Cryptography;
using System.Text;

namespace Orbit.Core.Documents;

/// <summary>A transient, independently owned secret. Dispose after the queue item/panel completes.</summary>
public sealed class SevenZipPassword : IDisposable
{
    private readonly object _gate = new();
    private char[]? _characters;
    public SevenZipPassword(ReadOnlySpan<char> password)
    {
        if (password.Length is < 1 or > 256) throw new ArgumentException(global::Orbit.Core.Localization.L.Get("Text_714F2DE5CABB"));
        for (int index = 0; index < password.Length; index++)
        {
            var value = password[index];
            if (char.IsControl(value) || value is '\u2028' or '\u2029' || char.IsLowSurrogate(value) || char.IsHighSurrogate(value) && (index + 1 >= password.Length || !char.IsLowSurrogate(password[++index])))
                throw new ArgumentException(global::Orbit.Core.Localization.L.Get("Text_2CBD7DE02227"));
        }
        _characters = password.ToArray();
    }
    public SevenZipPassword Clone()
    { lock (_gate) { ObjectDisposedException.ThrowIf(_characters is null, this); return new SevenZipPassword(_characters); } }
    internal byte[] CopyUtf8Line()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_characters is null, this);
            var line = new byte[Encoding.UTF8.GetByteCount(_characters) + 1];
            Encoding.UTF8.GetBytes(_characters.AsSpan(), line.AsSpan()); line[^1] = (byte)'\n'; return line;
        }
    }
    public void Dispose()
    { lock (_gate) { if (_characters is not null) { CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_characters.AsSpan())); _characters = null; } } }
    public override string ToString() => global::Orbit.Core.Localization.L.Get("Text_71CD1E020F9D");
}

public enum SevenZipProtection { None, Data, Headers }
public sealed record SevenZipProtectionInfo(string SourcePath, SevenZipProtection Protection);

public sealed class SevenZipPasswordRequiredException : Exception
{ public SevenZipPasswordRequiredException() : base(global::Orbit.Core.Localization.L.Get("Text_16B7669352FE")) { } }
public sealed class SevenZipPasswordRejectedException : Exception
{ public SevenZipPasswordRejectedException() : base(global::Orbit.Core.Localization.L.Get("Text_4CBBA7DAA6F9")) { } }
