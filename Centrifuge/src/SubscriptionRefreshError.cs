using System;

namespace Centrifuge
{
    public class SubscriptionRefreshError : Exception
    {
        public Exception Error { get; }

        public SubscriptionRefreshError(Exception error) : base(error.Message)
        {
            Error = error;
        }
    }
}