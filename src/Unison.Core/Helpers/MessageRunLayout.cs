using System;
using System.Collections.Generic;
using Unison.Core.Mappers;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// One pass over a visible timeline: run chrome, date chips, sender labels, and group
    /// author avatars. The bubble only binds the resolved <see cref="ChatMessage.ContactUri"/>.
    /// Mentions refresh stays on the ViewModel.
    /// </summary>
    public static class MessageRunLayout
    {
        public static void Apply(
            IList<ChatMessage> messages,
            bool isGroup,
            ChatItem groupChat,
            GroupParticipantLookup lookup,
            string todayLabel,
            string yesterdayLabel,
            Func<string, bool> quotedIdIsFromMe)
        {
            if (messages == null || messages.Count == 0)
            {
                return;
            }

            if (isGroup && lookup != null)
            {
                lookup.EnsureFresh(groupChat);
            }

            string today = string.IsNullOrWhiteSpace(todayLabel) ? "Today" : todayLabel;
            string yesterday = string.IsNullOrWhiteSpace(yesterdayLabel) ? "Yesterday" : yesterdayLabel;
            Func<string, bool> quoteFromMe = quotedIdIsFromMe ?? (_ => false);
            DateTime? previousLocalDate = null;

            for (int i = 0; i < messages.Count; i++)
            {
                ChatMessage current = messages[i];
                if (current == null)
                {
                    continue;
                }

                DateTime localDate = WhatsAppMapper.ToLocalCalendarDate(current.Timestamp);
                bool isFirstOfDay = localDate != DateTime.MinValue &&
                    (!previousLocalDate.HasValue || localDate != previousLocalDate.Value);
                current.IsFirstOfDay = isFirstOfDay;
                current.DateSeparatorText = isFirstOfDay
                    ? WhatsAppMapper.FormatDaySeparator(current.Timestamp, today, yesterday)
                    : string.Empty;
                if (localDate != DateTime.MinValue)
                {
                    previousLocalDate = localDate;
                }

                if (lookup != null)
                {
                    if (isGroup)
                    {
                        lookup.EnsureGroupSenderName(current, groupChat);
                    }

                    // Quotes exist in 1:1 too; self-quotes often omit Participant and must still show You.
                    lookup.EnsureQuotedSenderName(current, groupChat, quoteFromMe);
                }

                bool isRunStart = i == 0;
                bool isRunEnd = i == messages.Count - 1;

                if (!isRunStart)
                {
                    isRunStart = !IsSameMessageRun(messages[i - 1], current);
                }

                if (!isRunEnd)
                {
                    isRunEnd = !IsSameMessageRun(current, messages[i + 1]);
                }

                current.IsRunStart = isRunStart;
                current.IsRunEnd = isRunEnd;
                current.ShowGroupSenderName =
                    isGroup &&
                    isRunStart &&
                    !current.IsFromMe &&
                    !string.IsNullOrWhiteSpace(current.SenderName) &&
                    !string.Equals(current.SenderName, "Me", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(current.SenderName, "You", StringComparison.OrdinalIgnoreCase);

                bool contactSlot = isGroup && !current.IsFromMe;
                current.ShowContactSlot = contactSlot;
                current.ShowContact = contactSlot && isRunStart;
                current.ContactUri = contactSlot && lookup != null
                    ? lookup.ResolveContactUri(current.ParticipantJid, groupChat)
                    : null;

                current.ShowQuotedAuthorLink =
                    isGroup &&
                    current.HasQuote &&
                    (!string.IsNullOrWhiteSpace(current.QuotedParticipantJid) ||
                     !string.IsNullOrWhiteSpace(current.QuotedSenderName));
            }
        }

        /// <summary>
        /// Relabels date chips after local midnight (Hoje / Ontem / date).
        /// Does not re-resolve participant names/avatars — only the date separator fields.
        /// </summary>
        public static void RefreshDateSeparators(
            IList<ChatMessage> models,
            string todayLabel,
            string yesterdayLabel)
        {
            if (models == null || models.Count == 0)
            {
                return;
            }

            string today = string.IsNullOrWhiteSpace(todayLabel) ? "Today" : todayLabel;
            string yesterday = string.IsNullOrWhiteSpace(yesterdayLabel) ? "Yesterday" : yesterdayLabel;
            DateTime? previousLocalDate = null;

            for (int i = 0; i < models.Count; i++)
            {
                ChatMessage current = models[i];
                if (current == null)
                {
                    continue;
                }

                DateTime localDate = WhatsAppMapper.ToLocalCalendarDate(current.Timestamp);
                bool isFirstOfDay = localDate != DateTime.MinValue &&
                    (!previousLocalDate.HasValue || localDate != previousLocalDate.Value);
                current.IsFirstOfDay = isFirstOfDay;
                current.DateSeparatorText = isFirstOfDay
                    ? WhatsAppMapper.FormatDaySeparator(current.Timestamp, today, yesterday)
                    : string.Empty;
                if (localDate != DateTime.MinValue)
                {
                    previousLocalDate = localDate;
                }
            }
        }

        public static bool IsSameMessageRun(ChatMessage left, ChatMessage right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            if (left.IsFromMe != right.IsFromMe)
            {
                return false;
            }

            if (left.IsFromMe)
            {
                return true;
            }

            string leftParticipant = left.ParticipantJid ?? string.Empty;
            string rightParticipant = right.ParticipantJid ?? string.Empty;
            if (!string.IsNullOrEmpty(leftParticipant) && !string.IsNullOrEmpty(rightParticipant))
            {
                return string.Equals(leftParticipant, rightParticipant, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(
                left.SenderName ?? string.Empty,
                right.SenderName ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
