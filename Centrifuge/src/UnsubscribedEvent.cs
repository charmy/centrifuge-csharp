namespace Centrifuge
{
    public class UnsubscribedEvent
    {
        public int Code { get; }
        public string Reason { get; }

        public UnsubscribedEvent(int code, string reason)
        {
            Code = code;
            Reason = reason;
        }
    }
}