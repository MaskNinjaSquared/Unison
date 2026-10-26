// =============================================================================
// ChatListColdPopulateGate
//
// Frugal Mobile: VisibleChats empty → store (~250 rows). While Continues Insert
// that catalogue, tip-swapped slices must not also walk 259 previews on the same
// dispatcher (cold fill + tip-swapped was the remaining list hitch after names).
// =============================================================================
using System.Threading;

namespace Unison.Core.State
{
    public sealed class ChatListColdPopulateGate
    {
        private int _held;

        public bool IsHeld => Volatile.Read(ref _held) != 0;

        public void Hold() => Interlocked.Exchange(ref _held, 1);

        public void Release() => Interlocked.Exchange(ref _held, 0);
    }
}
