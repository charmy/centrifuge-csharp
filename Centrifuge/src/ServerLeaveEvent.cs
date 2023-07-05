namespace Centrifuge
{
    public class ServerLeaveEvent
    {
        public string Channel { get; }
        public ClientInfo Info { get; }

        public ServerLeaveEvent(string channel, ClientInfo info)
        {
            Channel = channel;
            Info = info;
        }
    }
}