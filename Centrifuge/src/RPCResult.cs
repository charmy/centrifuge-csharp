namespace Centrifuge
{
    public class RPCResult
    {
        public ReplyError Error { get; set; }
        public byte[] Data { get; set; }
    }
}