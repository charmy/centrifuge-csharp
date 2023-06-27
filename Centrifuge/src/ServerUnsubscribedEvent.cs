namespace Centrifuge
{
    public class ServerUnsubscribedEvent
    {
        public string Channel { get; }

        public ServerUnsubscribedEvent(string channel)
        {
            Channel = channel;
        }
    }
}