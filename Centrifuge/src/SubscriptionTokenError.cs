using System;

namespace Centrifuge
{
    public class SubscriptionTokenError : Exception
    {
        public Exception Error { get; }

        public SubscriptionTokenError(Exception error) : base(error.Message)
        {
            Error = error;
        }
    }
}