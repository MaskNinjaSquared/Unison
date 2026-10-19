// =============================================================================
// Tests for DuplicateChatFold.
//
// The dedupe pass over the chat list: every set of rows that resolve to one
// canonical address collapses into the row DuplicateChatRowMerge picks. Used to
// be inline in WhatsAppService.DeduplicateChatsAsync:
//
//   - rows with different canonical addresses are left alone;
//   - the survivor's address is normalized; the others are reported for removal;
//   - a secondary under another key hands over its messages and its push name
//     (when the survivor has none), and the pair is filed;
//   - which preview survives is judged on each row's own messages, measured
//     before any are moved. Fixed in the move: the secondary was measured after
//     its messages had gone to the survivor, so it always read as empty and an
//     unstamped newer preview never won.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class DuplicateChatFoldTests
    {
        private const string Pn = "5511999990001@s.whatsapp.net";
        private const string Lid = "111@lid";
        private const string Other = "5511999990002@s.whatsapp.net";

        private readonly ChatMessageCache _messages = new ChatMessageCache();
        private readonly DisplayNameTable _pushNames = new DisplayNameTable();
        private readonly JidAliasTable _aliases = new JidAliasTable();

        private DuplicateChatFold Fold() => new DuplicateChatFold(_messages, _pushNames, _aliases);

        private static ChatMessage At(string id, int minute) =>
            new ChatMessage { Id = id, Timestamp = new DateTime(2026, 1, 1, 12, minute, 0, DateTimeKind.Utc) };

        private void PairLidWithPn()
        {
            _aliases[Lid] = Pn;
            _aliases[Pn] = Lid;
        }

        [Fact]
        public void DistinctAddressesAreLeftAlone()
        {
            var rows = new[] { new ChatItem { JID = Pn }, new ChatItem { JID = Other } };

            DuplicateChatFoldResult result = Fold().Run(rows);

            Assert.Empty(result.Removed);
        }

        [Fact]
        public void PhoneRowSurvivesAndLidRowIsRemoved()
        {
            PairLidWithPn();
            var lidRow = new ChatItem { JID = Lid };
            var pnRow = new ChatItem { JID = Pn };

            DuplicateChatFoldResult result = Fold().Run(new[] { lidRow, pnRow });

            Assert.Same(lidRow, Assert.Single(result.Removed));
        }

        [Fact]
        public void SecondaryMessagesMoveUnderSurvivor()
        {
            PairLidWithPn();
            _messages.GetOrAdd(Lid).Add(At("L1", 1));
            _messages.GetOrAdd(Pn).Add(At("P1", 2));

            Fold().Run(new[] { new ChatItem { JID = Lid }, new ChatItem { JID = Pn } });

            Assert.False(_messages.Contains(Lid));
            Assert.Equal(2, _messages.CountFor(Pn));
        }

        [Fact]
        public void PushNameCarriesOnlyWhenSurvivorHasNone()
        {
            PairLidWithPn();
            _pushNames.TryAdd(Lid, "Ana");

            Fold().Run(new[] { new ChatItem { JID = Lid }, new ChatItem { JID = Pn } });

            Assert.True(_pushNames.TryGetValue(Pn, out string name));
            Assert.Equal("Ana", name);
        }

        [Fact]
        public void SurvivorPushNameIsKept()
        {
            PairLidWithPn();
            _pushNames.TryAdd(Lid, "Old");
            _pushNames.TryAdd(Pn, "Ana");

            Fold().Run(new[] { new ChatItem { JID = Lid }, new ChatItem { JID = Pn } });

            _pushNames.TryGetValue(Pn, out string name);
            Assert.Equal("Ana", name);
        }

        [Fact]
        public void UnstampedNewerSecondaryPreviewWins()
        {
            PairLidWithPn();
            _messages.GetOrAdd(Lid).Add(At("L1", 30));
            _messages.GetOrAdd(Pn).Add(At("P1", 10));
            var lidRow = new ChatItem { JID = Lid, LastMessage = "newer", LastMessageId = "L1" };
            var pnRow = new ChatItem { JID = Pn, LastMessage = "older", LastMessageId = "P1" };

            Fold().Run(new[] { lidRow, pnRow });

            Assert.Equal("newer", pnRow.LastMessage);
            Assert.Equal("L1", pnRow.LastMessageId);
        }

        [Fact]
        public void UnstampedOlderSecondaryPreviewLoses()
        {
            PairLidWithPn();
            _messages.GetOrAdd(Lid).Add(At("L1", 10));
            _messages.GetOrAdd(Pn).Add(At("P1", 30));
            var lidRow = new ChatItem { JID = Lid, LastMessage = "older", LastMessageId = "L1" };
            var pnRow = new ChatItem { JID = Pn, LastMessage = "newer", LastMessageId = "P1" };

            Fold().Run(new[] { lidRow, pnRow });

            Assert.Equal("newer", pnRow.LastMessage);
        }

        [Fact]
        public void SameKeyRowsMergeWithoutMovingMessages()
        {
            _messages.GetOrAdd(Pn).Add(At("P1", 1));
            var first = new ChatItem { JID = Pn, AvatarUrl = "ms-appdata:///local/a.jpg" };
            var second = new ChatItem { JID = " " + Pn, UnreadCount = 3 };

            DuplicateChatFoldResult result = Fold().Run(new[] { first, second });

            Assert.Same(second, Assert.Single(result.Removed));
            Assert.Equal(3, first.UnreadCount);
            Assert.Equal(1, _messages.CountFor(Pn));
        }

        [Fact]
        public void SurvivorAddressIsNormalized()
        {
            var first = new ChatItem { JID = " " + Pn + " ", AvatarUrl = "ms-appdata:///local/a.jpg" };
            var second = new ChatItem { JID = Pn };

            Fold().Run(new[] { first, second });

            Assert.Equal(Pn, first.JID);
        }

        [Fact]
        public void PairIsFiledFromTheMerge()
        {
            _aliases[Lid] = Pn;
            var rows = new[] { new ChatItem { JID = Lid }, new ChatItem { JID = Pn } };

            Fold().Run(rows);

            Assert.True(_aliases.TryGetValue(Pn, out string reverse));
            Assert.Equal(Lid, reverse);
        }

        [Fact]
        public void NullAndBlankRowsAreSkipped()
        {
            var rows = new List<ChatItem> { null, new ChatItem { JID = "" }, new ChatItem { JID = Pn } };

            DuplicateChatFoldResult result = Fold().Run(rows);

            Assert.Empty(result.Removed);
        }

        [Fact]
        public void NullRowsListIsEmpty()
        {
            Assert.Empty(Fold().Run(null).Removed);
        }
    }
}
