using System.Buffers.Binary;
namespace Orbit.Core.Watermarks;
internal static class WatermarkFontCoverage
{
    internal static bool Supports(string fontPath, string text)
    {
        var data=File.ReadAllBytes(fontPath); if(data.Length is <12 or >4194304) return false;
        int U16(int p)=>p>=0 && p+2<=data.Length?BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p)):throw new InvalidDataException();
        int U32(int p)=>p>=0 && p+4<=data.Length?checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p))):throw new InvalidDataException();
        var count=U16(4);int cmap=-1;
        for(int i=0;i<count;i++){int p=12+i*16;if(p+16>data.Length)return false;if(data.AsSpan(p,4).SequenceEqual("cmap"u8)){cmap=U32(p+8);break;}}
        if(cmap<0)return false;var subtables=new List<int>();
        for(int i=0;i<U16(cmap+2);i++){int p=cmap+4+i*8;var platform=U16(p);if(platform is 0 or 3){int sub=checked(cmap+U32(p+4));if(U16(sub) is 4 or 12)subtables.Add(sub);}}
        bool Has(int code,int sub)
        {
            if(U16(sub)==12){int groups=U32(sub+12);if(groups>200000)throw new InvalidDataException();int lo=0,hi=groups-1;while(lo<=hi){int m=(lo+hi)/2,p=sub+16+m*12,start=U32(p),end=U32(p+4);if(code<start)hi=m-1;else if(code>end)lo=m+1;else return U32(p+8)+code-start!=0;}return false;}
            if(code>65535)return false;int n=U16(sub+6)/2,endAt=sub+14,startAt=endAt+n*2+2,deltaAt=startAt+n*2,rangeAt=deltaAt+n*2;
            for(int i=0;i<n;i++){if(code>U16(endAt+i*2))continue;int start=U16(startAt+i*2);if(code<start)return false;int range=U16(rangeAt+i*2),delta=U16(deltaAt+i*2);if(range==0)return((code+delta)&65535)!=0;int glyph=U16(rangeAt+i*2+range+(code-start)*2);return glyph!=0 && ((glyph+delta)&65535)!=0;}return false;
        }
        foreach(var rune in text.EnumerateRunes())if(!subtables.Any(sub=>Has(rune.Value,sub)))return false;return true;
    }
}
