// =============================================================================
// DecryptedEventCounter
//
// Always-on decrypted-event counter for the runtime snapshot. No message text
// or contact data. One instance per app (DI singleton).
// =============================================================================
using System;
using System.Threading;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class DecryptedEventCounter
    {
        private long _count;
        private long _lastUtcTicks;

        public long Count => Interlocked.Read(ref _count);

        public long LastUtcTicks => Interlocked.Read(ref _lastUtcTicks);

        public void Observe(DateTime utcNow)
        {
            Interlocked.Increment(ref _count);
            Interlocked.Exchange(ref _lastUtcTicks, utcNow.Ticks);
        }
    }
}
