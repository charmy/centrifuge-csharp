namespace Centrifuge
{
    public class ConnectingEvent
    {
        public int Code { get; }
        public string Reason { get; }

        public ConnectingEvent(int code, string reason)
        {
            Code = code;
            Reason = reason;
        }
    }
}