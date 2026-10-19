// =============================================================================
// IncomingTimeline
//
// Puts one arriving message into the in-memory timeline. In order: a direct
// message already shown under the contact's other address is moved here; one
// already in this chat (or under an alias) only upgrades the existing bubble;
// anything else is inserted in time order and the window is trimmed.
// Groups never look at aliases.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.State
{
    public sealed class IncomingTimeline
    {
        private readonly ChatMessageCache _messages;
        private readonly MessageIdIndex _index;
        private readonly Func<string, IReadOnlyList<string>> _aliasChatsOf;
        private readonly Func<string, string> _normalize;
        private readonly Action<string> _trimWindow;

        public IncomingTimeline(
            ChatMessageCache messages,
            MessageIdIndex index,
            Func<string, IReadOnlyList<string>> aliasChatsOf,
            Func<string, string> normalize,
            Action<string> trimWindow)
        {
            _messages = messages ?? throw new ArgumentNullException(nameof(messages));
            _index = index ?? throw new ArgumentNullException(nameof(index));
            _aliasChatsOf = aliasChatsOf ?? throw new ArgumentNullException(nameof(aliasChatsOf));
            _normalize = normalize ?? throw new ArgumentNullException(nameof(normalize));
            _trimWindow = trimWindow ?? throw new ArgumentNullException(nameof(trimWindow));
        }

        public IncomingTimelineAcceptResult Accept(string chatJid, ChatMessage message, bool isGroup)
        {
            var result = new IncomingTimelineAcceptResult
            {
                Kind = IncomingTimelineAcceptKind.Inserted,
                Message = message
            };

            if (string.IsNullOrWhiteSpace(chatJid) || message == null)
            {
                return result;
            }

            string jid = _normalize(chatJid) ?? chatJid;
            _messages.GetOrAdd(jid);

            bool hasId = !string.IsNullOrEmpty(message.Id);
            string duplicateChatJid = null;
            ChatMessage duplicateMessage;
            bool hasAliasLinkedDuplicate = !isGroup &&
                hasId &&
                AliasLinkedMessageFind.TryFind(
                    _messages,
                    _aliasChatsOf(jid),
                    message.Id,
                    out duplicateChatJid,
                    out duplicateMessage);

            ChatMessage consolidatedMessage;
            if (hasAliasLinkedDuplicate &&
                !string.Equals(_normalize(duplicateChatJid), jid, StringComparison.OrdinalIgnoreCase) &&
                AliasDuplicateMessageMove.TryMove(
                    _messages,
                    _index,
                    _normalize(jid),
                    _normalize(duplicateChatJid),
                    message.Id,
                    out consolidatedMessage))
            {
                result.Kind = IncomingTimelineAcceptKind.AliasConsolidated;
                result.AliasSourceChatJid = _normalize(duplicateChatJid);
                result.ConsolidatedMessage = consolidatedMessage;
                result.Message = consolidatedMessage ?? message;
                return result;
            }

            List<ChatMessage> timeline = _messages.GetOrAdd(jid);
            if ((hasId && IdsOf(jid).Contains(message.Id)) ||
                (hasId && timeline.Any(m => string.Equals(m?.Id, message.Id, StringComparison.Ordinal))) ||
                hasAliasLinkedDuplicate)
            {
                ChatMessage existingMessage = timeline.FirstOrDefault(
                    m => string.Equals(m?.Id, message.Id, StringComparison.Ordinal));
                bool existingChanged = false;
                if (existingMessage != null)
                {
                    DuplicateArrivalPatch patch = DuplicateArrivalEnrichment.Compute(
                        existingMessage.Status,
                        message.Status,
                        message.IsFromMe,
                        existingMessage.ParticipantJid,
                        message.ParticipantJid,
                        existingMessage.SenderName,
                        message.SenderName);
                    existingChanged = DuplicateArrivalEnrichment.Apply(existingMessage, patch);
                }

                result.Kind = hasAliasLinkedDuplicate && existingMessage == null
                    ? IncomingTimelineAcceptKind.AliasLinkedDuplicate
                    : IncomingTimelineAcceptKind.DuplicateSameChat;
                result.Message = existingMessage ?? message;
                result.ExistingChanged = existingChanged;
                result.AliasSourceChatJid = duplicateChatJid;
                return result;
            }

            ChatMessageOrder.InsertSorted(timeline, message);
            _trimWindow(jid);
            if (hasId)
            {
                IdsOf(jid).Add(message.Id);
            }

            return result;
        }

        // Add into the built set rather than MessageIdIndex.Register: Register starts an empty
        // bucket for an unbuilt chat, and every other message in it would then read as new.
        private HashSet<string> IdsOf(string chatJid)
        {
            string key = _normalize(chatJid);
            return _index.GetOrBuild(key, () =>
            {
                List<ChatMessage> list;
                return _messages.TryGet(key, out list) ? list : null;
            });
        }
    }
}
