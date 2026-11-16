// =============================================================================
// ChatCatalogDebounceTimer
//
// Owns the debounce Timer field for catalogue persist. ChatCatalogPersistenceHost
// only arms / disposes through this helper.
// =============================================================================
using System;
using System.Threading;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class ChatCatalogDebounceTimer : IDisposable
    {
        private readonly object _gate = new object();
        private Timer _timer;

        public void Arm(int debounceMilliseconds, Action onElapsed)
        {
            if (onElapsed == null)
            {
                throw new ArgumentNullException(nameof(onElapsed));
            }

            lock (_gate)
            {
                _timer?.Dispose();
                _timer = new Timer(
                    _ => onElapsed(),
                    null,
                    debounceMilliseconds,
                    Timeout.Infinite);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }
    }
}
