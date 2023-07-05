namespace Centrifuge
{
    public class ClientInfo
    {
        public string User { get; set; }
        public string Client { get; set; }
        public byte[] ConnInfo { get; set; }
        public byte[] ChanInfo { get; set; }

        public static ClientInfo FromProtocolClientInfo(Protocol.ClientInfo protoClientInfo)
        {
            if (protoClientInfo != null)
            {
                return new ClientInfo
                {
                    User = protoClientInfo.User,
                    Client = protoClientInfo.Client,
                    ConnInfo = protoClientInfo.ConnInfo.ToByteArray(),
                    ChanInfo = protoClientInfo.ChanInfo.ToByteArray()
                };
            }
            else
            {
                return null;
            }
        }
    }
}