// =============================================================================
// HeldStatusPersist
//
// Write-behind for receipts that land on a message not in memory. PendingMessageState
// holds them in RAM so the next load applies them, but a process exit loses them and
// the bubble stays on one tick forever. Writing per receipt is too much for a 512 MB
// phone, so receipts are coalesced per message id and written in one transaction a
// short while later, or at once on suspend.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class HeldStatusPersist
    {
        public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(2);

        private readonly object _gate = new object();
        private readonly Dictionary<string, MessageSendState> _pending =
            new Dictionary<string, MessageSendState>(StringComparer.Ordinal);
        private readonly Func<IReadOnlyList<KeyValuePair<string, MessageSendState>>, Task<int>> _write;
        private readonly Func<TimeSpan, Task> _delay;
        private readonly TimeSpan _flushAfter;
        private bool _scheduled;

        public HeldStatusPersist(
            Func<IReadOnlyList<KeyValuePair<string, MessageSendState>>, Task<int>> write,
            TimeSpan? flushAfter = null,
            Func<TimeSpan, Task> delay = null)
        {
            _write = write ?? throw new ArgumentNullException(nameof(write));
            _flushAfter = flushAfter ?? DefaultDelay;
            _delay = delay ?? Task.Delay;
        }

        /// <summary>
        /// Queues a receipt status. Only sent / delivered / read are kept; pending and failed
        /// describe our own send, which the send path persists itself.
        /// </summary>
        public void Hold(string messageId, string status)
        {
            if (string.IsNullOrEmpty(messageId) || !TryMap(status, out MessageSendState state))
            {
                return;
            }

            lock (_gate)
            {
                if (!_pending.TryGetValue(messageId, out MessageSendState current) || state > current)
                {
                    _pending[messageId] = state;
                }

                if (_scheduled)
                {
                    return;
                }

                _scheduled = true;
            }

            _ = FlushLaterAsync();
        }

        public async Task FlushAsync()
        {
            List<KeyValuePair<string, MessageSendState>> batch;
            lock (_gate)
            {
                _scheduled = false;
                if (_pending.Count == 0)
                {
                    return;
                }

                batch = new List<KeyValuePair<string, MessageSendState>>(_pending);
                _pending.Clear();
            }

            try
            {
                int changed = await _write(batch).ConfigureAwait(false);
                Debug.WriteLine("[HeldStatusPersist] Wrote " + batch.Count + " receipt(s), rows changed=" + changed);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HeldStatusPersist] Write failed: " + ex.Message);
            }
        }

        private async Task FlushLaterAsync()
        {
            try
            {
                await _delay(_flushAfter).ConfigureAwait(false);
            }
            catch
            {
            }

            await FlushAsync().ConfigureAwait(false);
        }

        private static bool TryMap(string status, out MessageSendState state)
        {
            if (string.Equals(status, ChatMessage.StatusSent, StringComparison.OrdinalIgnoreCase))
            {
                state = MessageSendState.Sent;
                return true;
            }

            if (string.Equals(status, ChatMessage.StatusDelivered, StringComparison.OrdinalIgnoreCase))
            {
                state = MessageSendState.Delivered;
                return true;
            }

            if (string.Equals(status, ChatMessage.StatusRead, StringComparison.OrdinalIgnoreCase))
            {
                state = MessageSendState.Read;
                return true;
            }

            state = MessageSendState.NotApplicable;
            return false;
        }
    }
}
