using System.Collections.Generic;
using System.Net;

namespace Centrifuge
{
    public class Options
    {
        public string Token { get; set; } = "";
        public ConnectionTokenGetter TokenGetter { get; set; }
        public string Name { get; set; } = "java"; // todo 
        public string Version { get; set; } = "";
        public byte[] Data { get; set; }
        public Dictionary<string, string> Headers { get; set; }
        public int Timeout { get; set; } = 5000;
        public int MinReconnectDelay { get; set; } = 500;
        public int MaxReconnectDelay { get; set; } = 20000;
        public int MaxServerPingDelay { get; set; } = 10000;
    }
}