namespace Centrifuge
{
    public abstract class EventListener
    {
        public virtual void OnConnecting(Client client, ConnectingEvent e)
        {
        }

        public virtual void OnConnected(Client client, ConnectedEvent e)
        {
        }

        public virtual void OnDisconnected(Client client, DisconnectedEvent e)
        {
        }

        public virtual void OnError(Client client, ErrorEvent e)
        {
        }

        public virtual void OnMessage(Client client, MessageEvent e)
        {
        }

        public virtual void OnSubscribed(Client client, ServerSubscribedEvent e)
        {
        }

        public virtual void OnSubscribing(Client client, ServerSubscribingEvent e)
        {
        }

        public virtual void OnUnsubscribed(Client client, ServerUnsubscribedEvent e)
        {
        }

        public virtual void OnPublication(Client client, ServerPublicationEvent e)
        {
        }

        public virtual void OnJoin(Client client, ServerJoinEvent e)
        {
        }

        public virtual void OnLeave(Client client, ServerLeaveEvent e)
        {
        }
    }
}