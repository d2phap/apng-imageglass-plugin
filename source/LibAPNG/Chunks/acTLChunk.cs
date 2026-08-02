// Vendored from APNG.NET (https://github.com/xupefei/APNG.NET) - MIT License, (c) 2013 Amemiya.
// See LICENSE.txt. Unmodified except for this header and the #nullable directive.
#nullable disable
using System.IO;

namespace LibAPNG
{
    public class acTLChunk : Chunk
    {
        public acTLChunk(byte[] bytes)
            : base(bytes)
        {
        }

        public acTLChunk(MemoryStream ms)
            : base(ms)
        {
        }

        public acTLChunk(Chunk chunk)
            : base(chunk)
        {
        }

        public uint NumFrames { get; private set; }

        public uint NumPlays { get; private set; }

        protected override void ParseData(MemoryStream ms)
        {
            NumFrames = Helper.ConvertEndian(ms.ReadUInt32());
            NumPlays = Helper.ConvertEndian(ms.ReadUInt32());
        }
    }
}