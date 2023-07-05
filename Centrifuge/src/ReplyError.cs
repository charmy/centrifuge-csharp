using System;

namespace Centrifuge
{
    public class ReplyError : Exception
    {
        public int Code { get; set; }
        public string Message { get; set; }
        public bool IsTemporary { get; set; }

        public ReplyError(int code, string message, bool isTemporary) : base(message)
        {
            Code = code;
            Message = message;
            IsTemporary = isTemporary;
        }
    }
}