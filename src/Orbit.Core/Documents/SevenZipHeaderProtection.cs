namespace Orbit.Core.Documents;

// Reads coder descriptors only; native 7zr remains the sole decompressor.
internal static class SevenZipHeaderProtection
{
    internal static bool HasEncryptedHeader(byte[] header)
    {
        if (header.Length == 0 || header[0] == 1) return false;
        var reader = new Reader(header); if (reader.Byte() != 0x17) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_B309093E9CC4"));
        var id = reader.Byte();
        if (id == 6)
        {
            _ = reader.Number(); var streams = reader.Count(128); id = reader.Byte();
            if (id == 9) { for (int index = 0; index < streams; index++) _ = reader.Number(); id = reader.Byte(); }
            if (id == 10) { reader.Digests(streams); id = reader.Byte(); }
            if (id != 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_A688F09A030E")); id = reader.Byte();
        }
        if (id != 7 || reader.Byte() != 11) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_05444385E371"));
        var folders = reader.Count(128); if (reader.Byte() != 0) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_4134D6747640"));
        bool encrypted = false;
        for (int folder = 0; folder < folders; folder++)
        {
            var coders = reader.Count(64); int inputs = 0, outputs = 0;
            for (int coder = 0; coder < coders; coder++)
            {
                var flags = reader.Byte(); int size = flags & 15;
                if ((flags & 0xc0) != 0 || size is < 1 or > 8) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_927213450F39"));
                var codec = reader.Bytes(size); if (codec.SequenceEqual(new byte[] { 6, 0xf1, 7, 1 })) encrypted = true;
                inputs = checked(inputs + ((flags & 0x10) != 0 ? reader.Count(128) : 1));
                outputs = checked(outputs + ((flags & 0x10) != 0 ? reader.Count(128) : 1));
                if (inputs > 128 || outputs > 128) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_E606A6976E47"));
                if ((flags & 0x20) != 0) reader.Skip(reader.Count(65536));
            }
            var bindings = outputs - 1; if (bindings < 0 || bindings >= inputs) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_BE42D93DB829"));
            for (int binding = 0; binding < bindings; binding++) { if (reader.Number() >= (ulong)inputs || reader.Number() >= (ulong)outputs) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_F59670553E6F")); }
            var packed = inputs - bindings;
            if (packed > 1) for (int stream = 0; stream < packed; stream++) if (reader.Number() >= (ulong)inputs) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_9979CB0081B9"));
        }
        return encrypted;
    }
    private sealed class Reader(byte[] bytes)
    {
        private int _position;
        internal byte Byte() => _position < bytes.Length ? bytes[_position++] : throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_E721A32A896B"));
        internal ulong Number()
        {
            byte first = Byte(); int mask = 0x80; ulong value = 0;
            for (int index = 0; index < 8; index++, mask >>= 1) { if ((first & mask) == 0) return value | ((ulong)(first & (mask - 1)) << (index * 8)); value |= (ulong)Byte() << (index * 8); }
            return value;
        }
        internal int Count(int maximum) { var value = Number(); if (value > (ulong)maximum) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_5A7A70E7F51A")); return (int)value; }
        internal ReadOnlySpan<byte> Bytes(int count) { if (count < 0 || count > bytes.Length - _position) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_E721A32A896B")); var result = bytes.AsSpan(_position, count); _position += count; return result; }
        internal void Skip(int count) => _ = Bytes(count);
        internal void Digests(int count)
        {
            var all = Byte(); if (all > 1) throw new InvalidDataException(global::Orbit.Core.Localization.L.Get("Text_B87364B43FA3")); int defined = count;
            if (all == 0) { defined = 0; for (int index = 0; index < count; index += 8) { var bits = Byte(); for (int bit = 0; bit < Math.Min(8, count - index); bit++) if ((bits & (0x80 >> bit)) != 0) defined++; } }
            Skip(defined * 4);
        }
    }
}
