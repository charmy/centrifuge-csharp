using System.Collections.Generic;

namespace Centrifuge
{
    public class ServerPublicationEvent
    {
        public byte[] Data { get; set; }
        public ClientInfo Info { get; set; }
        public ulong Offset { get; set; }
        public string Channel { get; set; }
        public Dictionary<string, string> Tags { get; set; }
    }
}