// Vendored from APNG.NET (https://github.com/xupefei/APNG.NET) - MIT License, (c) 2013 Amemiya.
// See LICENSE.txt. Unmodified except for this header and the #nullable directive.
#nullable disable
using System.IO;

namespace LibAPNG
{
    public class IENDChunk : Chunk
    {
        public IENDChunk(byte[] bytes)
            : base(bytes)
        {
        }

        public IENDChunk(MemoryStream ms)
            : base(ms)
        {
        }

        public IENDChunk(Chunk chunk)
            : base(chunk)
        {
        }
    }
}