using System.Collections.Generic;

namespace Centrifuge
{
    public class PresenceResult
    {
        public Dictionary<string, ClientInfo> Clients { get; }

        public PresenceResult(Dictionary<string, ClientInfo> clients)
        {
            Clients = clients;
        }
    }
}