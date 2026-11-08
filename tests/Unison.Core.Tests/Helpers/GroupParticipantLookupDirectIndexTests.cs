// =============================================================================
// RebuildDirectChatIndex must Peek (not Get) canonical JIDs: promoting LidMapping
// misses on a full catalogue walk floods alias Changed and stalls chat-open UI
// (self / 1:1 open while groups+alias follow-up are in flight).
// =============================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class GroupParticipantLookupDirectIndexTests
    {
        [Fact]
        public void EnsureFresh_personal_chat_peeks_canonical_jids_without_promoting()
        {
            var chats = new ObservableCollection<ChatItem>();
            for (int i = 0; i < 40; i++)
            {
                chats.Add(new ChatItem
                {
                    JID = "55119" + i.ToString("00000000") + "@s.whatsapp.net",
                    Name = "Contact " + i,
                    IsGroup = false
                });
            }

            GroupParticipantLookup.InvalidateDirectChatIndex();
            var jids = new CountingJids();
            var chatState = new StubChatState(chats);
            var context = new ParticipantResolutionContext(jids, contacts: null, chatState, people: null);
            using (var lookup = new GroupParticipantLookup(context, () => "You"))
            {
                lookup.EnsureFresh(new ChatItem
                {
                    JID = "5511999999999@s.whatsapp.net",
                    IsGroup = false,
                    Name = "Me"
                });
            }

            Assert.True(jids.PeekCount >= chats.Count, "expected Peek per personal row");
            Assert.Equal(0, jids.GetCount);
        }

        private sealed class CountingJids : IJidResolver
        {
            public int GetCount;
            public int PeekCount;

            public Profile Self => null;

            public string GetCanonicalJid(string jid)
            {
                GetCount++;
                return jid;
            }

            public string PeekCanonicalJid(string jid)
            {
                PeekCount++;
                return jid;
            }

            public bool TryGetAlias(string jid, out string alias)
            {
                alias = null;
                return false;
            }

            public bool IsSelfLinked(string jid) => false;
        }

        private sealed class StubChatState : IChatStateStore
        {
            public StubChatState(ObservableCollection<ChatItem> chats)
            {
                Chats = chats;
            }

            public ObservableCollection<ChatItem> Chats { get; }

            public bool IsLoadingPersistedChats => false;

            public event EventHandler<string> MessagesChanged
            {
                add { }
                remove { }
            }

            public event EventHandler DisplayNamesChanged
            {
                add { }
                remove { }
            }

            public event EventHandler<GroupRosterChangedEventArgs> GroupRosterChanged
            {
                add { }
                remove { }
            }

            public ChatItem FindChat(string jid) => null;

            public IReadOnlyList<ChatMessage> GetMessages(string chatJid) =>
                Array.Empty<ChatMessage>();

            public List<ChatMessage> GetConversationMessages(string chatJid, Func<string, string> canonicalOf) =>
                new List<ChatMessage>();

            public int GetMessageCount(string chatJid) => 0;

            public Task UpsertChatsAsync(IEnumerable<ChatItem> chats) => Task.CompletedTask;

            public Task RemoveChatAsync(string jid) => Task.CompletedTask;

            public Task UpsertMessagesAsync(string chatJid, IEnumerable<ChatMessage> messages) =>
                Task.CompletedTask;

            public Task AddMissingMessagesAsync(string chatJid, IEnumerable<ChatMessage> messages) =>
                Task.CompletedTask;

            public Task RemoveMessageAsync(string chatJid, string messageId) => Task.CompletedTask;

            public void NotifyMessagesChanged(string chatJid)
            {
            }

            public string ResolveDisplayName(string jid) => null;

            public Task MergePushNamesAsync(IEnumerable<KeyValuePair<string, string>> namesByJid) =>
                Task.CompletedTask;

            public Task MergeAddressBookNamesAsync(IEnumerable<KeyValuePair<string, string>> namesByJid) =>
                Task.CompletedTask;

            public void NotifyDisplayNamesChanged()
            {
            }

            public void NotifyGroupRosterChanged(string groupJid, IReadOnlyList<string> changedJids = null)
            {
            }

            public Task ClearAsync() => Task.CompletedTask;
        }
    }
}
