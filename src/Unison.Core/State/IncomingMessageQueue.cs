// =============================================================================
// IncomingMessageQueue
//
// The two lanes the incoming pump drains, and the bookkeeping that lets a stalled
// or suspended pump be replaced. Live traffic is always served before offline
// replay. A generation number retires an old drain loop when a new one starts.
// One lock covers every field, including the pump task, so a drain cannot be
// observed half started.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Helpers;

namespace Unison.Core.State
{
    public struct IncomingQueueReset
    {
        public int Generation { get; set; }
        public int LiveDepth { get; set; }
        public int OfflineDepth { get; set; }
    }

    public struct IncomingQueueSnapshot
    {
        public int LiveDepth { get; set; }
        public int OfflineDepth { get; set; }
        public bool Running { get; set; }
        public int Generation { get; set; }
        public string Stage { get; set; }
        public string CurrentMessageId { get; set; }
        public long StageUtcTicks { get; set; }
    }

    public sealed class IncomingMessageQueue<T> where T : class
    {
        private readonly object _gate = new object();
        private readonly Queue<T> _live = new Queue<T>();
        private readonly Queue<T> _offline = new Queue<T>();
        private readonly HashSet<string> _timedOutIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Func<T, bool> _isOffline;
        private readonly Func<T, string> _idOf;
        private bool _running;
        private Task _pump = Task.CompletedTask;
        private int _generation;
        private T _current;
        private string _stage = "idle";
        private long _stageUtcTicks;

        public IncomingMessageQueue(Func<T, bool> isOffline, Func<T, string> idOf)
        {
            _isOffline = isOffline ?? throw new ArgumentNullException(nameof(isOffline));
            _idOf = idOf ?? throw new ArgumentNullException(nameof(idOf));
        }

        /// <summary>Read without the lock, as the diagnostics lines always did.</summary>
        public string Stage => _stage;

        public Task PumpTask
        {
            get
            {
                lock (_gate)
                {
                    return _pump ?? Task.CompletedTask;
                }
            }
        }

        public void Enqueue(T item)
        {
            if (item == null)
            {
                return;
            }

            lock (_gate)
            {
                LaneOf(item).Enqueue(item);
            }
        }

        /// <summary>
        /// Starts a drain when none is running and there is work. <paramref name="run"/> is
        /// called inside the lock with the generation the drain belongs to.
        /// </summary>
        public bool TryStart(Func<int, Task> run, DateTime nowUtc, out int generation)
        {
            lock (_gate)
            {
                generation = _generation;
                if (_running || (_live.Count == 0 && _offline.Count == 0))
                {
                    return false;
                }

                _running = true;
                _stage = "starting";
                _stageUtcTicks = nowUtc.Ticks;
                _pump = run(generation);
                return true;
            }
        }

        /// <summary>
        /// Hands the drain its next item, live first. False when this drain was retired or the
        /// lanes are empty; an empty lane also marks the pump idle.
        /// </summary>
        public bool TryTakeNext(int generation, DateTime nowUtc, out T next)
        {
            next = null;
            lock (_gate)
            {
                if (generation != _generation)
                {
                    return false;
                }

                if (_live.Count > 0)
                {
                    next = _live.Dequeue();
                }
                else if (_offline.Count > 0)
                {
                    next = _offline.Dequeue();
                }
                else
                {
                    _current = null;
                    _running = false;
                    _stage = "idle";
                    _stageUtcTicks = nowUtc.Ticks;
                    return false;
                }

                _current = next;
                _stage = "handle";
                _stageUtcTicks = nowUtc.Ticks;
                return true;
            }
        }

        /// <summary>A null item keeps the current one; a blank stage reads as unknown.</summary>
        public void SetStage(string stage, T item, DateTime nowUtc)
        {
            lock (_gate)
            {
                _stage = string.IsNullOrWhiteSpace(stage) ? "unknown" : stage;
                _current = item ?? _current;
                _stageUtcTicks = nowUtc.Ticks;
            }
        }

        /// <summary>Clears the current item when the drain that took it is still the live one.</summary>
        public void Finish(int generation, T item, DateTime nowUtc)
        {
            lock (_gate)
            {
                if (generation == _generation && ReferenceEquals(_current, item))
                {
                    _current = null;
                    _stage = "next";
                    _stageUtcTicks = nowUtc.Ticks;
                }
            }
        }

        /// <summary>
        /// Retires the running drain. The item it held goes back to the end of its lane when asked.
        /// </summary>
        public IncomingQueueReset Reset(string reason, bool requeueCurrent, DateTime nowUtc)
        {
            lock (_gate)
            {
                T current = _current;
                _generation++;
                if (requeueCurrent && current != null)
                {
                    LaneOf(current).Enqueue(current);
                }

                _current = null;
                _running = false;
                _pump = Task.CompletedTask;
                _stage = "reset:" + reason;
                _stageUtcTicks = nowUtc.Ticks;
                return new IncomingQueueReset
                {
                    Generation = _generation,
                    LiveDepth = _live.Count,
                    OfflineDepth = _offline.Count
                };
            }
        }

        /// <summary>
        /// A live item that timed out is retried once. The second time the same id blocks it is
        /// skipped. An item without an id is always retried.
        /// </summary>
        public bool ShouldRetryTimedOut(T item)
        {
            string id = item == null ? null : _idOf(item);
            if (string.IsNullOrWhiteSpace(id))
            {
                return true;
            }

            lock (_gate)
            {
                return _timedOutIds.Add(id);
            }
        }

        public void ForgetTimeout(T item)
        {
            string id = item == null ? null : _idOf(item);
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            lock (_gate)
            {
                _timedOutIds.Remove(id);
            }
        }

        public bool IsStalled(TimeSpan limit, DateTime nowUtc)
        {
            lock (_gate)
            {
                bool hasWork = _current != null || _live.Count > 0 || _offline.Count > 0;
                return IncomingPumpStall.IsStalled(_running, _stageUtcTicks, hasWork, nowUtc, limit);
            }
        }

        public IncomingQueueSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new IncomingQueueSnapshot
                {
                    LiveDepth = _live.Count,
                    OfflineDepth = _offline.Count,
                    Running = _running,
                    Generation = _generation,
                    Stage = _stage,
                    CurrentMessageId = _current == null ? null : _idOf(_current),
                    StageUtcTicks = _stageUtcTicks
                };
            }
        }

        private Queue<T> LaneOf(T item) => _isOffline(item) ? _offline : _live;
    }
}
