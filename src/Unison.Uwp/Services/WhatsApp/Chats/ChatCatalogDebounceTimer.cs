// =============================================================================
// ChatCatalogDebounceTimer
//
// Owns the debounce Timer field for catalogue persist. WhatsAppService only
// arms / disposes through this helper — Persistence.cs no longer holds the
// Timer itself.
// =============================================================================
using System;
using System.Threading;

namespace Unison.Uwp.Services.WhatsApp.Chats
{
    internal sealed class ChatCatalogDebounceTimer : IDisposable
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
