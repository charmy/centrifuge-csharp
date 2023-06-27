using System;

namespace Centrifuge
{
    public class ConfigurationError : Exception
    {
        public Exception Error { get; }

        public ConfigurationError(Exception error) : base(error.Message, error)
        {
            Error = error;
        }
    }
}