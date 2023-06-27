using System;

namespace Centrifuge
{
    public class UnclassifiedError : Exception
    {
        public Exception Error { get; }

        public UnclassifiedError(Exception error) : base(error.Message)
        {
            Error = error;
        }
    }
}