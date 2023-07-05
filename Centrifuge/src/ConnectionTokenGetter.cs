namespace Centrifuge
{
    public abstract class ConnectionTokenGetter
    {
        public virtual void GetConnectionToken(ConnectionTokenEvent e, TokenCallback cb)
        {
            //todo
            cb(null, "");
        }
    }
}