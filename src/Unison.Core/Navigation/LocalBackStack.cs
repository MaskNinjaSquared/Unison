using System;
using System.Collections.Generic;

namespace Unison.Core.Navigation
{
    /// <summary>
    /// LIFO stack of virtual back handlers for overlays / nested chrome that are not Frame pages.
    /// Shell consumes <see cref="TryHandle"/> before Frame or section back so hardware / title-bar
    /// Back cannot skip an open picker or create step.
    /// </summary>
    public sealed class LocalBackStack
    {
        private readonly object _gate = new object();
        private readonly List<Entry> _entries = new List<Entry>();
        private int _nextId = 1;

        /// <summary>True when at least one handler is registered.</summary>
        public bool HasHandlers
        {
            get
            {
                lock (_gate)
                {
                    return _entries.Count > 0;
                }
            }
        }

        /// <summary>Raised after push or dispose changes the stack.</summary>
        public event EventHandler Changed;

        /// <summary>
        /// Registers <paramref name="handler"/> on top of the stack. Dispose to unregister
        /// (safe if already disposed). Handler returns true when it consumed Back.
        /// </summary>
        public IDisposable Push(Func<bool> handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            var entry = new Entry(this, handler);
            lock (_gate)
            {
                entry.Id = _nextId++;
                _entries.Add(entry);
            }

            RaiseChanged();
            return entry;
        }

        /// <summary>
        /// Invokes the topmost handler only. Returns true when a handler consumed Back.
        /// Empty stack → false (shell continues with Frame / section back).
        /// </summary>
        public bool TryHandle()
        {
            Func<bool> handler;
            lock (_gate)
            {
                if (_entries.Count == 0)
                {
                    return false;
                }

                handler = _entries[_entries.Count - 1].Handler;
            }

            return handler != null && handler();
        }

        private void Remove(Entry entry)
        {
            bool removed;
            lock (_gate)
            {
                removed = _entries.Remove(entry);
            }

            if (removed)
            {
                RaiseChanged();
            }
        }

        private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        private sealed class Entry : IDisposable
        {
            private readonly LocalBackStack _owner;
            private bool _disposed;

            public Entry(LocalBackStack owner, Func<bool> handler)
            {
                _owner = owner;
                Handler = handler;
            }

            public int Id { get; set; }

            public Func<bool> Handler { get; }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _owner.Remove(this);
            }
        }
    }
}
