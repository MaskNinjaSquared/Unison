using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Puts the newest in-memory tip onto each chat row, and creates a row when the timeline
    /// has messages but the list does not.
    /// </summary>
    /// <remarks>
    /// Called once after an offline drain (and similar bulk repairs) so the list does not take a
    /// UI hop per message. The SQLite tip pass is still <c>ChatPreviewReconciler</c>.
    /// </remarks>
    public sealed class StoredChatPreviewRefresh
    {
        private readonly IDispatcher _ui;
        private readonly ChatStateStore _chats;
        private readonly IJidResolver _jids;
        private readonly ContactDisplayNames _names;
        private readonly ISelfMarkerNaming _markers;
        private readonly IStringResources _strings;
        private readonly Func<DateTime> _utcNow;

        public StoredChatPreviewRefresh(
            IDispatcher ui,
            ChatStateStore chats,
            IJidResolver jids,
            ContactDisplayNames names,
            ISelfMarkerNaming markers,
            IStringResources strings,
            Func<DateTime> utcNow = null)
        {
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _names = names ?? throw new ArgumentNullException(nameof(names));
            _markers = markers ?? throw new ArgumentNullException(nameof(markers));
            _strings = strings ?? throw new ArgumentNullException(nameof(strings));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>How many rows had their strip rewritten.</summary>
        public async Task<int> RefreshAsync(string reason)
        {
            int updated = 0;
            await _ui.RunAsync(() =>
            {
                DateTime now = _utcNow();
                string self = SelfName();
                string yesterday = Yesterday();
                foreach (ChatItem chat in _chats.Chats)
                {
                    if (chat == null)
                    {
                        continue;
                    }

                    string canonical = _jids.GetCanonicalJid(chat.JID);
                    if (!_chats.MessagesByChat.TryGet(canonical, out List<ChatMessage> messages) ||
                        messages == null ||
                        messages.Count == 0)
                    {
                        continue;
                    }

                    ChatMessage latest = PickValidTip(messages, now);
                    if (latest == null)
                    {
                        continue;
                    }

                    bool isGroup = JidHelper.IsGroupJid(canonical) || chat.IsGroup;
                    if (ChatMessageTipApply.Apply(
                            chat,
                            latest,
                            isGroup,
                            force: false,
                            self,
                            _jids.GetCanonicalJid(latest.ParticipantJid),
                            yesterday))
                    {
                        updated++;
                    }
                }
            }).ConfigureAwait(false);
            return updated;
        }

        /// <summary>Rows touched and how many were created because only the timeline knew them.</summary>
        public async Task<StoredChatPreviewReconcileResult> ReconcileAsync(string reason)
        {
            var result = new StoredChatPreviewReconcileResult();
            await _ui.RunAsync(() =>
            {
                DateTime now = _utcNow();
                string self = SelfName();
                string yesterday = Yesterday();

                foreach (KeyValuePair<string, List<ChatMessage>> pair in _chats.MessagesByChat.Snapshot())
                {
                    string canonical = _jids.GetCanonicalJid(pair.Key);
                    if (string.IsNullOrWhiteSpace(canonical) || pair.Value == null || pair.Value.Count == 0)
                    {
                        continue;
                    }

                    ChatMessage latest = PickValidTip(pair.Value, now);
                    if (latest == null)
                    {
                        continue;
                    }

                    ChatItem chat = FindChat(canonical);
                    if (chat == null)
                    {
                        chat = new ChatItem
                        {
                            JID = canonical,
                            Name = _names.Resolve(canonical, "chat")
                        };
                        chat.ApplyKind(canonical, _jids.IsSelfLinked(canonical));
                        _chats.Chats.Add(chat);
                        result.Created++;
                    }

                    bool isGroup = JidHelper.IsGroupJid(canonical) || chat.IsGroup;
                    ChatMessageTipApply.Apply(
                        chat,
                        latest,
                        isGroup,
                        force: false,
                        self,
                        _jids.GetCanonicalJid(latest.ParticipantJid),
                        yesterday);
                    chat.ApplyKind(chat.JID, _jids.IsSelfLinked(chat.JID));

                    if (!chat.IsGroup &&
                        PlaceholderChatLabel.IsPlaceholder(
                            chat.Name,
                            canonical,
                            _markers.IsMarkerLabel(chat.Name)))
                    {
                        chat.Name = _names.Resolve(canonical, "chat");
                    }

                    result.Refreshed++;
                }

                ChatDisplayOrder.SortInPlace(_chats.Chats);
            }).ConfigureAwait(false);
            return result;
        }

        private ChatItem FindChat(string canonical)
        {
            foreach (ChatItem chat in _chats.Chats)
            {
                if (chat != null &&
                    string.Equals(
                        _jids.GetCanonicalJid(chat.JID),
                        canonical,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return chat;
                }
            }

            return null;
        }

        private static ChatMessage PickValidTip(IReadOnlyList<ChatMessage> messages, DateTime utcNow)
        {
            List<ChatMessage> usable = null;
            for (int i = 0; i < messages.Count; i++)
            {
                ChatMessage message = messages[i];
                if (message == null || !MessageTimestampValidity.IsValid(message.Timestamp, utcNow))
                {
                    continue;
                }

                if (usable == null)
                {
                    usable = new List<ChatMessage>();
                }

                usable.Add(message);
            }

            return ChatPreviewTip.PickLatest(usable);
        }

        private string SelfName() => _strings.Get("Chat_SelfFallbackName", "You");

        private string Yesterday() => _strings.Get("Common_Yesterday", "Yesterday");
    }

    /// <summary>How many timelines were applied and how many rows were born for them.</summary>
    public sealed class StoredChatPreviewReconcileResult
    {
        public int Refreshed { get; set; }

        public int Created { get; set; }
    }
}
