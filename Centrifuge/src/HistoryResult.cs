using System.Collections.Generic;
using Protocol;

namespace Centrifuge
{
    public class HistoryResult
    {
        public List<Publication> Publications { get; set; }
        public long Offset { get; set; }
        public string Epoch { get; set; }
    }
}