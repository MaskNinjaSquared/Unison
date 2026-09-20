// =============================================================================
// TransientChatMerge
//
// What survives when the same contact turns out to occupy two rows.
//
// A conversation can be opened against an address before we know who is behind
// it -- a LID arrives, a row is created, and only later does a usync or an
// app-state alias reveal it is a contact already in the list under their phone
// number. At that point the two rows have to become one, and the question is
// which side of each field wins.
//
// The rule is not "the canonical row wins". The transient row is often the one
// the user has actually been reading, so its preview, its unread count and the
// name it learned are usually the newer facts. But it is also the row with the
// weaker identity, so it must not overwrite something the canonical row already
// knows. Every field below resolves that tension separately.
//
// Losing here is silent and permanent: the surviving row is what the user sees
// and what gets persisted, and the other one is gone.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class TransientChatMerge
    {
        /// <summary>
        /// Folds the transient row's facts into the canonical one. The caller removes the
        /// transient row afterwards.
        /// </summary>
        /// <param name="canonicalJid">
        /// Both addresses are needed because a row whose name is just its own number has
        /// no name at all, and which number counts differs per row.
        /// </param>
        public static void Apply(
            ChatItem canonical,
            ChatItem transient,
            string canonicalJid,
            string transientJid,
            ISelfMarkerNaming selfMarkers)
        {
            if (canonical == null || transient == null || ReferenceEquals(canonical, transient))
            {
                return;
            }

            MergePreview(canonical, transient);

            // The higher count wins rather than the sum: both rows were counting the same
            // conversation, so adding them would double-count every message that arrived
            // while the alias was still unknown.
            if (canonical.UnreadCount < transient.UnreadCount)
            {
                canonical.UnreadCount = transient.UnreadCount;
            }

            MergeAvatar(canonical, transient);
            MergeName(canonical, transient, canonicalJid, transientJid, selfMarkers);
        }

        /// <summary>
        /// The transient preview replaces the canonical one when it is newer, or when the
        /// canonical row has no preview at all to show.
        /// </summary>
        /// <remarks>
        /// The fields move together. Half a preview -- new text against an old author, or a
        /// new kind against an old timestamp -- renders as a message that was never sent.
        /// </remarks>
        private static void MergePreview(ChatItem canonical, ChatItem transient)
        {
            if (string.IsNullOrWhiteSpace(transient.LastMessage))
            {
                return;
            }

            DateTime canonicalAt = ComparableOrMin(canonical.LastMessageTimestampUtc);
            DateTime transientAt = ComparableOrMin(transient.LastMessageTimestampUtc);

            bool transientIsNewer = transientAt > canonicalAt;
            bool canonicalHasNothingToShow = string.IsNullOrWhiteSpace(canonical.LastMessage);
            if (!transientIsNewer && !canonicalHasNothingToShow)
            {
                return;
            }

            canonical.LastMessage = transient.LastMessage;
            canonical.LastMessageKind = transient.LastMessageKind;
            canonical.LastPreview.CopyFrom(transient.LastPreview);
            canonical.Timestamp = transient.Timestamp;
            canonical.LastMessageTimestampUtc = transient.LastMessageTimestampUtc;

            // The id names which message the row is showing, so leaving the old one behind
            // makes the row disagree with itself. Nothing corrects that afterwards:
            // ChatPreviewTip only fills the id in when it is missing, never when it is wrong.
            canonical.LastMessageId = transient.LastMessageId;
        }

        /// <summary>
        /// The transient picture is only taken when the canonical row has none at all.
        /// </summary>
        /// <remarks>
        /// Unlike the preview, a newer avatar is not a better one: the canonical row's
        /// picture was fetched against the identity we are keeping. The failure stamps
        /// travel with the urls so a copied-in avatar does not inherit a retry history that
        /// belongs to a different fetch.
        ///
        /// The two resolutions are one fact, not two fields. They fall back to each other
        /// for display, so a row holding the preview of one identity and the full-size
        /// picture of another shows a different face depending on which surface is asking —
        /// which is why the gap is tested on both urls and both are copied together.
        /// </remarks>
        private static void MergeAvatar(ChatItem canonical, ChatItem transient)
        {
            bool canonicalHasAPicture =
                !string.IsNullOrWhiteSpace(canonical.AvatarUrl) ||
                !string.IsNullOrWhiteSpace(canonical.AvatarHighUrl);

            bool transientHasAPicture =
                !string.IsNullOrWhiteSpace(transient.AvatarUrl) ||
                !string.IsNullOrWhiteSpace(transient.AvatarHighUrl);

            if (canonicalHasAPicture || !transientHasAPicture)
            {
                return;
            }

            canonical.AvatarUrl = transient.AvatarUrl;
            canonical.AvatarHighUrl = transient.AvatarHighUrl;
            canonical.AvatarFetchedAtUtc = transient.AvatarFetchedAtUtc;
            canonical.AvatarFetchFailedAtUtc = transient.AvatarFetchFailedAtUtc;
            canonical.AvatarFetchFailureReason = transient.AvatarFetchFailureReason;
        }

        /// <summary>
        /// The transient name is taken only when the canonical row is not really named and
        /// the transient one is.
        /// </summary>
        private static void MergeName(
            ChatItem canonical,
            ChatItem transient,
            string canonicalJid,
            string transientJid,
            ISelfMarkerNaming selfMarkers)
        {
            // Both sides are measured against both addresses. Neither address involved in the
            // merge is a name, whichever row happens to be wearing it: a LID row is often
            // labelled with the very number the canonical row is addressed by, and a canonical
            // row labelled with the LID's digits would otherwise pass as named and refuse the
            // real name the transient row is bringing.
            if (!IsUnnamed(canonical, canonicalJid, transientJid, selfMarkers) ||
                IsUnnamed(transient, transientJid, canonicalJid, selfMarkers))
            {
                return;
            }

            canonical.Name = transient.Name;
        }

        /// <summary>
        /// Whether a row is effectively nameless: blank, showing one of the merged addresses
        /// back at us, or carrying a self-marker.
        /// </summary>
        /// <remarks>
        /// The self-marker case is why this is not a blank check. A row labelled "(You)"
        /// looks named but is the label for the user's own chat, and leaving it on a
        /// contact's row makes their messages read as the user's own -- which a contact can
        /// arrange by setting their push name.
        /// </remarks>
        private static bool IsUnnamed(
            ChatItem chat,
            string ownJid,
            string otherJid,
            ISelfMarkerNaming selfMarkers)
        {
            if (string.IsNullOrWhiteSpace(chat.Name))
            {
                return true;
            }

            if (EchoesAddress(chat.Name, ownJid) || EchoesAddress(chat.Name, otherJid))
            {
                return true;
            }

            return selfMarkers != null && selfMarkers.IsMarkerLabel(chat.Name);
        }

        /// <summary>
        /// Whether a label is just an address written back at us.
        /// </summary>
        /// <remarks>
        /// Two tests, because neither covers the other. `IsPhoneEcho` sees through
        /// punctuation, so "+55 11 99999-0000" is recognised as the number it is, but it
        /// ignores anything shorter than a phone number on the grounds that short numeric
        /// nicknames are legitimate. A LID is frequently shorter than that, so the plain
        /// comparison still has to catch the digits repeated verbatim.
        /// </remarks>
        private static bool EchoesAddress(string name, string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return false;
            }

            return string.Equals(name, BareUser(jid), StringComparison.Ordinal) ||
                   ContactLabelSanitizer.IsPhoneEcho(name, jid);
        }

        private static string BareUser(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            int at = jid.IndexOf('@');
            return at < 0 ? jid : jid.Substring(0, at);
        }

        private static DateTime ComparableOrMin(DateTime? timestamp)
        {
            return timestamp.HasValue
                ? ChatMessageOrder.ToComparableUtc(timestamp.Value)
                : DateTime.MinValue;
        }

        /// <summary>
        /// Moves the transient conversation's messages into the canonical one, skipping
        /// those already there.
        /// </summary>
        /// <remarks>
        /// Both rows hold the same conversation, so the overlap is the normal case rather
        /// than the exception: whatever arrived before the alias was known was written to
        /// whichever address the sender used. Appending blindly would show every one of
        /// those twice.
        ///
        /// A message with no id cannot be recognised by id, so it falls back to reference
        /// identity — enough to stop a merge repeating itself, which is the case that
        /// actually occurs, since the two lists are built from the same objects.
        /// </remarks>
        /// <param name="knownIds">
        /// The canonical conversation's id index, updated as messages are added so the
        /// caller's index does not go stale. Optional: without one the index is built here,
        /// since a caller that passes nothing wants deduplication, not the absence of it.
        /// </param>
        public static void AppendMissingMessages(
            List<ChatMessage> canonical,
            IEnumerable<ChatMessage> transient,
            HashSet<string> knownIds = null)
        {
            if (canonical == null || transient == null)
            {
                return;
            }

            HashSet<string> seen = knownIds ?? BuildIdIndex(canonical);

            foreach (ChatMessage message in transient)
            {
                if (message == null)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(message.Id))
                {
                    if (!canonical.Contains(message))
                    {
                        canonical.Add(message);
                    }
                }
                else if (seen.Add(message.Id))
                {
                    canonical.Add(message);
                }
            }
        }

        private static HashSet<string> BuildIdIndex(List<ChatMessage> messages)
        {
            var index = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < messages.Count; i++)
            {
                string id = messages[i]?.Id;
                if (!string.IsNullOrEmpty(id))
                {
                    index.Add(id);
                }
            }

            return index;
        }
    }
}
