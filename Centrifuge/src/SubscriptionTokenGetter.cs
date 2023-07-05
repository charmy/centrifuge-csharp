namespace Centrifuge
{
    public abstract class SubscriptionTokenGetter
    {
        public virtual void GetSubscriptionToken(SubscriptionTokenEvent e, TokenCallback cb)
        {
            //todo
            cb(null, "");
        }
    }
}