namespace Centrifuge
{
    public class ServerJoinEvent
    {
        public string Channel { get; set; }
        public ClientInfo Info { get; set; }
        
        public ServerJoinEvent(string channel, ClientInfo info)
        {
            Channel = channel;
            Info = info;
        }
    }
}