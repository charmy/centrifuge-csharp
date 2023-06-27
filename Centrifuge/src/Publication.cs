using System.Collections.Generic;

namespace Centrifuge
{
    public class Publication
    {
        public byte[] Data { get; set; }
        public long Offset { get; set; }
    }
}