// =============================================================================
// PendingMessageState
//
// Receipts and pins that arrived for a message not in memory — older than the
// open page, or in a chat never opened. Touching disk on every receipt is too
// much for a 512 MB phone, so the change is held here and applied when the
// message is next loaded. A held change applies once and is then dropped, even
// when the message already carried it.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.State
{
    public struct PendingPin
    {
        public bool IsPinned;

        /// <summary>Null when unpinned.</summary>
        public DateTime? PinnedAtUtc;

        /// <summary>Null when unpinned or when the pin does not expire.</summary>
        public DateTime? ExpiresAtUtc;
    }

    public sealed class PendingMessageState
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, string> _statusByMessageId =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, PendingPin>> _pinsByChat =
            new Dictionary<string, Dictionary<string, PendingPin>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Holds the status unless a more advanced one is already held.</summary>
        public void HoldStatus(string messageId, string status)
        {
            if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(status))
            {
                return;
            }

            lock (_gate)
            {
                if (!_statusByMessageId.TryGetValue(messageId, out string current) ||
                    MessageStatusProgression.ShouldApply(current, status))
                {
                    _statusByMessageId[messageId] = status;
                }
            }
        }

        public void ForgetStatus(string messageId)
        {
            if (string.IsNullOrWhiteSpace(messageId))
            {
                return;
            }

            lock (_gate)
            {
                _statusByMessageId.Remove(messageId);
            }
        }

        /// <summary>Holds a pin or unpin; a pin with no time is stamped <paramref name="nowUtc"/>.</summary>
        /// <returns>The pin as held, times already cleared for an unpin.</returns>
        public PendingPin HoldPin(
            string canonicalChatJid,
            string messageId,
            bool isPinned,
            DateTime? pinnedAtUtc,
            DateTime? expiresAtUtc,
            DateTime nowUtc)
        {
            var pin = new PendingPin
            {
                IsPinned = isPinned,
                PinnedAtUtc = isPinned ? pinnedAtUtc ?? nowUtc : (DateTime?)null,
                ExpiresAtUtc = isPinned ? expiresAtUtc : null
            };

            lock (_gate)
            {
                if (!_pinsByChat.TryGetValue(canonicalChatJid, out var byMessageId))
                {
                    byMessageId = new Dictionary<string, PendingPin>(StringComparer.Ordinal);
                    _pinsByChat[canonicalChatJid] = byMessageId;
                }

                byMessageId[messageId] = pin;
            }

            return pin;
        }

        public void ForgetPin(string canonicalChatJid, string messageId)
        {
            lock (_gate)
            {
                RemovePinLocked(canonicalChatJid, messageId);
            }
        }

        /// <param name="statusPolicy">Shapes a held status for this chat before it is compared.</param>
        /// <returns>True when the message changed.</returns>
        public bool ApplyTo(ChatMessage message, string canonicalChatJid, Func<string, string> statusPolicy)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.Id))
            {
                return false;
            }

            bool changed = false;
            lock (_gate)
            {
                if (message.IsFromMe && _statusByMessageId.TryGetValue(message.Id, out string held))
                {
                    string status = statusPolicy(held);
                    if (MessageStatusProgression.ShouldApply(message.Status, status))
                    {
                        message.Status = status;
                        changed = true;
                    }

                    _statusByMessageId.Remove(message.Id);
                }

                if (!string.IsNullOrWhiteSpace(canonicalChatJid) &&
                    _pinsByChat.TryGetValue(canonicalChatJid, out var byMessageId) &&
                    byMessageId.TryGetValue(message.Id, out PendingPin pin))
                {
                    if (message.IsPinned != pin.IsPinned ||
                        message.PinnedAtUtc != pin.PinnedAtUtc ||
                        message.PinExpiresAtUtc != pin.ExpiresAtUtc)
                    {
                        message.IsPinned = pin.IsPinned;
                        message.PinnedAtUtc = pin.PinnedAtUtc;
                        message.PinExpiresAtUtc = pin.ExpiresAtUtc;
                        changed = true;
                    }

                    RemovePinLocked(canonicalChatJid, message.Id);
                }
            }

            return changed;
        }

        private void RemovePinLocked(string canonicalChatJid, string messageId)
        {
            if (string.IsNullOrWhiteSpace(canonicalChatJid) ||
                !_pinsByChat.TryGetValue(canonicalChatJid, out var byMessageId))
            {
                return;
            }

            byMessageId.Remove(messageId);
            if (byMessageId.Count == 0)
            {
                _pinsByChat.Remove(canonicalChatJid);
            }
        }
    }
}
