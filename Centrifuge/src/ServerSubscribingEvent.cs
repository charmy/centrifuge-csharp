namespace Centrifuge
{
    public class ServerSubscribingEvent
    {
        private string Channel { get; }

        public ServerSubscribingEvent(string channel)
        {
            Channel = channel;
        }
    }
}