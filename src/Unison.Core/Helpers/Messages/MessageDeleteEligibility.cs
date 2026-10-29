// =============================================================================
// MessageDeleteEligibility
//
// WhatsApp delete rules (FAQ "Como apagar mensagens"):
// - Delete for me: any sent or received bubble on this account (others keep their copy).
// - Delete for everyone: within ~2 days of send; own messages always; others only if
//   the viewer is a group admin. Replaces the bubble with a revoked tombstone.
//
// https://faq.whatsapp.com/1370476507114859/?locale=pt_BR&cms_platform=android
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Pure eligibility for delete-for-me / delete-for-everyone. Clock and admin flags
    /// are passed in so the rules stay testable.
    /// </summary>
    public static class MessageDeleteEligibility
    {
        /// <summary>
        /// FAQ: "cerca de dois dias". Product window is 2 days + 12 hours (60h).
        /// </summary>
        public static readonly TimeSpan DeleteForEveryoneWindow = TimeSpan.FromHours(60);

        /// <summary>
        /// Whether this account may remove its local copy (sent or received).
        /// </summary>
        public static bool CanDeleteForMe(ChatMessage message)
        {
            if (message == null ||
                string.IsNullOrWhiteSpace(message.Id) ||
                message.IsRevoked)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Whether this account may request a revoke for everyone.
        /// </summary>
        /// <param name="isGroupChat">True when the open chat is a group (or community announce).</param>
        /// <param name="viewerIsGroupAdmin">True when the current user is admin/super-admin of that group.</param>
        /// <param name="utcNow">Injected clock for the two-day window.</param>
        public static bool CanDeleteForEveryone(
            ChatMessage message,
            bool isGroupChat,
            bool viewerIsGroupAdmin,
            DateTime utcNow)
        {
            if (!CanDeleteForMe(message))
            {
                return false;
            }

            if (!IsWithinDeleteForEveryoneWindow(message.Timestamp, utcNow))
            {
                return false;
            }

            if (message.IsFromMe)
            {
                return true;
            }

            // Only group admins may revoke someone else's message, and only in a group.
            return isGroupChat && viewerIsGroupAdmin;
        }

        /// <summary>
        /// Whether <paramref name="messageTimestamp"/> still falls inside the delete-for-everyone window.
        /// </summary>
        public static bool IsWithinDeleteForEveryoneWindow(DateTime messageTimestamp, DateTime utcNow)
        {
            if (messageTimestamp == DateTime.MinValue)
            {
                return false;
            }

            DateTime sent = ChatMessageOrder.ToComparableUtc(messageTimestamp);
            DateTime now = ChatMessageOrder.ToComparableUtc(utcNow);
            if (sent > now)
            {
                // Clock skew: treat as still deletable while inside future tolerance of the window.
                return (sent - now) <= DeleteForEveryoneWindow;
            }

            return (now - sent) <= DeleteForEveryoneWindow;
        }
    }
}
