// Vendored from APNG.NET (https://github.com/xupefei/APNG.NET) - MIT License, (c) 2013 Amemiya.
// See LICENSE.txt. Unmodified except for this header and the #nullable directive.
#nullable disable
using System.IO;

namespace LibAPNG
{
    public class OtherChunk : Chunk
    {
        public OtherChunk(byte[] bytes)
            : base(bytes)
        {
        }

        public OtherChunk(MemoryStream ms)
            : base(ms)
        {
        }

        public OtherChunk(Chunk chunk)
            : base(chunk)
        {
        }

        protected override void ParseData(MemoryStream ms)
        {
        }
    }
}