namespace Centrifuge
{
    public abstract class SubscriptionEventListener
    {
        public virtual void OnPublication(Subscription sub, PublicationEvent e)
        {
        }

        public virtual void OnJoin(Subscription sub, JoinEvent e)
        {
        }

        public virtual void OnLeave(Subscription sub, LeaveEvent e)
        {
        }

        public virtual void OnSubscribed(Subscription sub, SubscribedEvent e)
        {
        }

        public virtual void OnUnsubscribed(Subscription sub, UnsubscribedEvent e)
        {
        }

        public virtual void OnSubscribing(Subscription sub, SubscribingEvent e)
        {
        }

        public virtual void OnError(Subscription sub, SubscriptionErrorEvent e)
        {
        }
    }
}