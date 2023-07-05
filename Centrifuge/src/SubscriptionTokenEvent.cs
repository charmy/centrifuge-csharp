using System;

namespace Centrifuge
{
    public class SubscriptionTokenEvent
    {
        public string Channel { get; }

        public SubscriptionTokenEvent(string channel)
        {
            Channel = channel;
        }
    }
}