﻿using Binance.Api;
using Binance.Api.WebSocket;
using Binance.Api.WebSocket.Events;
using Binance.Cache.Events;
using Binance.Market;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace Binance.Cache
{
    public class OrderBookCache : IOrderBookCache
    {
        #region Public Events

        public event EventHandler<OrderBookCacheEventArgs> Update;

        #endregion Public Events

        #region Public Properties

        public OrderBook OrderBook => _orderBookClone;

        public IDepthWebSocketClient Client { get; }

        #endregion Public Properties

        #region Private Fields

        private readonly IBinanceApi _api;

        private readonly ILogger<OrderBookCache> _logger;

        private bool _leaveClientOpen;

        private BufferBlock<DepthUpdateEventArgs> _bufferBlock;
        private ActionBlock<DepthUpdateEventArgs> _actionBlock;

        private Action<OrderBookCacheEventArgs> _callback;

        private OrderBook _orderBook;
        private OrderBook _orderBookClone;

        private string _symbol;
        private int _limit;
        private CancellationToken _token;

        #endregion Private Fields

        #region Constructors

        public OrderBookCache(IBinanceApi api, IDepthWebSocketClient client, bool leaveClientOpen = false, ILogger<OrderBookCache> logger = null)
        {
            Throw.IfNull(api, nameof(api));
            Throw.IfNull(client, nameof(client));

            _api = api;
            _logger = logger;

            Client = client;
            _leaveClientOpen = leaveClientOpen;
        }

        #endregion Constructors

        #region Public Methods

        public Task SubscribeAsync(string symbol, int limit = default, CancellationToken token = default)
            => SubscribeAsync(symbol, null, limit, token);

        public Task SubscribeAsync(string symbol, Action<OrderBookCacheEventArgs> callback, int limit = default, CancellationToken token = default)
        {
            Throw.IfNullOrWhiteSpace(symbol, nameof(symbol));

            _symbol = symbol;
            _limit = limit;
            _token = token;

            LinkTo(Client, callback, _leaveClientOpen);

            return Client.SubscribeAsync(symbol, token);
        }

        public void LinkTo(IDepthWebSocketClient client, Action<OrderBookCacheEventArgs> callback = null, bool leaveClientOpen = true)
        {
            Throw.IfNull(client, nameof(client));

            if (_bufferBlock != null)
            {
                if (client == Client)
                    throw new InvalidOperationException($"{nameof(OrderBookCache)} is already linked to this {nameof(IDepthWebSocketClient)}.");

                throw new InvalidOperationException($"{nameof(OrderBookCache)} is linked to another {nameof(IDepthWebSocketClient)}.");
            }

            _callback = callback;
            _leaveClientOpen = leaveClientOpen;

            _bufferBlock = new BufferBlock<DepthUpdateEventArgs>(new DataflowBlockOptions()
            {
                EnsureOrdered = true,
                CancellationToken = _token,
                BoundedCapacity = DataflowBlockOptions.Unbounded,
                MaxMessagesPerTask = DataflowBlockOptions.Unbounded,
            });

            _actionBlock = new ActionBlock<DepthUpdateEventArgs>(async @event =>
            {
                try
                {
                    // If order book has not been initialized.
                    if (_orderBook == null)
                    {
                        _orderBook = await _api.GetOrderBookAsync(_symbol, token: _token)
                            .ConfigureAwait(false);
                    }

                    // If there is a gap in events (order book out-of-sync).
                    if (@event.FirstUpdateId > _orderBook.LastUpdateId + 1)
                    {
                        _logger?.LogError($"{nameof(OrderBookCache)}: Synchronization failure (first update ID > last update ID + 1).");

                        await Task.Delay(1000, _token)
                            .ConfigureAwait(false); // wait a bit.

                        // Re-synchronize.
                        _orderBook = await _api.GetOrderBookAsync(_symbol, token: _token)
                            .ConfigureAwait(false);

                        // If still out-of-sync.
                        if (@event.FirstUpdateId > _orderBook.LastUpdateId + 1)
                        {
                            _logger?.LogError($"{nameof(OrderBookCache)}: Re-Synchronization failure (first update ID > last update ID + 1).");

                            // Reset and wait for next event.
                            _orderBook = null;
                            return;
                        }
                    }

                    Modify(@event.LastUpdateId, @event.Bids, @event.Asks, _limit);
                }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    _logger?.LogError(e, $"{nameof(OrderBookCache)}: \"{e.Message}\"");
                }
            }, new ExecutionDataflowBlockOptions()
            {
                BoundedCapacity = 1,
                EnsureOrdered = true,
                MaxDegreeOfParallelism = 1,
                CancellationToken = _token,
                SingleProducerConstrained = true,
            });

            _bufferBlock.LinkTo(_actionBlock);

            Client.DepthUpdate += OnDepthUpdate;
        }

        #endregion Public Methods

        #region Protected Methods

        /// <summary>
        /// Raise order book cache update event.
        /// </summary>
        /// <param name="args"></param>
        protected virtual void RaiseUpdateEvent(OrderBookCacheEventArgs args)
        {
            Throw.IfNull(args, nameof(args));

            try { Update?.Invoke(this, args); }
            catch (Exception e)
            {
                LogException(e, $"{nameof(DepthWebSocketClient)}.{nameof(RaiseUpdateEvent)}");
                throw;
            }
        }

        /// <summary>
        /// Update the order book.
        /// </summary>
        /// <param name="lastUpdateId"></param>
        /// <param name="bids"></param>
        /// <param name="asks"></param>
        /// <param name="limit"></param>
        protected virtual void Modify(long lastUpdateId, IEnumerable<(decimal, decimal)> bids, IEnumerable<(decimal, decimal)> asks, int limit)
        {
            if (lastUpdateId < _orderBook.LastUpdateId)
                return;

            _orderBook.Modify(lastUpdateId, bids, asks);

            _orderBookClone = limit > 0 ? _orderBook.Clone(limit) : _orderBook.Clone();

            var eventArgs = new OrderBookCacheEventArgs(_orderBookClone);

            _callback?.Invoke(eventArgs);
            RaiseUpdateEvent(eventArgs);
        }

        #endregion Protected Methods

        #region Private Methods

        /// <summary>
        /// <see cref="IDepthWebSocketClient"/> event handler.
        /// </summary>
        /// <param name="sender">The <see cref="IDepthWebSocketClient"/>.</param>
        /// <param name="event">The event arguments.</param>
        private void OnDepthUpdate(object sender, DepthUpdateEventArgs @event)
        {
            // Post event to buffer block (queue).
            _bufferBlock.Post(@event);
        }

        /// <summary>
        /// Log an exception if not already logged within this library.
        /// </summary>
        /// <param name="e"></param>
        /// <param name="source"></param>
        private void LogException(Exception e, string source)
        {
            if (e.IsLogged()) return;
            _logger?.LogError(e, $"{source}: \"{e.Message}\"");
            e.Logged();
        }

        #endregion Private Methods

        #region IDisposable

        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                Client.DepthUpdate -= OnDepthUpdate;

                if (!_leaveClientOpen)
                {
                    Client.Dispose();
                }

                _bufferBlock?.Complete();
                _actionBlock?.Complete();
            }

            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
        }

        #endregion IDisposable
    }
}
