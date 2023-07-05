using System;

namespace Centrifuge
{
    public class ErrorEvent
    {
        public Exception Error { get; }

        public ErrorEvent(Exception e)
        {
            Error = e;
        }
    }
}