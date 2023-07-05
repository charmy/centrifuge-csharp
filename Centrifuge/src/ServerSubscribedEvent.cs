namespace Centrifuge
{
    public class ServerSubscribedEvent
    {
        public string Channel { get; }
        public bool WasRecoveringField { get; }
        public bool Recovered { get; }
        public byte[] Data { get; }
        public bool Positioned { get; }
        public bool Recoverable { get; }
        public StreamPosition StreamPosition { get; }

        public ServerSubscribedEvent(string channel, bool wasRecovering, bool recovered, bool positioned, bool recoverable, StreamPosition streamPosition, byte[] data)
        {
            Channel = channel;
            WasRecoveringField = wasRecovering;
            Recovered = recovered;
            Positioned = positioned;
            Recoverable = recoverable;
            StreamPosition = streamPosition;
            Data = data;
        }
    }
}