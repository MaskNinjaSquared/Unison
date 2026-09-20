using Unison.Core.Helpers;

namespace Unison.Core.Tests.Helpers;

/// <summary>
/// The number a usync contact query is addressed to. Two places derived this independently — the
/// new-chat search box and the background name refresh — and neither stripped everything a person
/// types into a phone field.
/// </summary>
public class ContactLookupNumberTests
{
    private const string Self = "5511900000000@s.whatsapp.net";

    [Fact]
    public void A_number_typed_with_brackets_and_dots_still_resolves()
    {
        // What the user types in the new-chat box reaches this unchanged. The old cleanup removed
        // '+', spaces and hyphens only, so brackets and dots survived into the query and the
        // server was asked about "+(11)999999999" — which simply finds nobody, with no error to
        // explain why.
        ContactLookupNumber result = ContactLookupNumber.For("(11) 99999-9999", null, Self);

        Assert.True(result.IsUsable);
        Assert.Equal("+11999999999", result.Number);
    }

    [Theory]
    [InlineData("+55 11 99999-9999", "+5511999999999")]
    [InlineData("55.11.99999.9999", "+5511999999999")]
    [InlineData("(55) 11 99999 9999", "+5511999999999")]
    [InlineData("55/11/999999999", "+5511999999999")]
    public void Punctuation_never_reaches_the_query(string typed, string expected)
    {
        Assert.Equal(expected, ContactLookupNumber.For(typed, null, Self).Number);
    }

    [Fact]
    public void An_international_prefix_is_dropped()
    {
        // 00 is how a person writes "dial out of the country"; the server wants the number.
        Assert.Equal("+5511999999999", ContactLookupNumber.For("005511999999999", null, Self).Number);
    }

    [Fact]
    public void A_jid_is_reduced_to_its_user_part()
    {
        ContactLookupNumber result = ContactLookupNumber.For(
            "5511988887777@s.whatsapp.net", null, Self);

        Assert.Equal("+5511988887777", result.Number);
    }

    [Fact]
    public void A_device_suffix_is_cut_before_the_digits_are_read_not_after()
    {
        // Order matters here and is easy to get backwards. Reading digits first turns
        // "5511988887777:12" into "551198888777712" - a number that belongs to nobody, asked
        // about in a query that looks perfectly well formed.
        ContactLookupNumber result = ContactLookupNumber.For(
            "5511988887777:12@s.whatsapp.net", null, Self);

        Assert.Equal("+5511988887777", result.Number);
    }

    [Fact]
    public void The_canonical_address_wins_when_one_is_known()
    {
        // A LID has no phone number in it. The canonical form is where the number lives.
        ContactLookupNumber result = ContactLookupNumber.For(
            "123456789@lid",
            "5511988887777@s.whatsapp.net",
            Self);

        Assert.Equal("+5511988887777", result.Number);
    }

    [Fact]
    public void Our_own_account_is_never_queried()
    {
        ContactLookupNumber result = ContactLookupNumber.For(Self, null, Self);

        Assert.False(result.IsUsable);
        Assert.Equal(ContactLookupSkip.SelfAccount, result.Skip);
    }

    [Fact]
    public void Our_own_account_is_recognised_through_a_device_suffix()
    {
        ContactLookupNumber result = ContactLookupNumber.For(
            "5511900000000:9@s.whatsapp.net", null, Self);

        Assert.Equal(ContactLookupSkip.SelfAccount, result.Skip);
    }

    [Theory]
    [InlineData("12345@g.us")]
    [InlineData("12345@broadcast")]
    [InlineData("12345@newsletter")]
    public void Anything_that_is_not_a_one_to_one_chat_is_skipped(string jid)
    {
        ContactLookupNumber result = ContactLookupNumber.For(jid, null, Self);

        Assert.False(result.IsUsable);
        Assert.Equal(ContactLookupSkip.NotDirectChat, result.Skip);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_in_means_nothing_out(string? input)
    {
        ContactLookupNumber result = ContactLookupNumber.For(input, null, Self);

        Assert.False(result.IsUsable);
        Assert.Equal(ContactLookupSkip.Empty, result.Skip);
    }

    [Fact]
    public void A_value_with_no_digits_at_all_is_reported_rather_than_queried()
    {
        ContactLookupNumber result = ContactLookupNumber.For("abc-def", null, Self);

        Assert.False(result.IsUsable);
        Assert.Equal(ContactLookupSkip.NoDigits, result.Skip);
    }

    [Fact]
    public void A_number_already_in_query_form_is_left_alone()
    {
        Assert.Equal("+5511988887777", ContactLookupNumber.For("+5511988887777", null, Self).Number);
    }

    [Fact]
    public void Without_a_known_self_account_nothing_is_mistaken_for_it()
    {
        ContactLookupNumber result = ContactLookupNumber.For("5511988887777@s.whatsapp.net", null, null);

        Assert.True(result.IsUsable);
        Assert.Equal("+5511988887777", result.Number);
    }
}
