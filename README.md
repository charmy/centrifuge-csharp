# Centrifuge C# Client

![Nuget](https://img.shields.io/nuget/v/Centrifuge)

This SDK provides a client to connect to Centrifugo

# ⚠️ Working in progress
- SDK is not completed yet.
- This library is translated from [centrifuge java sdk](https://github.com/centrifugal/centrifuge-java). It is not stable.
# Installation
```
dotnet add package Centrifuge --version 1.0.0
```

# Platforms
* .NET Framework 4.8
* Unity 2022.3+ (Api .NET Framework)


### TODO

- [ ] Code Refactor
- [ ] History
- [ ] Presence


### Example

```cs
using System;
using System.Threading;
using Centrifuge;

namespace Example
{
    internal class Program
    {
        private static string TOKEN = "";

        public class TokenGetter : ConnectionTokenGetter
        {
            public override void GetConnectionToken(ConnectionTokenEvent e, TokenCallback cb)
            {
                cb(null, TOKEN);
            }
        }

        public class Listener : EventListener
        {
            public override void OnConnecting(Client client, ConnectingEvent e)
            {
                Console.WriteLine("onConnecting");
            }

            public override void OnConnected(Client client, ConnectedEvent e)
            {
                Console.WriteLine("onConnected");
            }

            public override void OnDisconnected(Client client, DisconnectedEvent e)
            {
                Console.WriteLine("onDisconnected");
            }

            public override void OnError(Client client, ErrorEvent e)
            {
                Console.WriteLine("onError1" + e.Error);
            }

            public override void OnMessage(Client client, MessageEvent e)
            {
                Console.WriteLine("onMessage");
            }

            public override void OnSubscribed(Client client, ServerSubscribedEvent e)
            {
                Console.WriteLine("onSubscribed");
            }

            public override void OnSubscribing(Client client, ServerSubscribingEvent e)
            {
                Console.WriteLine("onSubscribing");
            }

            public override void OnUnsubscribed(Client client, ServerUnsubscribedEvent e)
            {
                Console.WriteLine("onUnsubscribed");
            }

            public override void OnPublication(Client client, ServerPublicationEvent e)
            {
                Console.WriteLine("onPublication");
            }

            public override void OnJoin(Client client, ServerJoinEvent e)
            {
                Console.WriteLine("onJoin");
            }

            public override void OnLeave(Client client, ServerLeaveEvent e)
            {
                Console.WriteLine("onLeave");
            }
        }

        public class SubListener : SubscriptionEventListener
        {
            public override void OnPublication(Subscription sub, PublicationEvent e)
            {
                var d = System.Text.Encoding.UTF8.GetString(e.Data);
                Console.WriteLine("onPublication" + d);
            }

            public override void OnJoin(Subscription sub, JoinEvent e)
            {
                Console.WriteLine("onJoin");
            }

            public override void OnLeave(Subscription sub, LeaveEvent e)
            {
                Console.WriteLine("onLeave");
            }

            public override void OnSubscribed(Subscription sub, SubscribedEvent e)
            {
                Console.WriteLine("onSubscribed");
            }

            public override void OnUnsubscribed(Subscription sub, UnsubscribedEvent e)
            {
                Console.WriteLine("onUnsubscribed");
            }

            public override void OnSubscribing(Subscription sub, SubscribingEvent e)
            {
                Console.WriteLine("onSubscribing");
            }

            public override void OnError(Subscription sub, SubscriptionErrorEvent e)
            {
                Console.WriteLine("onError" + e.Error);
            }
        }

        public static void Main(string[] args)
        {
            Options opts = new Options()
            {
                Token = TOKEN,
                TokenGetter = new TokenGetter(),
            };

            Client client = new Client("ws://localhost:8088/connection/websocket", opts, new Listener());
            client.Connect();

            var sub = client.NewSubscription("message#0f63396b-3b29-4072-849b-a886ac08a5e2", new SubscriptionOptions(), new SubListener());
            sub.Subscribe();

            Thread.Sleep(200000);
        }
    }
}
```
