using System;

namespace Centrifuge
{
    public class SubscriptionErrorEvent : Exception
    {
        public Exception Error { get; }

        public SubscriptionErrorEvent(Exception t) : base(t.Message, t)
        {
            Error = t;
        }
    }
}