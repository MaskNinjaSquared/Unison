// =============================================================================
// Tests for HistoryOnDemandDispatch.
//
// Asking the phone for an older page of one chat. A page asked twice wastes
// the phone's budget and can earn a 479; a failed send that keeps the chat's
// slot blocks every later scroll in that chat until the next connect.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public sealed class HistoryOnDemandDispatchTests
    {
        private const string Chat = "5511999990000@s.whatsapp.net";
        private static readonly DateTime T0 = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

        private readonly HistoryOnDemandRegistry _registry = new HistoryOnDemandRegistry();
        private readonly FakeSocket _socket = new FakeSocket();
        private readonly List<string> _log = new List<string>();

        private HistoryOnDemandDispatch NewDispatch() =>
            new HistoryOnDemandDispatch(_registry, _socket, _log.Add);

        private static List<ChatMessage> Timeline(params string[] ids)
        {
            var list = new List<ChatMessage>();
            for (int i = 0; i < ids.Length; i++)
            {
                list.Add(new ChatMessage { Id = ids[i], IsFromMe = i % 2 == 1, Timestamp = T0.AddMinutes(i) });
            }

            return list;
        }

        // --- Chat page ---------------------------------------------------------

        [Fact]
        public async Task A_chat_page_is_asked_from_the_oldest_message_and_registered()
        {
            bool sent = await NewDispatch().RequestChatPageAsync(Chat, Timeline("old", "new"), 30, "scroll", T0);

            Assert.True(sent);
            Assert.Equal(1, _socket.ChatSends);
            Assert.Equal(Chat, _socket.LastChat);
            Assert.Equal("old", _socket.LastOldestId);
            Assert.False(_socket.LastOldestFromMe);
            Assert.Equal(new DateTimeOffset(T0).ToUnixTimeMilliseconds(), _socket.LastOldestTimestampMs);
            Assert.Equal(30, _socket.LastCount);
            Assert.Equal("stanza-1", _socket.LastStanzaId);

            HistoryOnDemandRequest request = _registry.Find("stanza-1");
            Assert.NotNull(request);
            Assert.Equal(2, request.BaselineMessageCount);
            Assert.Equal("scroll", request.TriggerReason);
            Assert.True(_registry.IsChatPending(Chat));
        }

        [Fact]
        public async Task The_same_anchor_is_not_asked_twice()
        {
            HistoryOnDemandDispatch dispatch = NewDispatch();
            List<ChatMessage> timeline = Timeline("old");
            await dispatch.RequestChatPageAsync(Chat, timeline, 30, "scroll", T0);
            _registry.Ack("stanza-1", null, T0);
            _registry.CompleteChats(new[] { Chat });

            bool again = await dispatch.RequestChatPageAsync(Chat, timeline, 30, "scroll", T0);

            Assert.False(again);
            Assert.Equal(1, _socket.ChatSends);
        }

        [Fact]
        public async Task A_chat_with_a_request_out_is_not_asked_again()
        {
            HistoryOnDemandDispatch dispatch = NewDispatch();
            await dispatch.RequestChatPageAsync(Chat, Timeline("old"), 30, "scroll", T0);

            bool again = await dispatch.RequestChatPageAsync(Chat, Timeline("older"), 30, "scroll", T0);

            Assert.False(again);
            Assert.Equal(1, _socket.ChatSends);
        }

        [Fact]
        public async Task Nothing_is_asked_without_a_ready_socket()
        {
            _socket.IsReady = false;

            bool sent = await NewDispatch().RequestChatPageAsync(Chat, Timeline("old"), 30, "scroll", T0);

            Assert.False(sent);
            Assert.Equal(0, _socket.ChatSends);
            Assert.False(_registry.IsChatPending(Chat));
        }

        [Fact]
        public async Task Nothing_is_asked_without_an_anchor()
        {
            HistoryOnDemandDispatch dispatch = NewDispatch();

            Assert.False(await dispatch.RequestChatPageAsync(Chat, null, 30, "scroll", T0));
            Assert.False(await dispatch.RequestChatPageAsync(Chat, new List<ChatMessage>(), 30, "scroll", T0));
            Assert.False(await dispatch.RequestChatPageAsync(Chat, Timeline(string.Empty), 30, "scroll", T0));
            Assert.False(await dispatch.RequestChatPageAsync(null, Timeline("old"), 30, "scroll", T0));
            Assert.Equal(0, _socket.ChatSends);
        }

        [Fact]
        public async Task A_send_that_throws_gives_the_chat_slot_back()
        {
            _socket.ThrowOnSend = true;

            bool sent = await NewDispatch().RequestChatPageAsync(Chat, Timeline("old"), 30, "scroll", T0);

            Assert.False(sent);
            Assert.False(_registry.IsChatPending(Chat));
            Assert.Null(_registry.Find("stanza-1"));
        }

        [Fact]
        public async Task A_backed_off_chat_is_skipped_and_logged()
        {
            HistoryOnDemandDispatch dispatch = NewDispatch();
            await dispatch.RequestChatPageAsync(Chat, Timeline("old"), 30, "scroll", T0);
            _registry.Ack("stanza-1", "479", T0);

            bool again = await dispatch.RequestChatPageAsync(Chat, Timeline("older"), 30, "scroll", T0.AddSeconds(1));

            Assert.False(again);
            Assert.Contains(_log, line => line.Contains("backoff active"));
        }

        // --- Full history ------------------------------------------------------

        [Fact]
        public async Task A_full_request_is_claimed_registered_and_sent()
        {
            HistoryFullRequestResult result =
                await NewDispatch().RequestFullAsync("offline-stale", true, () => 120, T0);

            Assert.Equal(HistoryFullRequestOutcome.Sent, result.Outcome);
            Assert.Equal("stanza-1", result.RequestId);
            Assert.Equal(120, result.BaselineMessageCount);
            Assert.Equal(1, _socket.FullSends);
            Assert.True(_registry.IsFullInFlight);
            HistoryOnDemandRequest request = _registry.Find("stanza-1");
            Assert.Equal(HistoryOnDemandKind.FullHistory, request.Kind);
            Assert.Equal(120, request.BaselineMessageCount);
            Assert.Equal("offline-stale", request.TriggerReason);
        }

        [Fact]
        public async Task A_second_full_request_on_the_same_connection_is_skipped()
        {
            HistoryOnDemandDispatch dispatch = NewDispatch();
            await dispatch.RequestFullAsync("first", true, () => 0, T0);

            HistoryFullRequestResult second = await dispatch.RequestFullAsync("second", true, () => 0, T0);

            Assert.Equal(HistoryFullRequestOutcome.AlreadyClaimed, second.Outcome);
            Assert.Equal(1, _socket.FullSends);
            Assert.Contains(_log, line => line.Contains("already pending") && line.Contains("stanza-1"));
        }

        [Fact]
        public async Task A_full_request_waits_for_a_ready_socket_without_claiming()
        {
            _socket.IsReady = false;
            bool counted = false;

            HistoryFullRequestResult result =
                await NewDispatch().RequestFullAsync("offline-stale", true, () => { counted = true; return 0; }, T0);

            Assert.Equal(HistoryFullRequestOutcome.SocketNotReady, result.Outcome);
            Assert.False(_registry.IsFullClaimed);
            Assert.False(counted);
        }

        [Fact]
        public async Task A_full_send_that_throws_releases_the_claim()
        {
            _socket.ThrowOnSend = true;

            HistoryFullRequestResult result =
                await NewDispatch().RequestFullAsync("offline-stale", true, () => 0, T0);

            Assert.Equal(HistoryFullRequestOutcome.Failed, result.Outcome);
            Assert.False(_registry.IsFullClaimed);
            Assert.Null(_registry.Find("stanza-1"));
            Assert.Contains(_log, line => line.Contains("request failed") && line.Contains("socket gone"));
        }

        private sealed class FakeSocket : IHistoryOnDemandSocket
        {
            private int _ids;

            public bool IsReady { get; set; } = true;
            public bool ThrowOnSend { get; set; }
            public int FullSends { get; private set; }
            public int ChatSends { get; private set; }
            public string LastChat { get; private set; }
            public string LastOldestId { get; private set; }
            public bool LastOldestFromMe { get; private set; }
            public long LastOldestTimestampMs { get; private set; }
            public int LastCount { get; private set; }
            public string LastStanzaId { get; private set; }

            public string GenerateMessageId() => "stanza-" + (++_ids);

            public Task<string> RequestChatHistoryAsync(
                string chatJid,
                string oldestMessageId,
                bool oldestFromMe,
                long oldestTimestampMs,
                int count,
                string stanzaId)
            {
                if (ThrowOnSend)
                {
                    throw new InvalidOperationException("socket gone");
                }

                ChatSends++;
                LastChat = chatJid;
                LastOldestId = oldestMessageId;
                LastOldestFromMe = oldestFromMe;
                LastOldestTimestampMs = oldestTimestampMs;
                LastCount = count;
                LastStanzaId = stanzaId;
                return Task.FromResult(stanzaId);
            }

            public Task<string> RequestFullHistoryAsync(string stanzaId)
            {
                if (ThrowOnSend)
                {
                    throw new InvalidOperationException("socket gone");
                }

                FullSends++;
                return Task.FromResult(stanzaId);
            }
        }
    }
}
