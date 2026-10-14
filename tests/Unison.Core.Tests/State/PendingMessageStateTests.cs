// =============================================================================
// Tests for PendingMessageState.
//
// Receipts and pins often arrive for a message that is not in memory: an old one
// outside the open page, or a chat that was never opened. Rather than touch disk
// on every receipt, the change is held and applied when the message is next
// loaded. Each held change applies once.
// =============================================================================
using System;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public sealed class PendingMessageStateTests
    {
        private const string Chat = "5511999990001@s.whatsapp.net";
        private static readonly DateTime T0 = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        private static readonly Func<string, string> AsIs = status => status;

        private readonly PendingMessageState _state = new PendingMessageState();

        private static ChatMessage Mine(string id, string status = "sent") =>
            new ChatMessage { Id = id, IsFromMe = true, Status = status };

        [Fact]
        public void A_held_status_reaches_the_message_once()
        {
            _state.HoldStatus("M1", "read");
            ChatMessage message = Mine("M1");

            Assert.True(_state.ApplyTo(message, Chat, AsIs));
            Assert.Equal("read", message.Status);

            message.Status = "sent";
            Assert.False(_state.ApplyTo(message, Chat, AsIs));
        }

        [Fact]
        public void A_status_never_moves_backwards()
        {
            _state.HoldStatus("M1", "delivered");
            ChatMessage message = Mine("M1", "read");

            Assert.False(_state.ApplyTo(message, Chat, AsIs));
            Assert.Equal("read", message.Status);
        }

        [Fact]
        public void Holding_keeps_the_most_advanced_status()
        {
            _state.HoldStatus("M1", "read");
            _state.HoldStatus("M1", "delivered");
            ChatMessage message = Mine("M1");

            _state.ApplyTo(message, Chat, AsIs);

            Assert.Equal("read", message.Status);
        }

        [Fact]
        public void The_chat_policy_shapes_the_held_status()
        {
            _state.HoldStatus("M1", "read");
            ChatMessage message = Mine("M1");

            _state.ApplyTo(message, Chat, status => "delivered");

            Assert.Equal("delivered", message.Status);
        }

        [Fact]
        public void Someone_elses_message_takes_no_status_and_keeps_it_held()
        {
            _state.HoldStatus("M1", "read");
            var theirs = new ChatMessage { Id = "M1", IsFromMe = false, Status = "sent" };

            Assert.False(_state.ApplyTo(theirs, Chat, AsIs));
            Assert.True(_state.ApplyTo(Mine("M1"), Chat, AsIs));
        }

        [Fact]
        public void A_forgotten_status_is_not_applied()
        {
            _state.HoldStatus("M1", "read");
            _state.ForgetStatus("M1");

            Assert.False(_state.ApplyTo(Mine("M1"), Chat, AsIs));
        }

        [Fact]
        public void A_held_pin_reaches_the_message_once()
        {
            PendingPin pin = _state.HoldPin(Chat, "M1", true, T0, T0.AddDays(7), T0.AddHours(1));
            var message = new ChatMessage { Id = "M1" };

            Assert.True(_state.ApplyTo(message, Chat, AsIs));
            Assert.True(message.IsPinned);
            Assert.Equal(T0, message.PinnedAtUtc);
            Assert.Equal(T0.AddDays(7), message.PinExpiresAtUtc);
            Assert.Equal(T0, pin.PinnedAtUtc);

            message.IsPinned = false;
            Assert.False(_state.ApplyTo(message, Chat, AsIs));
        }

        [Fact]
        public void A_pin_with_no_time_is_stamped_now()
        {
            PendingPin pin = _state.HoldPin(Chat, "M1", true, null, null, T0);

            Assert.Equal(T0, pin.PinnedAtUtc);
        }

        [Fact]
        public void An_unpin_clears_both_times()
        {
            PendingPin pin = _state.HoldPin(Chat, "M1", false, T0, T0.AddDays(1), T0);
            var message = new ChatMessage { Id = "M1", IsPinned = true, PinnedAtUtc = T0, PinExpiresAtUtc = T0 };

            Assert.Null(pin.PinnedAtUtc);
            Assert.Null(pin.ExpiresAtUtc);
            Assert.True(_state.ApplyTo(message, Chat, AsIs));
            Assert.False(message.IsPinned);
            Assert.Null(message.PinnedAtUtc);
            Assert.Null(message.PinExpiresAtUtc);
        }

        [Fact]
        public void A_pin_already_in_place_is_consumed_without_a_change()
        {
            _state.HoldPin(Chat, "M1", true, T0, null, T0);
            var message = new ChatMessage { Id = "M1", IsPinned = true, PinnedAtUtc = T0 };

            Assert.False(_state.ApplyTo(message, Chat, AsIs));
            message.IsPinned = false;
            Assert.False(_state.ApplyTo(message, Chat, AsIs));
        }

        [Fact]
        public void A_pin_is_held_per_chat()
        {
            _state.HoldPin(Chat, "M1", true, T0, null, T0);

            Assert.False(_state.ApplyTo(new ChatMessage { Id = "M1" }, "120363000000000000@g.us", AsIs));
            Assert.True(_state.ApplyTo(new ChatMessage { Id = "M1" }, Chat.ToUpperInvariant(), AsIs));
        }

        [Fact]
        public void A_forgotten_pin_is_not_applied()
        {
            _state.HoldPin(Chat, "M1", true, T0, null, T0);
            _state.ForgetPin(Chat, "M1");

            Assert.False(_state.ApplyTo(new ChatMessage { Id = "M1" }, Chat, AsIs));
        }

        [Fact]
        public void A_message_without_an_id_takes_nothing()
        {
            _state.HoldStatus("M1", "read");

            Assert.False(_state.ApplyTo(null, Chat, AsIs));
            Assert.False(_state.ApplyTo(new ChatMessage { IsFromMe = true }, Chat, AsIs));
        }
    }
}
