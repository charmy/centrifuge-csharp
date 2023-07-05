namespace Centrifuge
{
    public class SubscribedEvent
    {
        public bool WasRecoveringField { get; }
        public bool Recovered { get; }
        public bool Positioned { get; }
        public bool Recoverable { get; }
        public StreamPosition StreamPosition { get; }
        public byte[] Data { get; }

        public SubscribedEvent(bool wasRecovering, bool recovered, bool positioned, bool recoverable, StreamPosition streamPosition, byte[] data)
        {
            WasRecoveringField = wasRecovering;
            Recovered = recovered;
            Positioned = positioned;
            Recoverable = recoverable;
            StreamPosition = streamPosition;
            Data = data;
        }
    }
}