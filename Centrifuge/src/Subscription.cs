using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;

namespace Centrifuge
{
    public class Subscription
    {
        private readonly Client _client;
        private readonly string _channel;
        private readonly SubscriptionOptions _opts;
        private bool _recover;
        private ulong _offset;
        private string _epoch;
        private readonly SubscriptionEventListener _listener;
        private volatile SubscriptionState _state = SubscriptionState.UNSUBSCRIBED;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<Exception>> _futures = new ConcurrentDictionary<string, TaskCompletionSource<Exception>>();
        private readonly Backoff _backoff;

        private Task _refreshTask;
        private CancellationTokenSource _refreshTaskCts = new CancellationTokenSource();

        private Task _resubscribeTask;
        private CancellationTokenSource _resubscribeTaskCts = new CancellationTokenSource();


        private int _resubscribeAttempts;
        private string _token;
        private readonly ByteString _data;

        public Subscription(Client client, string channel, SubscriptionEventListener listener, SubscriptionOptions options)
        {
            _client = client;
            _channel = channel;
            _listener = listener;
            _backoff = new Backoff();
            _opts = options;
            _token = options.Token;
            if (_opts.Data != null)
            {
                _data = ByteString.CopyFrom(options.Data);
            }
        }

        public SubscriptionState GetState()
        {
            return _state;
        }

        public string GetChannel()
        {
            return _channel;
        }

        public SubscriptionEventListener GetListener()
        {
            return _listener;
        }

        public void SetOffset(ulong offset)
        {
            _offset = offset;
        }

        // Access must be synchronized.
        public void ResubscribeIfNecessary()
        {
            if (_state != SubscriptionState.SUBSCRIBING)
            {
                return;
            }

            SendSubscribe();
        }

        private void SendRefresh()
        {
            if (_opts.TokenGetter == null)
            {
                return;
            }

            _client.GetExecutor().StartNew(() =>
            {
                _opts.TokenGetter.GetSubscriptionToken(new SubscriptionTokenEvent(_channel), (err, token) =>
                {
                    if (_state != SubscriptionState.SUBSCRIBED)
                    {
                        return;
                    }

                    if (err != null)
                    {
                        _listener.OnError(this, new SubscriptionErrorEvent(new SubscriptionTokenError(err)));
                        _refreshTaskCts = new CancellationTokenSource();
                        _refreshTask = Task.Delay(_backoff.Duration(0, 10000, 20000)).ContinueWith((_) => { SendRefresh(); }, _refreshTaskCts.Token);
                        return;
                    }

                    if (string.IsNullOrEmpty(token))
                    {
                        FailUnauthorized(true);
                        return;
                    }

                    _token = token;
                    _client.SubRefreshSynchronized(_channel, token, (error, result) =>
                    {
                        if (_state != SubscriptionState.SUBSCRIBED)
                        {
                            return;
                        }

                        var errorOrNull = error != null ? error : (result == null ? new NullReferenceException() : null);
                        if (errorOrNull != null)
                        {
                            _listener.OnError(this, new SubscriptionErrorEvent(new SubscriptionRefreshError(errorOrNull)));
                            if (error is ReplyError)
                            {
                                var e = (ReplyError)error;
                                if (e.IsTemporary)
                                {
                                    _refreshTaskCts = new CancellationTokenSource();
                                    _refreshTask = Task.Delay(_backoff.Duration(0, 10000, 20000)).ContinueWith((_) => { SendRefresh(); }, _refreshTaskCts.Token);
                                }
                                else
                                {
                                    _unsubscribe(true, e.Code, e.Message);
                                }
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
                            _refreshTaskCts = new CancellationTokenSource();
                            _refreshTask = Task.Delay((int)result.Ttl * 1000).ContinueWith((_) => { SendRefresh(); }, _refreshTaskCts.Token);
                        }
                    });
                });
            });
        }

        public void MoveToSubscribing(int code, string reason)
        {
            if (_state == SubscriptionState.SUBSCRIBING)
            {
                ClearSubscribingState();
                return;
            }

            _state = SubscriptionState.SUBSCRIBING;
            _listener.OnSubscribing(this, new SubscribingEvent(code, reason));
        }

        public void MoveToUnsubscribed(bool sendUnsubscribe, int code, string reason)
        {
            if (_state == SubscriptionState.UNSUBSCRIBED)
            {
                return;
            }

            _unsubscribe(sendUnsubscribe, code, reason);
        }

        public void MoveToSubscribed(Protocol.SubscribeResult result)
        {
            _state = SubscriptionState.SUBSCRIBED;
            _epoch = result.Epoch;

            if (result.Recoverable)
            {
                _recover = true;
            }

            byte[] data = null;
            if (result.Data != null)
            {
                data = result.Data.ToByteArray();
            }

            var e = new SubscribedEvent(result.WasRecovering, result.Recovered, result.Positioned, result.Recoverable, result.Positioned || result.Recoverable ? new StreamPosition(result.Offset, result.Epoch) : null, data);
            _listener.OnSubscribed(this, e);

            if (result.Publications.Count > 0)
            {
                foreach (Protocol.Publication publication in result.Publications)
                {
                    _listener.OnPublication(this, new PublicationEvent()
                    {
                        Data = publication.Data.ToByteArray(),
                        Offset = publication.Offset
                    });

                    _offset = publication.Offset;
                }
            }
            else
            {
                _offset = result.Offset;
            }

            foreach (var entry in _futures)
            {
                var f = entry.Value;
                f.SetResult(null);
            }

            _futures.Clear();

            if (result.Expires)
            {
                _refreshTaskCts = new CancellationTokenSource();
                _refreshTask = Task.Delay((int)result.Ttl * 1000).ContinueWith((_) => { SendRefresh(); }, _refreshTaskCts.Token);
            }
        }

        public void SubscribeError(ReplyError err)
        {
            _listener.OnError(this, new SubscriptionErrorEvent(new SubscriptionSubscribeError(err)));
            if (err.Code == 109) // Token expired.
            {
                _token = "";
                ScheduleResubscribe();
            }

            if (err.IsTemporary)
            {
                ScheduleResubscribe();
            }
            else
            {
                _unsubscribe(false, err.Code, err.Message);
            }
        }

        public void Subscribe()
        {
            _client.GetExecutor().StartNew(() =>
            {
                if (_state == SubscriptionState.SUBSCRIBED || _state == SubscriptionState.SUBSCRIBING)
                {
                    return;
                }

                _state = SubscriptionState.SUBSCRIBING;
                _listener.OnSubscribing(this, new SubscribingEvent(Client.SUBSCRIBING_SUBSCRIBE_CALLED, "subscribe called"));
                SendSubscribe();
            });
        }

        private Protocol.SubscribeRequest CreateSubscribeRequest()
        {
            StreamPosition streamPosition = new StreamPosition();

            if (_recover)
            {
                streamPosition.Offset = _offset;
                streamPosition.Epoch = _epoch;
            }

            Protocol.SubscribeRequest subscribeRequest = new Protocol.SubscribeRequest();
            subscribeRequest.Channel = _channel;
            subscribeRequest.Token = _token;

            if (_data != null)
            {
                subscribeRequest.Data = _data;
            }

            if (_recover)
            {
                subscribeRequest.Recoverable = true;
                subscribeRequest.Epoch = streamPosition.Epoch;
                subscribeRequest.Offset = streamPosition.Offset;
            }

            subscribeRequest.Positioned = _opts.IsPositioned;
            subscribeRequest.Recoverable = _opts.IsRecoverable;
            subscribeRequest.JoinLeave = _opts.JoinLeave;

            return subscribeRequest;
        }

        private void SendSubscribe()
        {
            StreamPosition streamPosition = new StreamPosition();

            if (_recover)
            {
                streamPosition.Offset = _offset;
                streamPosition.Epoch = _epoch;
            }

            if (string.IsNullOrEmpty(_token) && _opts.TokenGetter != null)
            {
                SubscriptionTokenEvent subscriptionTokenEvent = new SubscriptionTokenEvent(_channel);
                _opts.TokenGetter.GetSubscriptionToken(subscriptionTokenEvent, (err, token) =>
                {
                    _client.GetExecutor().StartNew(() =>
                    {
                        if (_state != SubscriptionState.SUBSCRIBING)
                        {
                            return;
                        }

                        if (err != null)
                        {
                            _listener.OnError(this, new SubscriptionErrorEvent(new SubscriptionTokenError(err)));
                            ScheduleResubscribe();
                            return;
                        }

                        if (string.IsNullOrEmpty(token))
                        {
                            FailUnauthorized(false);
                            return;
                        }

                        _token = token;
                        _client.SendSubscribe(this, CreateSubscribeRequest());
                    });
                });
            }
            else
            {
                _client.SendSubscribe(this, CreateSubscribeRequest());
            }
        }

        public void Unsubscribe()
        {
            _client.GetExecutor().StartNew(() => { _unsubscribe(true, Client.UNSUBSCRIBED_UNSUBSCRIBE_CALLED, "unsubscribe called"); });
        }

        private void ClearSubscribedState()
        {
            if (_refreshTask != null)
            {
                _refreshTaskCts.Cancel();
                _refreshTask = null;
            }
        }

        private void ClearSubscribingState()
        {
            if (_resubscribeTask != null)
            {
                _resubscribeTaskCts.Cancel();
                _resubscribeTask = null;
            }
        }

        private void _unsubscribe(bool sendUnsubscribe, int code, string reason)
        {
            if (_state == SubscriptionState.UNSUBSCRIBED)
            {
                return;
            }

            if (_state == SubscriptionState.SUBSCRIBED)
            {
                ClearSubscribedState();
            }
            else if (_state == SubscriptionState.SUBSCRIBING)
            {
                ClearSubscribingState();
            }

            _state = SubscriptionState.UNSUBSCRIBED;
            if (sendUnsubscribe)
            {
                _client.SendUnsubscribe(_channel);
            }

            foreach (var entry in _futures)
            {
                var f = entry.Value;
                f.SetResult(new SubscriptionStateError(_state));
            }

            _futures.Clear();
            _listener.OnUnsubscribed(this, new UnsubscribedEvent(code, reason));
        }

        private void ScheduleResubscribe()
        {
            if (_state != SubscriptionState.SUBSCRIBING)
            {
                return;
            }

            _resubscribeTaskCts = new CancellationTokenSource();
            _resubscribeTask = Task.Delay(_backoff.Duration(_resubscribeAttempts, _opts.MinResubscribeDelay, _opts.MaxResubscribeDelay)).ContinueWith((_) => { StartResubscribing(); }, _resubscribeTaskCts.Token);
            _resubscribeAttempts++;
        }

        private void StartResubscribing()
        {
            _client.GetExecutor().StartNew(SendSubscribe);
        }

        private void FailUnauthorized(bool sendUnsubscribe)
        {
            _unsubscribe(sendUnsubscribe, Client.UNSUBSCRIBED_UNAUTHORIZED, "unauthorized");
        }

        public void Publish(byte[] data, ResultCallback<PublishResult> cb)
        {
            _client.GetExecutor().StartNew(() => PublishSynchronized(data, cb));
        }

        private void PublishSynchronized(byte[] data, ResultCallback<PublishResult> cb)
        {
            var tcs = new TaskCompletionSource<Exception>();
            CancellationTokenSource cts = new CancellationTokenSource();
            cts.CancelAfter(_client.GetOpts().Timeout);

            var uuid = Guid.NewGuid().ToString();
            _futures.TryAdd(uuid, tcs);


            tcs.Task.ContinueWith((res) =>
            {
                if (res.Exception != null)
                {
                    cb(res.Exception, null);
                    return;
                }

                _futures.TryRemove(uuid, out _);
                _client.Publish(_channel, data, cb);
            }, cts.Token).ContinueWith((res) =>
            {
                _futures.TryRemove(uuid, out _);
                cb(res.Exception, null);
            }, TaskContinuationOptions.OnlyOnCanceled);

            if (_state == SubscriptionState.SUBSCRIBED)
            {
                tcs.SetResult(null);
            }
        }
    }
}