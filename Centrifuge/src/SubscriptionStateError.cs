using System;

namespace Centrifuge
{
    public class SubscriptionStateError : Exception
    {
        public SubscriptionState State { get; }

        public SubscriptionStateError(SubscriptionState state) : base(state.ToString())
        {
            State = state;
        }
    }
}