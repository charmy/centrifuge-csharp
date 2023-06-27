namespace Centrifuge
{
    public class StreamPosition
    {
        public ulong Offset { get; set; }
        public string Epoch { get; set; }

        public StreamPosition(ulong offset, string epoch)
        {
            Offset = offset;
            Epoch = epoch;
        }

        public StreamPosition()
        {
        }

        public Protocol.StreamPosition ToProto()
        {
            return new Protocol.StreamPosition
            {
                Epoch = Epoch,
                Offset = Offset
            };
        }
    }
}