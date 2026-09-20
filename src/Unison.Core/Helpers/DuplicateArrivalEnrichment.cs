// =============================================================================
// DuplicateArrivalEnrichment
//
// What a second delivery of the same message is allowed to improve on the row
// we already have: a better delivery tick, a participant we did not know, a
// sender name that was blank. Written once so the offline fast-path and the
// full duplicate path cannot drift apart — the fast-path used to resolve the
// missing-message ledger and return without touching the row at all.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Fields a duplicate arrival may write onto an existing <see cref="ChatMessage"/>.
    /// Null on a field means leave the existing value alone.
    /// </summary>
    public struct DuplicateArrivalPatch
    {
        public string Status;
        public string ParticipantJid;
        public string SenderName;

        public bool Changed =>
            Status != null || ParticipantJid != null || SenderName != null;
    }

    public static class DuplicateArrivalEnrichment
    {
        /// <summary>
        /// Computes the patch a duplicate should apply. Does not mutate anything.
        /// </summary>
        /// <param name="incomingIsFromMe">
        /// Status only advances on our own messages — receipts for someone else's
        /// message do not rewrite their ticks.
        /// </param>
        public static DuplicateArrivalPatch Compute(
            string existingStatus,
            string incomingStatus,
            bool incomingIsFromMe,
            string existingParticipantJid,
            string incomingParticipantJid,
            string existingSenderName,
            string incomingSenderName)
        {
            var patch = new DuplicateArrivalPatch();

            if (incomingIsFromMe &&
                MessageStatusProgression.ShouldApply(existingStatus, incomingStatus))
            {
                patch.Status = incomingStatus;
            }

            if (string.IsNullOrWhiteSpace(existingParticipantJid) &&
                !string.IsNullOrWhiteSpace(incomingParticipantJid))
            {
                patch.ParticipantJid = incomingParticipantJid.Trim();
            }

            if (string.IsNullOrWhiteSpace(existingSenderName) &&
                !string.IsNullOrWhiteSpace(incomingSenderName))
            {
                patch.SenderName = incomingSenderName.Trim();
            }

            return patch;
        }

        /// <summary>
        /// Writes a non-empty patch onto <paramref name="existing"/>. Returns whether anything changed.
        /// </summary>
        public static bool Apply(ChatMessage existing, DuplicateArrivalPatch patch)
        {
            if (existing == null || !patch.Changed)
            {
                return false;
            }

            bool changed = false;
            if (patch.Status != null &&
                !string.Equals(existing.Status, patch.Status, StringComparison.Ordinal))
            {
                existing.Status = patch.Status;
                changed = true;
            }

            if (patch.ParticipantJid != null &&
                !string.Equals(existing.ParticipantJid, patch.ParticipantJid, StringComparison.OrdinalIgnoreCase))
            {
                existing.ParticipantJid = patch.ParticipantJid;
                changed = true;
            }

            if (patch.SenderName != null &&
                !string.Equals(existing.SenderName, patch.SenderName, StringComparison.Ordinal))
            {
                existing.SenderName = patch.SenderName;
                changed = true;
            }

            return changed;
        }
    }
}
