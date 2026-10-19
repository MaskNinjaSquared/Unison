// =============================================================================
// Tests for the send-path and disconnect-text rules lifted out of the client.
// Expected values are the literals the client already used.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class OutgoingMessageIdTests
    {
        [Fact]
        public void Companion_minted_ids_start_with_3EB0_and_uppercase_hex()
        {
            string id = OutgoingMessageId.FromRandomBytes(new byte[] { 0x0A, 0xB0, 0x00, 0xFF });

            Assert.Equal("3EB00AB000FF", id);
            Assert.Equal(10, OutgoingMessageId.RandomByteCount);
        }

        [Fact]
        public void Ten_zero_bytes_are_twenty_zero_digits()
        {
            string id = OutgoingMessageId.FromRandomBytes(new byte[OutgoingMessageId.RandomByteCount]);

            Assert.Equal("3EB0" + new string('0', 20), id);
        }

        [Fact]
        public void No_bytes_is_just_the_prefix()
        {
            Assert.Equal("3EB0", OutgoingMessageId.FromRandomBytes(null));
        }
    }

    public class OutgoingSendSettlementTests
    {
        [Theory]
        [InlineData(ChatMessage.StatusPending)]
        [InlineData("PENDING")]
        public void A_bubble_still_waiting_is_promoted(string status)
        {
            Assert.True(OutgoingSendSettlement.ShouldPromotePending(status));
        }

        [Theory]
        [InlineData(ChatMessage.StatusSent)]
        [InlineData(ChatMessage.StatusDelivered)]
        [InlineData(ChatMessage.StatusRead)]
        [InlineData(ChatMessage.StatusFailed)]
        [InlineData(null)]
        [InlineData("")]
        public void A_status_that_already_moved_is_left_alone(string status)
        {
            Assert.False(OutgoingSendSettlement.ShouldPromotePending(status));
        }
    }

    public class FatalStreamCodeTextTests
    {
        [Theory]
        [InlineData("stream error (401)", "401")]
        [InlineData("conflict (440)", "440")]
        [InlineData("forbidden (403)", "403")]
        [InlineData("failure (500)", "500")]
        public void A_parenthesised_code_is_taken_as_written(string message, string code)
        {
            Assert.Equal(code, FatalStreamCodeText.Read(message));
        }

        [Fact]
        public void The_numeric_code_wins_over_a_later_phrase()
        {
            // "(440)" is checked before "Logged out", so a message that carries both
            // keeps the code that was printed, not the phrase.
            Assert.Equal("440", FatalStreamCodeText.Read("Logged out (440)"));
        }

        [Theory]
        [InlineData("Logged out")]
        [InlineData("logged out by phone")]
        [InlineData("conflict device_removed")]
        [InlineData("conflict device-removed")]
        public void Logout_wording_without_a_code_is_401(string message)
        {
            Assert.Equal("401", FatalStreamCodeText.Read(message));
        }

        [Fact]
        public void Connection_replaced_is_440()
        {
            Assert.Equal("440", FatalStreamCodeText.Read("Connection replaced by another device"));
        }

        [Fact]
        public void Bad_session_is_500()
        {
            Assert.Equal("500", FatalStreamCodeText.Read("Bad session"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("socket timed out")]
        public void Anything_else_is_not_a_fatal_code(string message)
        {
            Assert.Null(FatalStreamCodeText.Read(message));
        }
    }

    public class DuplicateChatRowPickTests
    {
        [Fact]
        public void A_picture_beats_a_name_and_a_name_beats_a_blank_row()
        {
            var blank = new ChatItem { JID = "a", Name = "  " };
            var named = new ChatItem { JID = "b", Name = "Ada" };
            var pictured = new ChatItem { JID = "c", Name = null, AvatarUrl = "ms-appdata:///pic" };

            ChatItem picked = DuplicateChatRowPick.PreferredForPreview(new List<ChatItem> { blank, named, pictured });

            Assert.Same(pictured, picked);
        }

        [Fact]
        public void Among_equal_rows_the_earlier_one_stays()
        {
            var first = new ChatItem { JID = "a", Name = "Ada", AvatarUrl = "pic" };
            var second = new ChatItem { JID = "b", Name = "Ada", AvatarUrl = "other" };

            ChatItem picked = DuplicateChatRowPick.PreferredForPreview(new List<ChatItem> { first, second });

            Assert.Same(first, picked);
        }

        [Fact]
        public void A_name_is_enough_when_nobody_has_a_picture()
        {
            var blank = new ChatItem { JID = "a" };
            var named = new ChatItem { JID = "b", Name = "Ada" };

            Assert.Same(named, DuplicateChatRowPick.PreferredForPreview(new List<ChatItem> { blank, named }));
        }

        [Fact]
        public void No_rows_picks_nothing()
        {
            Assert.Null(DuplicateChatRowPick.PreferredForPreview(null));
            Assert.Null(DuplicateChatRowPick.PreferredForPreview(new List<ChatItem>()));
        }

        [Fact]
        public void The_first_non_blank_name_is_the_one_already_on_a_row()
        {
            var rows = new List<ChatItem>
            {
                null,
                new ChatItem { Name = "  " },
                new ChatItem { Name = "Ada" },
                new ChatItem { Name = "Bea" }
            };

            Assert.Equal("Ada", DuplicateChatRowPick.FirstNonBlankName(rows));
            Assert.Null(DuplicateChatRowPick.FirstNonBlankName(null));
        }
    }

    public class ReplayDiagnosticNoiseTests
    {
        [Theory]
        [InlineData("[Noise] Decrypted frame body")]
        [InlineData("[Signal] SenderKey cache miss for the group")]
        [InlineData("[Socket] Unpadded payload: abc")]
        public void Protocol_chatter_is_noise_during_replay(string message)
        {
            Assert.True(ReplayDiagnosticNoise.Matches(message));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("[WhatsAppService] SendTextMessageAsync to 1@s.whatsapp.net")]
        [InlineData("noise decrypted")]
        public void Ordinary_lines_are_kept(string message)
        {
            Assert.False(ReplayDiagnosticNoise.Matches(message));
        }
    }
}
