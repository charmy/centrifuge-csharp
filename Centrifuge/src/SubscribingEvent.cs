namespace Centrifuge
{
    public class SubscribingEvent
    {
        public int Code { get; }
        public string Reason { get; }

        public SubscribingEvent(int code, string reason)
        {
            Code = code;
            Reason = reason;
        }
    }
}