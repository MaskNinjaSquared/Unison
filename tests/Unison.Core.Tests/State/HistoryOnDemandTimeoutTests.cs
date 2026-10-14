// =============================================================================
// Tests for HistoryOnDemandTimeout.
//
// An accepted request the phone never answers has to be forgotten, or the chat
// (or the whole catch-up) stays blocked until the next connect. One answered in
// time must not be expired on top of the answer.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public sealed class HistoryOnDemandTimeoutTests
    {
        private const string Chat = "5511999990000@s.whatsapp.net";
        private static readonly DateTime T0 = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan ChatWindow = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan FullWindow = TimeSpan.FromMinutes(6);

        private readonly HistoryOnDemandRegistry _registry = new HistoryOnDemandRegistry();
        private readonly List<TimeSpan> _delays = new List<TimeSpan>();
        private readonly TaskCompletionSource<bool> _window = new TaskCompletionSource<bool>();
        private Action _onElapsed;

        private HistoryOnDemandTimeout NewTimeout() =>
            new HistoryOnDemandTimeout(_registry, ChatWindow, FullWindow, Delay);

        private async Task Delay(TimeSpan span)
        {
            _delays.Add(span);
            await _window.Task;
            _onElapsed?.Invoke();
        }

        private HistoryOnDemandRequest StartChat()
        {
            Assert.Equal(HistoryOnDemandChatGate.Started, _registry.TryBeginChat(Chat, "M1", T0, out _));
            return _registry.RegisterChat(Chat, "R1", "M1", 10, "scroll", T0);
        }

        private HistoryOnDemandRequest StartFull()
        {
            Assert.True(_registry.TryClaimFull());
            return _registry.RegisterFull("F1", true, 100, "offline-stale", T0);
        }

        [Fact]
        public async Task An_unanswered_chat_request_expires_after_the_chat_window()
        {
            HistoryOnDemandRequest request = StartChat();

            Assert.True(NewTimeout().TryArm(request, out Task<HistoryOnDemandRequest> expiry));
            _window.SetResult(true);
            HistoryOnDemandRequest expired = await expiry;

            Assert.Equal(new[] { ChatWindow }, _delays);
            Assert.NotNull(expired);
            Assert.Equal("R1", expired.RequestId);
            Assert.NotSame(request, expired);
            Assert.False(_registry.IsChatPending(Chat));
        }

        [Fact]
        public async Task A_full_request_waits_the_full_window()
        {
            HistoryOnDemandRequest request = StartFull();

            Assert.True(NewTimeout().TryArm(request, out Task<HistoryOnDemandRequest> expiry));
            _window.SetResult(true);
            await expiry;

            Assert.Equal(new[] { FullWindow }, _delays);
            Assert.Equal(FullWindow, NewTimeout().WindowFor(request));
        }

        [Fact]
        public async Task A_request_answered_inside_the_window_is_not_expired()
        {
            HistoryOnDemandRequest request = StartChat();
            _onElapsed = () => _registry.CompleteChats(new[] { Chat });

            Assert.True(NewTimeout().TryArm(request, out Task<HistoryOnDemandRequest> expiry));
            _window.SetResult(true);

            Assert.Null(await expiry);
        }

        [Fact]
        public void A_request_is_armed_only_once()
        {
            HistoryOnDemandRequest request = StartChat();
            HistoryOnDemandTimeout timeout = NewTimeout();

            Assert.True(timeout.TryArm(request, out _));
            Assert.False(timeout.TryArm(request, out Task<HistoryOnDemandRequest> second));

            Assert.Null(second);
            Assert.Single(_delays);
        }

        [Fact]
        public void Nothing_is_armed_for_a_missing_request()
        {
            Assert.False(NewTimeout().TryArm(null, out Task<HistoryOnDemandRequest> expiry));
            Assert.Null(expiry);
            Assert.Empty(_delays);
        }
    }
}
