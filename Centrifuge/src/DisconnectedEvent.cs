namespace Centrifuge
{
    public class DisconnectedEvent
    {
        public int Code { get; }
        public string Reason { get; }

        public DisconnectedEvent(int code, string reason)
        {
            Code = code;
            Reason = reason;
        }
    }
}