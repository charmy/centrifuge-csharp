namespace Centrifuge
{
    class ServerSubscription
    {
        public ulong Offset { get; set; }
        public string Epoch { get; set; }
        public bool Recoverable { get; set; }

        public ServerSubscription(bool recoverable, ulong offset, string epoch)
        {
            Recoverable = recoverable;
            Offset = offset;
            Epoch = epoch;
        }
    }
}