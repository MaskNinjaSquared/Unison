using Unison.Core.Helpers;

namespace Unison.Core.Tests.Helpers;

public class IncomingSenderResolverTests
{
    [Fact]
    public void A_group_message_from_us_uses_our_name()
    {
        IncomingSenderResolution r = IncomingSenderResolver.Resolve(
            isGroup: true,
            envelopeFromMe: true,
            accountDisplayName: "Meu Nome",
            selfFallbackName: "You",
            participantResolvedName: "Outro",
            chatResolvedName: "Grupo",
            hasParticipant: true);

        Assert.Equal("Meu Nome", r.SenderName);
        Assert.True(r.IsFromMe);
    }

    [Fact]
    public void A_group_message_from_someone_else_uses_the_participant()
    {
        IncomingSenderResolution r = IncomingSenderResolver.Resolve(
            isGroup: true,
            envelopeFromMe: false,
            accountDisplayName: "Meu Nome",
            selfFallbackName: "You",
            participantResolvedName: "Maria",
            chatResolvedName: "Grupo",
            hasParticipant: true);

        Assert.Equal("Maria", r.SenderName);
        Assert.False(r.IsFromMe);
    }

    [Fact]
    public void A_group_message_without_a_participant_falls_back_to_the_chat_name()
    {
        IncomingSenderResolution r = IncomingSenderResolver.Resolve(
            isGroup: true,
            envelopeFromMe: false,
            accountDisplayName: null,
            selfFallbackName: "You",
            participantResolvedName: "ignored",
            chatResolvedName: "Grupo",
            hasParticipant: false);

        Assert.Equal("Grupo", r.SenderName);
    }

    [Fact]
    public void A_direct_message_from_us_is_from_us()
    {
        IncomingSenderResolution r = IncomingSenderResolver.Resolve(
            isGroup: false,
            envelopeFromMe: true,
            accountDisplayName: null,
            selfFallbackName: "Você",
            participantResolvedName: null,
            chatResolvedName: "Ana",
            hasParticipant: false);

        Assert.Equal("Você", r.SenderName);
        Assert.True(r.IsFromMe);
    }

    [Fact]
    public void A_direct_message_from_them_uses_the_chat_name()
    {
        IncomingSenderResolution r = IncomingSenderResolver.Resolve(
            isGroup: false,
            envelopeFromMe: false,
            accountDisplayName: "Meu Nome",
            selfFallbackName: "You",
            participantResolvedName: null,
            chatResolvedName: "Ana",
            hasParticipant: false);

        Assert.Equal("Ana", r.SenderName);
        Assert.False(r.IsFromMe);
    }
}
