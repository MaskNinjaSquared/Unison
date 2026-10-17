// =============================================================================
// Tests for SyncStatusChannel: the one line the chat list shows while the app
// syncs, what may replace it, and what is kept off it.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class SyncStatusChannelTests
    {
        private const string Chat = "5511999990001@s.whatsapp.net";

        private static readonly string HistoryCatchUp = SyncPhaseStatus.Format(SyncPhaseStatus.HistoryCatchUp);
        private static readonly string Names = SyncPhaseStatus.Format(SyncPhaseStatus.Names);

        private readonly ActiveChat _activeChat = new ActiveChat(jid => jid);
        private readonly List<string> _published = new List<string>();
        private bool _shown = true;

        private SyncStatusChannel Create()
        {
            var channel = new SyncStatusChannel(_activeChat, () => _shown);
            channel.Changed += (_, status) => _published.Add(status);
            return channel;
        }

        [Fact]
        public void A_status_reaches_the_list()
        {
            SyncStatusChannel channel = Create();

            channel.Raise("Re-syncing conversations...");
            channel.Raise(null);

            Assert.Equal(new[] { "Re-syncing conversations...", null }, _published);
        }

        [Fact]
        public void List_enrichment_is_kept_off_an_open_conversation()
        {
            SyncStatusChannel channel = Create();
            _activeChat.Open(Chat);

            channel.Raise(Names);

            Assert.Empty(_published);
        }

        [Fact]
        public void Other_statuses_still_show_over_an_open_conversation()
        {
            SyncStatusChannel channel = Create();
            _activeChat.Open(Chat);

            channel.Raise("History did not arrive.");

            Assert.Equal(new[] { "History did not arrive." }, _published);
        }

        [Fact]
        public void Starting_history_catch_up_shows_its_banner()
        {
            SyncStatusChannel channel = Create();

            channel.BeginHistoryCatchUp();

            Assert.True(channel.IsHistoryCatchUpShown);
            Assert.Equal(new[] { HistoryCatchUp }, _published);
        }

        [Fact]
        public void A_clear_during_history_catch_up_puts_the_banner_back()
        {
            SyncStatusChannel channel = Create();
            channel.BeginHistoryCatchUp();
            _published.Clear();

            channel.Raise(null);
            channel.Raise(string.Empty);

            Assert.Equal(new[] { HistoryCatchUp, HistoryCatchUp }, _published);
        }

        [Fact]
        public void Ending_history_catch_up_clears_the_line_once()
        {
            SyncStatusChannel channel = Create();
            channel.BeginHistoryCatchUp();
            _published.Clear();

            Assert.True(channel.EndHistoryCatchUp());
            Assert.False(channel.EndHistoryCatchUp());

            Assert.False(channel.IsHistoryCatchUpShown);
            Assert.Equal(new string[] { null }, _published);
        }

        [Fact]
        public void Ending_without_a_catch_up_publishes_nothing()
        {
            SyncStatusChannel channel = Create();

            Assert.False(channel.EndHistoryCatchUp());

            Assert.Empty(_published);
        }

        [Fact]
        public void Publish_skips_the_rules_but_not_the_setting()
        {
            SyncStatusChannel channel = Create();
            channel.BeginHistoryCatchUp();
            _activeChat.Open(Chat);
            _published.Clear();

            channel.Publish(null);
            channel.Publish(Names);
            _shown = false;
            channel.Publish(Names);

            Assert.Equal(new[] { null, Names, null }, _published);
        }

        [Fact]
        public void With_the_setting_off_every_status_arrives_as_a_clear()
        {
            SyncStatusChannel channel = Create();
            _shown = false;

            channel.Raise("Re-syncing conversations...");
            channel.BeginHistoryCatchUp();

            Assert.Equal(new string[] { null, null }, _published);
            Assert.True(channel.IsHistoryCatchUpShown);
        }

        [Fact]
        public void A_setting_that_cannot_be_read_shows_the_status()
        {
            var channel = new SyncStatusChannel(_activeChat, () => throw new InvalidOperationException());
            channel.Changed += (_, status) => _published.Add(status);

            channel.Raise("Re-syncing conversations...");

            Assert.Equal(new[] { "Re-syncing conversations..." }, _published);
        }

        [Fact]
        public void Missing_dependencies_are_rejected()
        {
            Assert.Throws<ArgumentNullException>(() => new SyncStatusChannel(null, () => true));
            Assert.Throws<ArgumentNullException>(() => new SyncStatusChannel(_activeChat, null));
        }
    }
}
