// =============================================================================
// Tests for the next rules lifted out of WhatsAppService. Expected values are
// the literals the client already produced.
// =============================================================================
using System;
using System.Text;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class DiagnosticJidMaskTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_missing_jid_is_a_dash(string jid)
        {
            Assert.Equal("-", DiagnosticJidMask.Apply(jid));
        }

        [Fact]
        public void Only_the_last_four_digits_of_a_number_survive()
        {
            Assert.Equal("*9999@s.whatsapp.net", DiagnosticJidMask.Apply("5511999999999@s.whatsapp.net"));
        }

        [Fact]
        public void A_user_of_four_characters_or_fewer_is_kept_whole()
        {
            Assert.Equal("*ab@lid", DiagnosticJidMask.Apply("ab@lid"));
            Assert.Equal("*1234@lid", DiagnosticJidMask.Apply("1234@lid"));
        }

        [Fact]
        public void An_address_with_no_domain_still_keeps_a_tail()
        {
            Assert.Equal("*efgh", DiagnosticJidMask.Apply("abcdefgh"));
        }
    }

    public class UnixTimeTests
    {
        [Fact]
        public void Zero_is_the_unix_epoch_in_utc()
        {
            DateTime instant = UnixTime.FromMilliseconds(0);

            Assert.Equal(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc), instant);
            Assert.Equal(DateTimeKind.Utc, instant.Kind);
        }

        [Fact]
        public void A_leftover_half_second_is_kept()
        {
            Assert.Equal(
                new DateTime(1970, 1, 1, 0, 0, 1, 500, DateTimeKind.Utc),
                UnixTime.FromMilliseconds(1500));
        }
    }

    public class DiagnosticUtcTicksTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Zero_or_negative_ticks_mean_never(long ticks)
        {
            Assert.Equal(DateTime.MinValue, DiagnosticUtcTicks.ToDateTime(ticks));
        }

        [Fact]
        public void A_positive_count_is_utc()
        {
            var expected = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

            DateTime actual = DiagnosticUtcTicks.ToDateTime(expected.Ticks);

            Assert.Equal(expected, actual);
            Assert.Equal(DateTimeKind.Utc, actual.Kind);
        }
    }

    public class SelfPushNameCaptureTests
    {
        [Theory]
        [InlineData("-")]
        [InlineData(" - ")]
        public void A_dash_is_not_a_name(string pushName)
        {
            Assert.True(SelfPushNameCapture.IsDashPlaceholder(pushName));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Ada")]
        [InlineData("—")]
        public void Anything_other_than_a_dash_is_a_candidate(string pushName)
        {
            Assert.False(SelfPushNameCapture.IsDashPlaceholder(pushName));
        }

        [Fact]
        public void A_later_echo_does_not_replace_a_name_already_trusted()
        {
            Assert.Equal("Ada", SelfPushNameCapture.PreferEstablished("Ada", "Someone else", fromAppState: false));
        }

        [Fact]
        public void A_deliberate_rename_on_another_device_replaces_it()
        {
            Assert.Equal("Bea", SelfPushNameCapture.PreferEstablished("Ada", "Bea", fromAppState: true));
        }

        [Fact]
        public void The_same_name_again_is_kept()
        {
            Assert.Equal("Ada", SelfPushNameCapture.PreferEstablished("Ada", "Ada", fromAppState: false));
        }

        [Fact]
        public void The_first_real_name_is_taken()
        {
            Assert.Equal("Ada", SelfPushNameCapture.PreferEstablished(null, "Ada", fromAppState: false));
            Assert.Equal("Ada", SelfPushNameCapture.PreferEstablished("  ", "Ada", fromAppState: false));
        }
    }

    public class FirstNonBlankTextTests
    {
        [Fact]
        public void The_first_usable_value_wins_and_is_trimmed()
        {
            Assert.Equal("Ada", FirstNonBlankText.Pick(null, "  ", " Ada ", "Bea"));
        }

        [Fact]
        public void Nothing_usable_is_nothing()
        {
            Assert.Null(FirstNonBlankText.Pick(null));
            Assert.Null(FirstNonBlankText.Pick());
        }
    }

    public class MessageKeyAltJidScanTests
    {
        [Fact]
        public void A_later_string_field_that_looks_like_a_user_is_returned()
        {
            byte[] bytes = LengthDelimited(5, "5511999999999@s.whatsapp.net");

            Assert.Equal("5511999999999@s.whatsapp.net", MessageKeyAltJidScan.Read(bytes));
        }

        [Fact]
        public void The_known_participant_field_is_skipped()
        {
            byte[] participant = LengthDelimited(4, "111@s.whatsapp.net");
            byte[] alt = LengthDelimited(5, "222@lid");

            Assert.Equal("222@lid", MessageKeyAltJidScan.Read(Concat(participant, alt)));
        }

        [Fact]
        public void A_group_address_is_not_a_user_and_the_scan_continues()
        {
            byte[] group = LengthDelimited(5, "123@g.us");
            byte[] phone = LengthDelimited(6, "333@hosted");

            Assert.Equal("333@hosted", MessageKeyAltJidScan.Read(Concat(group, phone)));
        }

        [Fact]
        public void A_varint_in_front_does_not_hide_the_address()
        {
            byte[] flag = new byte[] { 0x08, 0x01 };
            byte[] phone = LengthDelimited(5, "444@s.whatsapp.net");

            Assert.Equal("444@s.whatsapp.net", MessageKeyAltJidScan.Read(Concat(flag, phone)));
        }

        [Fact]
        public void Nothing_to_scan_returns_nothing()
        {
            Assert.Null(MessageKeyAltJidScan.Read(new byte[0]));
            Assert.Null(MessageKeyAltJidScan.Read(null));
        }

        private static byte[] LengthDelimited(int fieldNumber, string text)
        {
            byte[] payload = Encoding.UTF8.GetBytes(text);
            return Concat(new byte[] { (byte)((fieldNumber << 3) | 2), (byte)payload.Length }, payload);
        }

        private static byte[] Concat(byte[] left, byte[] right)
        {
            var combined = new byte[left.Length + right.Length];
            left.CopyTo(combined, 0);
            right.CopyTo(combined, left.Length);
            return combined;
        }
    }
}
