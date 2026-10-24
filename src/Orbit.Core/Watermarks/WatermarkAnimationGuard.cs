using System.Buffers.Binary;
namespace Orbit.Core.Watermarks;
internal static class WatermarkAnimationGuard
{
    // Some explicitly selected still-image coders can expose only the primary frame.
    internal static void Check(FileStream input)
    {
        var header=new byte[12];input.Position=0;int initial=input.Read(header);input.Position=0;
        if(initial>=8 && header.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))
        {
            long offset=8;var chunk=new byte[8];int count=0;
            while(offset+12<=input.Length)
            {
                if(++count>100000)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_A51F14741B85"));
                input.Position=offset;input.ReadExactly(chunk);long length=BinaryPrimitives.ReadUInt32BigEndian(chunk);
                if(length>input.Length-offset-12)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_6C544C6AC330"));
                if(chunk.AsSpan(4).SequenceEqual("acTL"u8)||chunk.AsSpan(4).SequenceEqual("fcTL"u8)||chunk.AsSpan(4).SequenceEqual("fdAT"u8))throw Animated();
                offset+=12+length;if(chunk.AsSpan(4).SequenceEqual("IEND"u8))break;
            }
        }
        else if(initial==12 && header.AsSpan(0,4).SequenceEqual("RIFF"u8)&&header.AsSpan(8,4).SequenceEqual("WEBP"u8))
        {
            long offset=12;var chunk=new byte[8];int count=0;
            while(offset+8<=input.Length)
            {
                if(++count>100000)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_3DA815059368"));
                input.Position=offset;input.ReadExactly(chunk);long length=BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
                if(length>input.Length-offset-8)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_ED02F7BEF3D2"));
                if(chunk.AsSpan(0,4).SequenceEqual("ANIM"u8)||chunk.AsSpan(0,4).SequenceEqual("ANMF"u8))throw Animated();
                if(chunk.AsSpan(0,4).SequenceEqual("VP8X"u8)&&length>0&&(input.ReadByte()&2)!=0)throw Animated();
                offset+=8+length+(length&1);
            }
        }
        else if(initial==12 && header.AsSpan(4,4).SequenceEqual("ftyp"u8))
        {
            long size=BinaryPrimitives.ReadUInt32BigEndian(header);
            if(size<16||size>4096||size>input.Length||(size&3)!=0)throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_878B82D7DC3A"));
            input.Position=8;var brands=new byte[(int)size-8];input.ReadExactly(brands);for(int i=0;i+4<=brands.Length;i+=4)if(brands.AsSpan(i,4).SequenceEqual("avis"u8))throw Animated();
        }
        input.Position=0;
    }
    private static ImageProcessingException Animated()=>new(global::Orbit.Core.Localization.L.Get("Text_9E1EB1A3CACF"));
}
