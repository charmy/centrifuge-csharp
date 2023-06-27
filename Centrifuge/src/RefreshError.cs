using System;

namespace Centrifuge
{
    public class RefreshError : Exception
    {
        public Exception Error { get; }

        public RefreshError(Exception error) : base(error.Message, error)
        {
            Error = error;
        }
    }
}