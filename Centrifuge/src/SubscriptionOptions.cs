namespace Centrifuge
{
    public class SubscriptionOptions
    {
        public string Token { get; set; } = "";
        public byte[] Data { get; set; }
        public int MinResubscribeDelay { get; set; } = 500;
        public int MaxResubscribeDelay { get; set; } = 20000;
        public bool IsPositioned { get; set; } = false;
        public bool IsRecoverable { get; set; } = false;
        public bool JoinLeave { get; set; } = false;
        public SubscriptionTokenGetter TokenGetter { get; set; }
    }
}