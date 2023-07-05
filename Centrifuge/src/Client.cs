using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Websocket.Client;

namespace Centrifuge
{
    public class Client
    {
        private WebsocketClient _ws;
        private readonly string _endpoint;
        private readonly Options _opts;
        private string _token;
        private readonly ByteString _data;
        private readonly EventListener _listener;
        private readonly ConcurrentDictionary<uint, TaskCompletionSource<Protocol.Reply>> _futures = new ConcurrentDictionary<uint, TaskCompletionSource<Protocol.Reply>>();
        private readonly ConcurrentDictionary<uint, Protocol.Command> _connectCommands = new ConcurrentDictionary<uint, Protocol.Command>();
        private readonly ConcurrentDictionary<uint, Protocol.Command> _connectAsyncCommands = new ConcurrentDictionary<uint, Protocol.Command>();

        private volatile ClientState _state = ClientState.DISCONNECTED;
        private readonly ConcurrentDictionary<string, Subscription> _subs = new ConcurrentDictionary<string, Subscription>();
        private readonly ConcurrentDictionary<string, ServerSubscription> _serverSubs = new ConcurrentDictionary<string, ServerSubscription>();
        private readonly Backoff _backoff;

        private static readonly CancellationTokenSource executorCts = new CancellationTokenSource();
        private readonly TaskFactory _executor = new TaskFactory(executorCts.Token);

        private TimeSpan _pingInterval;
        private bool _sendPong;

        private Task _pingTask;
        private CancellationTokenSource _pingTaskCts = new CancellationTokenSource();
        private Task _refreshTask;
        private CancellationTokenSource _refreshTaskCts = new CancellationTokenSource();
        private Task _reconnectTask;
        private CancellationTokenSource _reconnectTaskCts = new CancellationTokenSource();

        private int _reconnectAttempts;
        private bool _refreshRequired;

        private const int NORMAL_CLOSURE_STATUS = 1000;
        private const int MESSAGE_SIZE_LIMIT_EXCEEDED_STATUS = 1009;

        public const int DISCONNECTED_DISCONNECT_CALLED = 0;
        public const int DISCONNECTED_UNAUTHORIZED = 1;
        public const int DISCONNECTED_BAD_PROTOCOL = 2;
        public const int DISCONNECTED_MESSAGE_SIZE_LIMIT = 3;

        public const int CONNECTING_CONNECT_CALLED = 0;
        public const int CONNECTING_TRANSPORT_CLOSED = 1;
        public const int CONNECTING_NO_PING = 2;
        public const int CONNECTING_SUBSCRIBE_TIMEOUT = 3;
        public const int CONNECTING_UNSUBSCRIBE_ERROR = 4;

        public const int SUBSCRIBING_SUBSCRIBE_CALLED = 0;
        public const int SUBSCRIBING_TRANSPORT_CLOSED = 1;

        public const int UNSUBSCRIBED_UNSUBSCRIBE_CALLED = 0;
        public const int UNSUBSCRIBED_UNAUTHORIZED = 1;
        public const int UNSUBSCRIBED_CLIENT_CLOSED = 2;

        private uint _id;

        public Client(string endpoint, Options opts, EventListener listener)
        {
            _endpoint = endpoint;
            _opts = opts;
            _listener = listener;
            _backoff = new Backoff();
            _token = opts.Token;
            if (opts.Data != null)
            {
                _data = ByteString.CopyFrom(opts.Data);
            }
        }

        public Options GetOpts()
        {
            return _opts;
        }

        public TaskFactory GetExecutor()
        {
            return _executor;
        }

        private uint GetNextId()
        {
            return ++_id;
        }

        public void Connect()
        {
            _executor.StartNew(() =>
            {
                if (_state == ClientState.CONNECTED || _state == ClientState.CONNECTING)
                {
                    return;
                }

                _reconnectAttempts = 0;
                _state = ClientState.CONNECTING;
                ConnectingEvent e = new ConnectingEvent(CONNECTING_CONNECT_CALLED, "connect called");
                _listener.OnConnecting(this, e);
                _connect();
            });
        }

        public void Disconnect()
        {
            _executor.StartNew(() => ProcessDisconnect(DISCONNECTED_DISCONNECT_CALLED, "disconnect called", false));
        }

        public bool Close(int awaitMilliseconds)
        {
            Disconnect();
            executorCts.CancelAfter(awaitMilliseconds);

            return false;
        }

        private void ProcessDisconnect(int code, string reason, bool shouldReconnect)
        {
            if (_state == ClientState.DISCONNECTED || _state == ClientState.CLOSED)
            {
                return;
            }

            ClientState previousState = _state;

            if (_pingTask != null)
            {
                _pingTaskCts.Cancel();
                _pingTask = null;
            }

            if (_refreshTask != null)
            {
                _refreshTaskCts.Cancel();
                _refreshTask = null;
            }

            if (_reconnectTask != null)
            {
                _reconnectTaskCts.Cancel();
                _reconnectTask = null;
            }

            bool needEvent;
            if (shouldReconnect)
            {
                needEvent = previousState != ClientState.CONNECTING;
                _state = ClientState.CONNECTING;
            }
            else
            {
                needEvent = previousState != ClientState.DISCONNECTED;
                _state = ClientState.DISCONNECTED;
            }

            lock (_subs)
            {
                foreach (KeyValuePair<string, Subscription> entry in _subs)
                {
                    Subscription sub = entry.Value;
                    if (sub.GetState() == SubscriptionState.UNSUBSCRIBED)
                    {
                        continue;
                    }

                    sub.MoveToSubscribing(SUBSCRIBING_TRANSPORT_CLOSED, "transport closed");
                }
            }

            foreach (KeyValuePair<uint, TaskCompletionSource<Protocol.Reply>> entry in _futures)
            {
                TaskCompletionSource<Protocol.Reply> f = entry.Value;
                f.SetException(new IOException());
            }

            if (previousState == ClientState.CONNECTED)
            {
                foreach (KeyValuePair<string, ServerSubscription> entry in _serverSubs)
                {
                    _listener.OnSubscribing(this, new ServerSubscribingEvent(entry.Key));
                }
            }

            if (needEvent)
            {
                if (shouldReconnect)
                {
                    ConnectingEvent e = new ConnectingEvent(code, reason);
                    _listener.OnConnecting(this, e);
                }
                else
                {
                    DisconnectedEvent e = new DisconnectedEvent(code, reason);
                    _listener.OnDisconnected(this, e);
                }
            }

            _ws.Stop(WebSocketCloseStatus.NormalClosure, "");
        }

        private async void _connect()
        {
            if (_ws != null)
            {
                Task t = _ws.Stop(WebSocketCloseStatus.NormalClosure, "");
                t.Wait();
            }

            var factory = new Func<ClientWebSocket>(() =>
            {
                var c = new ClientWebSocket();

                if (_opts.Headers != null)
                {
                    foreach (var pair in _opts.Headers)
                    {
                        c.Options.SetRequestHeader(pair.Key, pair.Value);
                    }
                }

                c.Options.AddSubProtocol("centrifuge-protobuf");

                return c;
            });

            var client = new WebsocketClient(new Uri(_endpoint), factory);
            client.IsReconnectionEnabled = false;

            client.MessageReceived.Subscribe(result =>
            {
                _executor.StartNew(() =>
                {
                    if (_state != ClientState.CONNECTING && _state != ClientState.CONNECTED)
                    {
                        return;
                    }

                    using (var stream = new MemoryStream(result.Binary))
                    {
                        while (stream.Position < stream.Length)
                        {
                            Protocol.Reply reply;
                            try
                            {
                                reply = Protocol.Reply.Parser.ParseDelimitedFrom(stream);
                            }
                            catch (Exception ex)
                            {
                                // Should never happen. Corrupted server protocol data?
                                Console.WriteLine(ex.StackTrace);
                                _listener.OnError(this, new ErrorEvent(new UnclassifiedError(ex)));
                                ProcessDisconnect(DISCONNECTED_BAD_PROTOCOL, "bad protocol (proto)", false);
                                break;
                            }

                            try
                            {
                                ProcessReply(reply);
                            }
                            catch (Exception ex)
                            {
                                // Should never happen. Most probably indicates an unexpected exception coming from the user-level code.
                                // Theoretically may indicate a bug of SDK also – stack trace will help here.
                                Console.WriteLine(ex.StackTrace);
                                _listener.OnError(this, new ErrorEvent(new UnclassifiedError(ex)));
                                ProcessDisconnect(DISCONNECTED_BAD_PROTOCOL, "bad protocol (message)", false);
                                break;
                            }
                        }
                    }
                });
            });

            client.DisconnectionHappened.Subscribe(info =>
            {
                _executor.StartNew(() =>
                {
                    // todo
                    //     boolean reconnect = code < 3500 || code >= 5000 || (code >= 4000 && code < 4500);
                    //     int disconnectCode = code;
                    //     String disconnectReason = reason;
                    //     if (disconnectCode < 3000) {
                    //         if (disconnectCode == MESSAGE_SIZE_LIMIT_EXCEEDED_STATUS) {
                    //             disconnectCode = DISCONNECTED_MESSAGE_SIZE_LIMIT;
                    //             disconnectReason = "message size limit";
                    //         } else {
                    //             disconnectCode = CONNECTING_TRANSPORT_CLOSED;
                    //             disconnectReason = "transport closed";
                    //         }
                    //     }
                    if (_state != ClientState.DISCONNECTED)
                    {
                        ProcessDisconnect(CONNECTING_TRANSPORT_CLOSED, "transport closed", false);
                        // ProcessDisconnect(CONNECTING_TRANSPORT_CLOSED, "transport closed", reconnect);
                    }

                    if (_state == ClientState.CONNECTING)
                    {
                        ScheduleReconnect();
                    }
                });
            });

            try
            {
                await client.StartOrFail();
            }
            catch (Exception e)
            {
                HandleConnectionError(e);
                ProcessDisconnect(CONNECTING_TRANSPORT_CLOSED, "transport closed", true);
                if (_state == ClientState.CONNECTING)
                {
                    ScheduleReconnect();
                }

                return;
            }

            _ws = client;
            _executor.StartNew(async () =>
            {
                try
                {
                    await HandleConnectionOpen();
                }
                catch (Exception ex)
                {
                    // Should never happen.
                    Console.WriteLine(ex.StackTrace);
                    _listener.OnError(this, new ErrorEvent(new UnclassifiedError(ex)));
                    ProcessDisconnect(DISCONNECTED_BAD_PROTOCOL, "bad protocol (open)", false);
                }
            });
        }

        private async Task HandleConnectionOpen()
        {
            if (_state != ClientState.CONNECTING)
            {
                return;
            }

            if (_refreshRequired || (_token == null && _opts.TokenGetter != null))
            {
                ConnectionTokenEvent connectionTokenEvent = new ConnectionTokenEvent();
                if (_opts.TokenGetter == null)
                {
                    _listener.OnError(this, new ErrorEvent(new ConfigurationError(new Exception("tokenGetter function should be provided in Client options to handle token refresh, see Options.setTokenGetter"))));
                    ProcessDisconnect(DISCONNECTED_UNAUTHORIZED, "unauthorized", false);
                    return;
                }

                _opts.TokenGetter.GetConnectionToken(connectionTokenEvent, (err, token) =>
                {
                    _executor.StartNew(() =>
                    {
                        if (_state != ClientState.CONNECTING)
                        {
                            return;
                        }

                        if (err != null)
                        {
                            _listener.OnError(this, new ErrorEvent(new TokenError(err)));
                            _ws.Stop(WebSocketCloseStatus.NormalClosure, "");
                            return;
                        }

                        if (token == null || token.Equals(""))
                        {
                            FailUnauthorized();
                            return;
                        }

                        _token = token;
                        _refreshRequired = false;
                        SendConnect();
                    });
                });
            }
            else
            {
                SendConnect();
            }
        }

        private void HandleConnectionError(Exception t)
        {
            _listener.OnError(this, new ErrorEvent(t));
        }


        private void StartReconnecting()
        {
            _executor.StartNew(() =>
            {
                if (_state != ClientState.CONNECTING)
                {
                    return;
                }

                _connect();
            });
        }

        private void ScheduleReconnect()
        {
            if (_state != ClientState.CONNECTING)
            {
                return;
            }

            var delay = _backoff.Duration(_reconnectAttempts, _opts.MinReconnectDelay, _opts.MaxReconnectDelay);
            _reconnectTaskCts = new CancellationTokenSource();
            _reconnectTask = Task.Delay(delay).ContinueWith((_) => { StartReconnecting(); }, _reconnectTaskCts.Token);
            _reconnectAttempts++;
        }

        private void SendSubscribeSynchronized(string channel, Protocol.SubscribeRequest req)
        {
            Protocol.Command cmd = new Protocol.Command
            {
                Id = GetNextId(),
                Subscribe = req
            };

            var tcs = new TaskCompletionSource<Protocol.Reply>();
            CancellationTokenSource cts = new CancellationTokenSource();
            cts.CancelAfter(_opts.Timeout);

            _futures.TryAdd(cmd.Id, tcs);
            tcs.Task.ContinueWith(t =>
            {
                if (_state != ClientState.CONNECTED)
                {
                    return;
                }

                HandleSubscribeReply(channel, t.Result);
                _futures.TryRemove(cmd.Id, out _);
            }, cts.Token).ContinueWith((t) =>
            {
                if (_state != ClientState.CONNECTED)
                {
                    return;
                }

                _futures.TryRemove(cmd.Id, out _);
                ProcessDisconnect(CONNECTING_SUBSCRIBE_TIMEOUT, "subscribe timeout", true);
            }, TaskContinuationOptions.OnlyOnCanceled);

            Task sendTask = _ws.SendInstant(SerializeCommand(cmd));
            sendTask.Wait();
        }

        public void SendSubscribe(Subscription sub, Protocol.SubscribeRequest req)
        {
            if (_state != ClientState.CONNECTED)
            {
                // Subscription registered and will start subscribing as soon as
                // client will be connected.
                return;
            }

            SendSubscribeSynchronized(sub.GetChannel(), req);
        }

        public void SendUnsubscribe(string channel)
        {
            _executor.StartNew(() => SendUnsubscribeSynchronized(channel));
        }

        private void SendUnsubscribeSynchronized(string channel)
        {
            if (_state != ClientState.CONNECTED)
            {
                return;
            }

            Protocol.UnsubscribeRequest req = new Protocol.UnsubscribeRequest
            {
                Channel = channel
            };

            Protocol.Command cmd = new Protocol.Command
            {
                Id = GetNextId(),
                Unsubscribe = req
            };

            var tcs = new TaskCompletionSource<Protocol.Reply>();
            _futures.TryAdd(cmd.Id, tcs);
            tcs.Task.ContinueWith(t =>
            {
                // No need to handle reply for now.
                _futures.TryRemove(cmd.Id, out _);
            }).ContinueWith(t => { ProcessDisconnect(CONNECTING_UNSUBSCRIBE_ERROR, "unsubscribe error", true); }, TaskContinuationOptions.OnlyOnFaulted);

            _ws.SendInstant(SerializeCommand(cmd));
        }

        private byte[] SerializeCommand(Protocol.Command cmd)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                cmd.WriteDelimitedTo(stream);
                return stream.ToArray();
            }
        }

        private Subscription GetSub(string channel)
        {
            Subscription v;
            bool isExists = _subs.TryGetValue(channel, out v);

            if (isExists)
            {
                return v;
            }

            return null;
        }

        private ServerSubscription GetServerSub(string channel)
        {
            ServerSubscription v;
            bool isExists = _serverSubs.TryGetValue(channel, out v);

            if (isExists)
            {
                return v;
            }

            return null;
        }

        public Subscription NewSubscription(string channel, SubscriptionOptions options, SubscriptionEventListener listener)
        {
            Subscription sub;
            lock (_subs)
            {
                if (GetSub(channel) != null)
                {
                    throw new DuplicateSubscriptionException();
                }

                sub = new Subscription(this, channel, listener, options);
                _subs.TryAdd(channel, sub);
            }

            return sub;
        }

        public Subscription NewSubscription(string channel, SubscriptionEventListener listener)
        {
            return NewSubscription(channel, new SubscriptionOptions(), listener);
        }

        public Subscription GetSubscription(string channel)
        {
            Subscription sub;
            lock (_subs)
            {
                sub = GetSub(channel);
            }

            return sub;
        }

        public void RemoveSubscription(Subscription sub)
        {
            lock (_subs)
            {
                sub.Unsubscribe();
                if (GetSub(sub.GetChannel()) != null)
                {
                    _subs.TryRemove(sub.GetChannel(), out _);
                }
            }
        }

        private void HandleSubscribeReply(string channel, Protocol.Reply reply)
        {
            Subscription sub = GetSub(channel);
            if (sub != null)
            {
                Protocol.SubscribeResult result;
                if (reply.Error != null)
                {
                    if (reply.Error.Code != 0)
                    {
                        ReplyError err = new ReplyError((int)reply.Error.Code, reply.Error.Message, reply.Error.Temporary);
                        sub.SubscribeError(err);
                        return;
                    }
                }

                result = reply.Subscribe;
                sub.MoveToSubscribed(result);
            }
        }

        private void _waitServerPing()
        {
            if (_state != ClientState.CONNECTED)
            {
                return;
            }

            ProcessDisconnect(CONNECTING_NO_PING, "no ping", true);
        }

        private void WaitServerPing()
        {
            _executor.StartNew(() => { _waitServerPing(); });
        }

        private void HandleConnectReply(Protocol.Reply reply)
        {
            if (_state != ClientState.CONNECTING)
            {
                return;
            }


            if (reply.Error != null)
            {
                if (reply.Error.Code != 0)
                {
                    HandleConnectionError(new ReplyError((int)reply.Error.Code, reply.Error.Message, reply.Error.Temporary));
                    if (reply.Error.Code == 109) // Token expired.
                    {
                        _refreshRequired = true;
                        _ws.Stop(WebSocketCloseStatus.NormalClosure, "");
                    }
                    else if (reply.Error.Temporary)
                    {
                        _ws.Stop(WebSocketCloseStatus.NormalClosure, "");
                    }
                    else
                    {
                        ProcessDisconnect((int)reply.Error.Code, reply.Error.Message, false);
                    }

                    return;
                }
            }

            Protocol.ConnectResult result = reply.Connect;
            ConnectedEvent e = new ConnectedEvent()
            {
                Client = result.Client,
                Data = result.Data.ToByteArray()
            };

            _state = ClientState.CONNECTED;
            _listener.OnConnected(this, e);
            _pingInterval = TimeSpan.FromSeconds(result.Ping);
            _sendPong = result.Pong;

            lock (_subs)
            {
                foreach (KeyValuePair<string, Subscription> entry in _subs)
                {
                    Subscription sub = entry.Value;
                    sub.ResubscribeIfNecessary();
                }
            }

            foreach (KeyValuePair<string, Protocol.SubscribeResult> entry in result.Subs)
            {
                Protocol.SubscribeResult subResult = entry.Value;
                string channel = entry.Key;
                ServerSubscription serverSub;
                if (_serverSubs.ContainsKey(channel))
                {
                    serverSub = GetServerSub(channel);
                }
                else
                {
                    serverSub = new ServerSubscription(subResult.Recoverable, subResult.Offset, subResult.Epoch);
                    _serverSubs.TryAdd(channel, serverSub);
                }

                serverSub.Recoverable = subResult.Recoverable;
                serverSub.Epoch = subResult.Epoch;

                byte[] data = null;
                if (subResult.Data != null)
                {
                    data = subResult.Data.ToByteArray();
                }

                _listener.OnSubscribed(this, new ServerSubscribedEvent(channel, subResult.WasRecovering, subResult.Recovered, subResult.Positioned, subResult.Recoverable, subResult.Positioned || subResult.Recoverable ? new StreamPosition(subResult.Offset, subResult.Epoch) : null, data));
                if (subResult.Publications.Count > 0)
                {
                    foreach (Protocol.Publication publication in subResult.Publications)
                    {
                        ServerPublicationEvent publicationEvent = new ServerPublicationEvent()
                        {
                            Channel = channel,
                            Data = publication.Data.ToByteArray(),
                            Tags = new Dictionary<string, string>(publication.Tags),
                        };

                        ClientInfo info = ClientInfo.FromProtocolClientInfo(publication.Info);

                        publicationEvent.Info = info;
                        publicationEvent.Offset = publication.Offset;

                        if (publication.Offset > 0)
                        {
                            serverSub.Offset = publication.Offset;
                        }

                        _listener.OnPublication(this, publicationEvent);
                    }
                }
                else
                {
                    serverSub.Offset = subResult.Offset;
                }
            }

            IEnumerator<KeyValuePair<string, ServerSubscription>> it = _serverSubs.GetEnumerator();
            while (it.MoveNext())
            {
                KeyValuePair<string, ServerSubscription> entry = it.Current;
                if (!result.Subs.ContainsKey(entry.Key))
                {
                    _listener.OnUnsubscribed(this, new ServerUnsubscribedEvent(entry.Key));
                    _serverSubs.TryRemove(entry.Key, out _);
                }
            }

            _reconnectAttempts = 0;

            foreach (KeyValuePair<uint, Protocol.Command> entry in _connectCommands)
            {
                Protocol.Command cmd = entry.Value;
                Task sendTask = _ws.SendInstant(SerializeCommand(cmd));
                sendTask.Wait();
                if (sendTask.IsFaulted)
                {
                    TaskCompletionSource<Protocol.Reply> tcs;
                    _futures.TryGetValue(cmd.Id, out tcs);
                    if (tcs != null)
                    {
                        tcs.TrySetException(new IOException());
                    }
                }
            }

            _connectCommands.Clear();

            foreach (KeyValuePair<uint, Protocol.Command> entry in _connectAsyncCommands)
            {
                Protocol.Command cmd = entry.Value;
                TaskCompletionSource<Protocol.Reply> tcs;
                _futures.TryGetValue(cmd.Id, out tcs);
                Task sendTask = _ws.SendInstant(SerializeCommand(cmd));
                sendTask.Wait();

                if (sendTask.IsFaulted)
                {
                    if (tcs != null)
                    {
                        tcs.TrySetException(new IOException());
                    }
                }
                else
                {
                    if (tcs != null)
                    {
                        tcs.SetResult(null);
                    }
                }
            }

            _connectAsyncCommands.Clear();

            _pingTaskCts = new CancellationTokenSource();
            _pingTask = Task.Delay(_pingInterval.Add(TimeSpan.FromMilliseconds(_opts.MaxServerPingDelay))).ContinueWith((_) => { WaitServerPing(); }, _pingTaskCts.Token);

            if (result.Expires)
            {
                var ttl = (int)Math.Min((double)result.Ttl * 1000, int.MaxValue);
                _refreshTaskCts = new CancellationTokenSource();
                _refreshTask = Task.Delay(ttl).ContinueWith((_) => { SendRefresh(); }, _refreshTaskCts.Token);
            }
        }

        private void SendRefresh()
        {
            if (_opts.TokenGetter == null)
            {
                return;
            }

            _executor.StartNew(() => _opts.TokenGetter.GetConnectionToken(new ConnectionTokenEvent(), (err, token) => _executor.StartNew(() =>
            {
                if (_state != ClientState.CONNECTED)
                {
                    return;
                }

                if (err != null)
                {
                    _listener.OnError(this, new ErrorEvent(new TokenError(err)));
                    _refreshTaskCts = new CancellationTokenSource();
                    _refreshTask = Task.Delay(_backoff.Duration(0, 10000, 20000)).ContinueWith((_) => { SendRefresh(); }, _refreshTaskCts.Token);
                    return;
                }

                if (string.IsNullOrEmpty(token))
                {
                    FailUnauthorized();
                    return;
                }

                _token = token;
                RefreshSynchronized(token, (error, result) =>
                {
                    if (_state != ClientState.CONNECTED)
                    {
                        return;
                    }

                    if (error != null)
                    {
                        _listener.OnError(this, new ErrorEvent(new RefreshError(error)));
                        if (error is ReplyError)
                        {
                            ReplyError e;
                            e = (ReplyError)error;
                            if (e.IsTemporary)
                            {
                                _refreshTaskCts = new CancellationTokenSource();
                                _refreshTask = Task.Delay(_backoff.Duration(0, 10000, 20000)).ContinueWith((_) => { SendRefresh(); }, _refreshTaskCts.Token);
                            }
                            else
                            {
                                ProcessDisconnect(e.Code, e.Message, false);
                            }

                            return;
                        }
                        else
                        {
                            _refreshTaskCts = new CancellationTokenSource();
                            _refreshTask = Task.Delay(_backoff.Duration(0, 10000, 20000)).ContinueWith((_) => { SendRefresh(); }, _refreshTaskCts.Token);
                        }

                        return;
                    }

                    if (result.Expires)
                    {
                        var ttl = (int)Math.Min((double)result.Ttl * 1000, int.MaxValue);
                        _refreshTaskCts = new CancellationTokenSource();
                        _refreshTask = Task.Delay(ttl).ContinueWith((_) => { SendRefresh(); }, _refreshTaskCts.Token);
                    }
                });
            })));
        }

        private void SendConnect()
        {
            Protocol.ConnectRequest req = new Protocol.ConnectRequest();
            if (!string.IsNullOrEmpty(_token))
            {
                req.Token = _token;
            }

            if (!string.IsNullOrEmpty(_opts.Name))
            {
                req.Name = _opts.Name;
            }

            if (!string.IsNullOrEmpty(_opts.Version))
            {
                req.Version = _opts.Version;
            }

            if (_data != null)
            {
                req.Data = _data;
            }

            if (_serverSubs.Count > 0)
            {
                foreach (KeyValuePair<string, ServerSubscription> entry in _serverSubs)
                {
                    Protocol.SubscribeRequest subReq = new Protocol.SubscribeRequest();
                    if (entry.Value.Recoverable)
                    {
                        subReq.Epoch = entry.Value.Epoch;
                        subReq.Offset = entry.Value.Offset;
                        subReq.Recover = true;
                    }

                    req.Subs[entry.Key] = subReq;
                }
            }

            Protocol.Command cmd = new Protocol.Command()
            {
                Id = GetNextId(),
                Connect = req
            };

            var tcs = new TaskCompletionSource<Protocol.Reply>();
            CancellationTokenSource cts = new CancellationTokenSource();
            cts.CancelAfter(_opts.Timeout);

            _futures.TryAdd(cmd.Id, tcs);
            tcs.Task.ContinueWith(t =>
            {
                _futures.TryRemove(cmd.Id, out _);
                try
                {
                    HandleConnectReply(t.Result);
                }
                catch (Exception e)
                {
                    // Should never happen.
                    HandleConnectionError(e);
                    _ws.Stop(WebSocketCloseStatus.NormalClosure, "");
                }
            }, cts.Token).ContinueWith(t =>
            {
                HandleConnectionError(t.Exception);
                _futures.TryRemove(cmd.Id, out _);
                _ws.Stop(WebSocketCloseStatus.NormalClosure, "");
            }, TaskContinuationOptions.OnlyOnCanceled);

            Task sendTask = _ws.SendInstant(SerializeCommand(cmd));
            sendTask.Wait();
        }

        private void FailUnauthorized()
        {
            ProcessDisconnect(DISCONNECTED_UNAUTHORIZED, "unauthorized", false);
        }

        private void ProcessReply(Protocol.Reply reply)
        {
            if (reply.Id > 0)
            {
                TaskCompletionSource<Protocol.Reply> tcs;
                _futures.TryGetValue(reply.Id, out tcs);
                if (tcs != null)
                {
                    tcs.SetResult(reply);
                }
            }
            else
            {
                if (reply.Push != null)
                {
                    HandlePush(reply.Push);
                }
                else
                {
                    HandlePing();
                }
            }
        }

        private void HandlePub(string channel, Protocol.Publication pub)
        {
            var info = ClientInfo.FromProtocolClientInfo(pub.Info);
            var sub = GetSub(channel);
            if (sub != null)
            {
                var evnt = new PublicationEvent()
                {
                    Data = pub.Data.ToByteArray(),
                    Info = info,
                    Offset = pub.Offset,
                    Tags = new Dictionary<string, string>(pub.Tags)
                };

                if (pub.Offset > 0)
                {
                    sub.SetOffset(pub.Offset);
                }

                sub.GetListener().OnPublication(sub, evnt);
            }
            else
            {
                var serverSub = GetServerSub(channel);
                if (serverSub != null)
                {
                    var evnt = new ServerPublicationEvent()
                    {
                        Channel = channel,
                        Data = pub.Data.ToByteArray(),
                        Info = info,
                        Offset = pub.Offset,
                        Tags = new Dictionary<string, string>(pub.Tags),
                    };

                    if (pub.Offset > 0)
                    {
                        serverSub.Offset = pub.Offset;
                    }

                    _listener.OnPublication(this, evnt);
                }
            }
        }

        private void HandleSubscribe(string channel, Protocol.Subscribe sub)
        {
            var serverSub = new ServerSubscription(sub.Recoverable, sub.Offset, sub.Epoch);
            _serverSubs.TryAdd(channel, serverSub);

            serverSub.Recoverable = sub.Recoverable;
            serverSub.Epoch = sub.Epoch;
            serverSub.Offset = sub.Offset;

            byte[] data = null;
            if (sub.Data != null)
            {
                data = sub.Data.ToByteArray();
            }

            _listener.OnSubscribed(this, new ServerSubscribedEvent(channel, false, false, sub.Positioned, sub.Recoverable, sub.Positioned || sub.Recoverable ? new StreamPosition(sub.Offset, sub.Epoch) : null, data));
        }

        private void HandleUnsubscribe(string channel, Protocol.Unsubscribe unsubscribe)
        {
            var sub = GetSub(channel);
            if (sub != null)
            {
                if (unsubscribe.Code < 2500)
                {
                    sub.MoveToUnsubscribed(false, (int)unsubscribe.Code, unsubscribe.Reason);
                }
                else
                {
                    sub.MoveToSubscribing((int)unsubscribe.Code, unsubscribe.Reason);
                    sub.ResubscribeIfNecessary();
                }
            }
            else
            {
                var serverSub = GetServerSub(channel);
                if (serverSub != null)
                {
                    _serverSubs.TryRemove(channel, out _);
                    _listener.OnUnsubscribed(this, new ServerUnsubscribedEvent(channel));
                }
            }
        }

        private void HandleJoin(string channel, Protocol.Join join)
        {
            var info = ClientInfo.FromProtocolClientInfo(join.Info);
            var sub = GetSub(channel);
            if (sub != null)
            {
                var evnt = new JoinEvent()
                {
                    Info = info
                };

                sub.GetListener().OnJoin(sub, evnt);
            }
            else
            {
                var serverSub = GetServerSub(channel);
                if (serverSub != null)
                {
                    _listener.OnJoin(this, new ServerJoinEvent(channel, info));
                }
            }
        }

        private void HandleLeave(string channel, Protocol.Leave leave)
        {
            var evnt = new LeaveEvent();
            var info = ClientInfo.FromProtocolClientInfo(leave.Info);

            var sub = GetSub(channel);
            if (sub != null)
            {
                evnt.Info = info;
                sub.GetListener().OnLeave(sub, evnt);
            }
            else
            {
                var serverSub = GetServerSub(channel);
                if (serverSub != null)
                {
                    _listener.OnLeave(this, new ServerLeaveEvent(channel, info));
                }
            }
        }

        private void HandleMessage(Protocol.Message msg)
        {
            var evnt = new MessageEvent()
            {
                Data = msg.Data.ToByteArray(),
            };
            _listener.OnMessage(this, evnt);
        }

        private void HandleDisconnect(Protocol.Disconnect disconnect)
        {
            var code = disconnect.Code;
            var reconnect = code < 3500 || code >= 5000 || (code >= 4000 && code < 4500);
            if (_state != ClientState.DISCONNECTED)
            {
                ProcessDisconnect((int)code, disconnect.Reason, reconnect);
            }
        }

        private void HandlePush(Protocol.Push push)
        {
            var channel = push.Channel;
            if (push.Pub != null)
            {
                HandlePub(channel, push.Pub);
            }
            else if (push.Subscribe != null)
            {
                HandleSubscribe(channel, push.Subscribe);
            }
            else if (push.Join != null)
            {
                HandleJoin(channel, push.Join);
            }
            else if (push.Leave != null)
            {
                HandleLeave(channel, push.Leave);
            }
            else if (push.Unsubscribe != null)
            {
                HandleUnsubscribe(channel, push.Unsubscribe);
            }
            else if (push.Message != null)
            {
                HandleMessage(push.Message);
            }
            else if (push.Disconnect != null)
            {
                HandleDisconnect(push.Disconnect);
            }
        }

        private void HandlePing()
        {
            if (_pingTask != null)
            {
                _pingTaskCts.Cancel();
            }

            _pingTaskCts = new CancellationTokenSource();
            _pingTask = Task.Delay(_pingInterval.Add(TimeSpan.FromMilliseconds(_opts.MaxServerPingDelay))).ContinueWith((_) => { WaitServerPing(); }, _pingTaskCts.Token);
            if (_sendPong)
            {
                // Empty command as a ping.
                var cmd = new Protocol.Command();
                Task sendTask = _ws.SendInstant(SerializeCommand(cmd));
                sendTask.Wait();
            }
        }

        /// <summary>
        /// Send asynchronous message with data to server. Callback successfully completes if data
        /// written to connection. No reply from server expected in this case.
        /// </summary>
        /// <param name="data">Custom data to publish.</param>
        /// <param name="cb">Will be called as soon as data is sent to the connection or an error occurs.</param>
        public void Send(byte[] data, CompletionCallback cb)
        {
            _executor.StartNew(() => { SendSynchronized(data, cb); });
        }

        private void SendSynchronized(byte[] data, CompletionCallback cb)
        {
            var req = new Protocol.SendRequest
            {
                Data = ByteString.CopyFrom(data)
            };

            var cmd = new Protocol.Command
            {
                Id = GetNextId(),
                Send = req
            };

            var tcs = new TaskCompletionSource<Protocol.Reply>();
            CancellationTokenSource cts = new CancellationTokenSource();
            cts.CancelAfter(_opts.Timeout);

            _futures.TryAdd(cmd.Id, tcs);
            tcs.Task.ContinueWith(reply =>
            {
                CleanCommandFuture(cmd);
                cb(null);
            }, cts.Token).ContinueWith((reply) =>
            {
                CleanCommandFuture(cmd);
                cb(reply.Exception);
            }, TaskContinuationOptions.OnlyOnCanceled);

            if (_state != ClientState.CONNECTED)
            {
                _connectAsyncCommands.TryAdd(cmd.Id, cmd);
            }
            else
            {
                Task sendTask = _ws.SendInstant(SerializeCommand(cmd));
                sendTask.Wait();
                if (sendTask.IsFaulted)
                {
                    tcs.SetException(new IOException());
                }
                else
                {
                    tcs.SetResult(null);
                }
            }
        }

        private void EnqueueCommandFuture(Protocol.Command cmd, TaskCompletionSource<Protocol.Reply> f)
        {
            _futures.TryAdd(cmd.Id, f);
            if (_state != ClientState.CONNECTED)
            {
                _connectCommands.TryAdd(cmd.Id, cmd);
            }
            else
            {
                Task sendTask = _ws.SendInstant(SerializeCommand(cmd));
                sendTask.Wait();
                if (sendTask.IsFaulted)
                {
                    f.SetException(new IOException());
                }
            }
        }

        private ReplyError GetReplyError(Protocol.Reply reply)
        {
            return new ReplyError((int)reply.Error.Code, reply.Error.Message, reply.Error.Temporary);
        }

        private void CleanCommandFuture(Protocol.Command cmd)
        {
            _futures.TryRemove(cmd.Id, out _);
            _connectCommands.TryRemove(cmd.Id, out _);
            _connectAsyncCommands.TryRemove(cmd.Id, out _);
        }

        /// <summary>
        /// Send RPC with method to server and process the result in a callback.
        /// </summary>
        /// <param name="method">Method of the RPC call.</param>
        /// <param name="data">Custom payload for the RPC call.</param>
        /// <param name="cb">Will be called as soon as the RPC response is received or an error occurs.</param>
        public void Rpc(string method, byte[] data, ResultCallback<RPCResult> cb)
        {
            _executor.StartNew(() => RpcSynchronized(method, data, cb));
        }

        private void RpcSynchronized(string method, byte[] data, ResultCallback<RPCResult> cb)
        {
            var req = new Protocol.RPCRequest
            {
                Data = Google.Protobuf.ByteString.CopyFrom(data),
                Method = method
            };

            var cmd = new Protocol.Command
            {
                Id = GetNextId(),
                Rpc = req
            };

            var tcs = new TaskCompletionSource<Protocol.Reply>();
            CancellationTokenSource cts = new CancellationTokenSource();
            cts.CancelAfter(_opts.Timeout);

            tcs.Task.ContinueWith(reply =>
            {
                CleanCommandFuture(cmd);
                if (reply.Result.Error != null && reply.Result.Error.Code != 0)
                {
                    cb(GetReplyError(reply.Result), null);
                }
                else
                {
                    var rpcResult = reply.Result.Rpc;
                    var result = new RPCResult()
                    {
                        Data = rpcResult.Data.ToByteArray()
                    };
                    cb(null, result);
                }
            }, cts.Token).ContinueWith((reply) =>
            {
                CleanCommandFuture(cmd);
                cb(reply.Exception, null);
            }, TaskContinuationOptions.OnlyOnCanceled);

            EnqueueCommandFuture(cmd, tcs);
        }

        /// <summary>
        /// Publish data to a channel without being subscribed to it. The publish option should be
        /// enabled in the Centrifuge/Centrifugo server configuration.
        /// </summary>
        /// <param name="channel">Channel to publish into.</param>
        /// <param name="data">Data to publish.</param>
        /// <param name="cb">Will be called as soon as the publish response is received or an error occurs.</param>
        public void Publish(string channel, byte[] data, ResultCallback<PublishResult> cb)
        {
            _executor.StartNew(() => PublishSynchronized(channel, data, cb));
        }

        private void PublishSynchronized(string channel, byte[] data, ResultCallback<PublishResult> cb)
        {
            var req = new Protocol.PublishRequest
            {
                Channel = channel,
                Data = ByteString.CopyFrom(data)
            };

            var cmd = new Protocol.Command
            {
                Id = GetNextId(),
                Publish = req
            };

            var tcs = new TaskCompletionSource<Protocol.Reply>();
            CancellationTokenSource cts = new CancellationTokenSource();
            cts.CancelAfter(_opts.Timeout);

            tcs.Task.ContinueWith(reply =>
            {
                CleanCommandFuture(cmd);
                if (reply.Result.Error.Code != 0)
                {
                    cb(GetReplyError(reply.Result), null);
                }
                else
                {
                    var result = new PublishResult();
                    cb(null, result);
                }
            }, cts.Token).ContinueWith((reply) =>
            {
                CleanCommandFuture(cmd);
                cb(reply.Exception, null);
            }, TaskContinuationOptions.OnlyOnCanceled);

            EnqueueCommandFuture(cmd, tcs);
        }

        private void RefreshSynchronized(string token, ResultCallback<Protocol.RefreshResult> cb)
        {
            Protocol.RefreshRequest req = new Protocol.RefreshRequest()
            {
                Token = token
            };

            Protocol.Command cmd = new Protocol.Command()
            {
                Id = GetNextId(),
                Refresh = req
            };

            TaskCompletionSource<Protocol.Reply> tcs = new TaskCompletionSource<Protocol.Reply>();
            CancellationTokenSource cts = new CancellationTokenSource();
            cts.CancelAfter(_opts.Timeout);

            tcs.Task.ContinueWith(task =>
            {
                var reply = task.Result;

                CleanCommandFuture(cmd);
                if (reply.Error != null && reply.Error.Code != 0)
                {
                    cb(GetReplyError(reply), null);
                }
                else
                {
                    Protocol.RefreshResult replyResult = reply.Refresh;
                    cb(null, replyResult);
                }
            }, cts.Token).ContinueWith((task) =>
            {
                CleanCommandFuture(cmd);
                cb(task.Exception, null);
            }, TaskContinuationOptions.OnlyOnCanceled);

            EnqueueCommandFuture(cmd, tcs);
        }

        public void SubRefreshSynchronized(string channel, string token, ResultCallback<Protocol.SubRefreshResult> cb)
        {
            Protocol.SubRefreshRequest req = new Protocol.SubRefreshRequest()
            {
                Token = token,
                Channel = channel
            };

            Protocol.Command cmd = new Protocol.Command()
            {
                Id = GetNextId(),
                SubRefresh = req
            };

            TaskCompletionSource<Protocol.Reply> f = new TaskCompletionSource<Protocol.Reply>();
            CancellationTokenSource cts = new CancellationTokenSource();
            cts.CancelAfter(_opts.Timeout);

            f.Task.ContinueWith(task =>
            {
                var reply = task.Result;

                CleanCommandFuture(cmd);
                if (reply.Error != null && reply.Error.Code != 0)
                {
                    cb(GetReplyError(reply), null);
                }
                else
                {
                    Protocol.SubRefreshResult replyResult = reply.SubRefresh;
                    cb(null, replyResult);
                }
            }, cts.Token).ContinueWith((task) =>
            {
                CleanCommandFuture(cmd);
                cb(task.Exception, null);
            }, TaskContinuationOptions.OnlyOnCanceled);

            EnqueueCommandFuture(cmd, f);
        }
    }
}