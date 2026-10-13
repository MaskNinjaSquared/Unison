// =============================================================================
// MessageStatusProgression
//
// Which delivery status is allowed to replace which. This is what decides the
// ticks on an outgoing message, and it is a ratchet rather than an assignment:
// status reports arrive out of order -- a delivery receipt can land after the
// read receipt it preceded -- so simply taking the latest one would flicker the
// ticks backwards in front of the user.
//
// The one exception worth understanding is failure. A late error cannot undo
// evidence that the message already arrived: once something is delivered or
// read, the recipient has it, whatever a straggling error says afterwards.
// Before that point, failure is believed.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class MessageStatusProgression
    {
        /// <summary>
        /// Position on the delivery ladder. Failure sits below the ladder rather than on
        /// it, so any real progress outranks it.
        /// </summary>
        /// <remarks>
        /// An unrecognised status ranks as <see cref="ChatMessage.StatusPending"/>, which
        /// makes it the weakest real state: it can be replaced by anything and displaces
        /// nothing.
        /// </remarks>
        public static int Rank(string status)
        {
            switch ((status ?? string.Empty).ToLowerInvariant())
            {
                case ChatMessage.StatusPending: return 0;
                case ChatMessage.StatusSent: return 1;
                case ChatMessage.StatusDelivered: return 2;
                case ChatMessage.StatusRead: return 3;
                case ChatMessage.StatusFailed: return -1;
                default: return 0;
            }
        }

        /// <summary>
        /// Whether <paramref name="incoming"/> should replace <paramref name="current"/>.
        /// </summary>
        public static bool ShouldApply(string current, string incoming)
        {
            if (string.IsNullOrWhiteSpace(incoming))
            {
                return false;
            }

            if (string.Equals(current, incoming, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(incoming, ChatMessage.StatusFailed, StringComparison.OrdinalIgnoreCase))
            {
                // A late error cannot undo proof that the recipient already received or
                // read it.
                return Rank(current) < Rank(ChatMessage.StatusDelivered);
            }

            return Rank(incoming) > Rank(current);
        }
    }
}
