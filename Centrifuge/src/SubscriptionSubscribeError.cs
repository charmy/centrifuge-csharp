using System;

namespace Centrifuge
{
    public class SubscriptionSubscribeError : Exception
    {
        public Exception Error { get; }

        public SubscriptionSubscribeError(Exception error) : base(error.Message)
        {
            Error = error;
        }
    }
}