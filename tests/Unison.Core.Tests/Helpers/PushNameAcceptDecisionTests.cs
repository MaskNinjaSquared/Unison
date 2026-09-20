using Unison.Core.Helpers;

namespace Unison.Core.Tests.Helpers;

public class PushNameAcceptDecisionTests
{
    private const string Jid = "5511999999999@s.whatsapp.net";

    [Fact]
    public void An_empty_offer_is_ignored()
    {
        Assert.Equal(
            PushNameAccept.IgnoreEmpty,
            PushNameAcceptDecision.Decide(null, null, Jid));
    }

    [Fact]
    public void A_real_name_already_on_file_is_left_alone()
    {
        Assert.Equal(
            PushNameAccept.IgnoreAlreadyNamed,
            PushNameAcceptDecision.Decide("Novo", "Ana", Jid));
    }

    [Fact]
    public void A_placeholder_existing_name_is_replaced()
    {
        Assert.Equal(
            PushNameAccept.Accept,
            PushNameAcceptDecision.Decide("Ana", "5511999999999", Jid));
    }

    [Fact]
    public void A_missing_existing_name_is_filled()
    {
        Assert.Equal(
            PushNameAccept.Accept,
            PushNameAcceptDecision.Decide("Ana", null, Jid));
    }

    [Fact]
    public void A_self_marker_existing_name_is_treated_as_unnamed()
    {
        Assert.Equal(
            PushNameAccept.Accept,
            PushNameAcceptDecision.Decide("Ana", "(You)", Jid, existingIsSelfMarker: true));
    }
}
