// =============================================================================
// WaEventBuffer
//
// The default IWaEventBus. While buffering is off it dispatches each event as it
// arrives; while on, it accumulates events per kind through their registered
// merger and releases everything as one batch on flush.
//
// Two safety nets exist because a stuck buffer would silently stop the app from
// seeing anything: a 30s timeout auto-flushes, and buffering is reference
// counted so nested scopes cannot flush a parent's work early.
//
// Ports: rc14 makeEventBuffer in src/Utils/event-buffer.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Socket.Abstractions;

namespace Unison.Socket.Events
{
    /// <summary>
    /// Consolidates events into batches. Differs from rc14 in two deliberate ways: emitting is
    /// awaitable (so a caller knows when handlers finished, which the replay drain depends on)
    /// and handlers run one at a time in registration order.
    /// </summary>
    public sealed class WaEventBuffer : IWaEventBus, IDisposable
    {
        private static readonly TimeSpan BufferTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan FlushDebounce = TimeSpan.FromMilliseconds(100);

        /// <summary>rc14 BUFFERABLE_EVENT.</summary>
        private static readonly HashSet<WaEventKind> Bufferable = new HashSet<WaEventKind>
        {
            WaEventKind.MessagingHistorySet,
            WaEventKind.ChatsUpsert,
            WaEventKind.ChatsUpdate,
            WaEventKind.ChatsDelete,
            WaEventKind.ContactsUpsert,
            WaEventKind.ContactsUpdate,
            WaEventKind.MessagesUpsert,
            WaEventKind.MessagesUpdate,
            WaEventKind.MessagesDelete,
            WaEventKind.MessagesReaction,
            WaEventKind.MessageReceiptUpdate,
            WaEventKind.GroupsUpdate
        };

        private readonly object _gate = new object();
        private readonly ISocketLog _log;
        private readonly Dictionary<WaEventKind, IWaEventMerger> _mergers = new Dictionary<WaEventKind, IWaEventMerger>();
        private readonly Dictionary<WaEventKind, object> _pending = new Dictionary<WaEventKind, object>();
        private readonly List<Func<WaEventBatch, Task>> _handlers = new List<Func<WaEventBatch, Task>>();
        private readonly SemaphoreSlim _dispatchGate = new SemaphoreSlim(1, 1);

        private Timer _bufferTimer;
        private Timer _flushTimer;
        private bool _isBuffering;
        private int _bufferCount;
        private bool _disposed;

        public WaEventBuffer(ISocketLog log = null)
        {
            _log = log ?? NullSocketLog.Instance;
        }

        public bool IsBuffering
        {
            get
            {
                lock (_gate)
                {
                    return _isBuffering;
                }
            }
        }

        public void RegisterMerger(IWaEventMerger merger)
        {
            if (merger == null)
            {
                throw new ArgumentNullException(nameof(merger));
            }

            if (!Bufferable.Contains(merger.Kind))
            {
                throw new ArgumentException($"{merger.Kind} is not a bufferable event", nameof(merger));
            }

            lock (_gate)
            {
                _mergers[merger.Kind] = merger;
            }
        }

        public async Task<bool> EmitAsync(WaEventKind kind, object payload)
        {
            ThrowIfDisposed();

            lock (_gate)
            {
                if (_isBuffering && _mergers.ContainsKey(kind) && Bufferable.Contains(kind))
                {
                    Append(kind, payload);
                    return true;
                }
            }

            await DispatchAsync(WaEventBatch.Single(kind, payload)).ConfigureAwait(false);
            return false;
        }

        public IDisposable Process(Func<WaEventBatch, Task> handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            ThrowIfDisposed();

            lock (_gate)
            {
                _handlers.Add(handler);
            }

            return new Subscription(this, handler);
        }

        public void Buffer()
        {
            ThrowIfDisposed();

            lock (_gate)
            {
                if (!_isBuffering)
                {
                    _log.Debug("Event buffer activated");
                    _isBuffering = true;
                    _bufferCount = 0;
                    StartBufferTimeout();
                }

                _bufferCount++;
            }
        }

        public async Task<bool> FlushAsync()
        {
            WaEventBatch batch = null;

            lock (_gate)
            {
                if (!_isBuffering)
                {
                    return false;
                }

                _log.Debug($"Flushing event buffer (bufferCount={_bufferCount}, kinds={_pending.Count})");
                _isBuffering = false;
                _bufferCount = 0;
                StopTimers();

                if (_pending.Count > 0)
                {
                    batch = new WaEventBatch(new Dictionary<WaEventKind, object>(_pending));
                    _pending.Clear();
                }
            }

            if (batch != null)
            {
                await DispatchAsync(batch).ConfigureAwait(false);
            }

            return true;
        }

        public async Task RunBufferedAsync(Func<Task> work)
        {
            if (work == null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            await RunBufferedAsync<object>(async () =>
            {
                await work().ConfigureAwait(false);
                return null;
            }).ConfigureAwait(false);
        }

        public async Task<T> RunBufferedAsync<T>(Func<Task<T>> work)
        {
            if (work == null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            Buffer();
            try
            {
                return await work().ConfigureAwait(false);
            }
            finally
            {
                int remaining;
                lock (_gate)
                {
                    _bufferCount = Math.Max(0, _bufferCount - 1);
                    remaining = _bufferCount;
                }

                // The outermost caller schedules the flush; nested ones just decrement.
                if (remaining == 0)
                {
                    ScheduleDebouncedFlush();
                }
            }
        }

        public void Destroy()
        {
            lock (_gate)
            {
                StopTimers();
                _pending.Clear();
                _handlers.Clear();
                _isBuffering = false;
                _bufferCount = 0;
            }

            _log.Debug("Event buffer destroyed");
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Destroy();
            _dispatchGate.Dispose();
        }

        /// <summary>Caller must hold <see cref="_gate"/>.</summary>
        private void Append(WaEventKind kind, object payload)
        {
            var merger = _mergers[kind];

            object existing;
            _pending.TryGetValue(kind, out existing);

            try
            {
                _pending[kind] = merger.Merge(existing, payload);
            }
            catch (Exception ex)
            {
                // Losing the merge must not lose the event: keep the newest payload.
                _log.Error($"Merger for {kind} failed; keeping latest payload", ex);
                _pending[kind] = payload;
            }
        }

        private async Task DispatchAsync(WaEventBatch batch)
        {
            Func<WaEventBatch, Task>[] handlers;
            lock (_gate)
            {
                if (_handlers.Count == 0)
                {
                    return;
                }

                handlers = _handlers.ToArray();
            }

            await _dispatchGate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var handler in handlers)
                {
                    try
                    {
                        await handler(batch).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // One bad subscriber must not stop the others.
                        _log.Error("Event handler threw", ex);
                    }
                }
            }
            finally
            {
                _dispatchGate.Release();
            }
        }

        /// <summary>Caller must hold <see cref="_gate"/>.</summary>
        private void StartBufferTimeout()
        {
            if (_bufferTimer == null)
            {
                _bufferTimer = new Timer(OnBufferTimeout, null, BufferTimeout, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _bufferTimer.Change(BufferTimeout, Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>Caller must hold <see cref="_gate"/>.</summary>
        private void StopTimers()
        {
            if (_bufferTimer != null)
            {
                _bufferTimer.Dispose();
                _bufferTimer = null;
            }

            if (_flushTimer != null)
            {
                _flushTimer.Dispose();
                _flushTimer = null;
            }
        }

        private void ScheduleDebouncedFlush()
        {
            lock (_gate)
            {
                if (_disposed || !_isBuffering || _flushTimer != null)
                {
                    return;
                }

                _flushTimer = new Timer(OnDebouncedFlush, null, FlushDebounce, Timeout.InfiniteTimeSpan);
            }
        }

        private void OnBufferTimeout(object state)
        {
            _log.Warn("Buffer timeout reached, auto-flushing");
            FireAndForget(FlushAsync(), "auto-flush after buffer timeout");
        }

        private void OnDebouncedFlush(object state)
        {
            lock (_gate)
            {
                if (_flushTimer != null)
                {
                    _flushTimer.Dispose();
                    _flushTimer = null;
                }

                if (_bufferCount != 0)
                {
                    return;
                }
            }

            FireAndForget(FlushAsync(), "debounced flush");
        }

        private void FireAndForget(Task task, string what)
        {
            task.ContinueWith(
                t => _log.Error($"{what} failed", t.Exception),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        private void Unsubscribe(Func<WaEventBatch, Task> handler)
        {
            lock (_gate)
            {
                _handlers.Remove(handler);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(WaEventBuffer));
            }
        }

        private sealed class Subscription : IDisposable
        {
            private readonly WaEventBuffer _owner;
            private Func<WaEventBatch, Task> _handler;

            public Subscription(WaEventBuffer owner, Func<WaEventBatch, Task> handler)
            {
                _owner = owner;
                _handler = handler;
            }

            public void Dispose()
            {
                var handler = Interlocked.Exchange(ref _handler, null);
                if (handler != null)
                {
                    _owner.Unsubscribe(handler);
                }
            }
        }
    }
}
