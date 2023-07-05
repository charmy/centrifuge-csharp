using System;

namespace Centrifuge
{
    public class TokenError : Exception
    {
        public Exception Error { get; }

        public TokenError(Exception error) : base(error.Message)
        {
            Error = error;
        }
    }
}