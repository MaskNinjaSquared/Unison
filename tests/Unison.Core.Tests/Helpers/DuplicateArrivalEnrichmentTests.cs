using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.Tests.Helpers;

/// <summary>
/// What a second delivery of the same message may improve on the row already held.
/// The offline fast-path and the full duplicate path both have to agree, or reconnect
/// silently drops upgrades the live path would have applied.
/// </summary>
public class DuplicateArrivalEnrichmentTests
{
    [Fact]
    public void A_better_tick_on_our_own_message_is_kept()
    {
        DuplicateArrivalPatch patch = DuplicateArrivalEnrichment.Compute(
            ChatMessage.StatusSent,
            ChatMessage.StatusRead,
            incomingIsFromMe: true,
            existingParticipantJid: null,
            incomingParticipantJid: null,
            existingSenderName: null,
            incomingSenderName: null);

        Assert.Equal(ChatMessage.StatusRead, patch.Status);
        Assert.True(patch.Changed);
    }

    [Fact]
    public void Someone_elses_ticks_are_not_rewritten_by_a_duplicate()
    {
        DuplicateArrivalPatch patch = DuplicateArrivalEnrichment.Compute(
            null,
            ChatMessage.StatusDelivered,
            incomingIsFromMe: false,
            existingParticipantJid: null,
            incomingParticipantJid: null,
            existingSenderName: null,
            incomingSenderName: null);

        Assert.Null(patch.Status);
        Assert.False(patch.Changed);
    }

    [Fact]
    public void A_blank_participant_is_filled_in()
    {
        DuplicateArrivalPatch patch = DuplicateArrivalEnrichment.Compute(
            null,
            null,
            incomingIsFromMe: false,
            existingParticipantJid: null,
            incomingParticipantJid: "5511888888888@s.whatsapp.net",
            existingSenderName: null,
            incomingSenderName: null);

        Assert.Equal("5511888888888@s.whatsapp.net", patch.ParticipantJid);
    }

    [Fact]
    public void An_existing_participant_is_left_alone()
    {
        DuplicateArrivalPatch patch = DuplicateArrivalEnrichment.Compute(
            null,
            null,
            incomingIsFromMe: false,
            existingParticipantJid: "old@s.whatsapp.net",
            incomingParticipantJid: "new@s.whatsapp.net",
            existingSenderName: null,
            incomingSenderName: null);

        Assert.Null(patch.ParticipantJid);
    }

    [Fact]
    public void A_blank_sender_name_is_filled_in()
    {
        DuplicateArrivalPatch patch = DuplicateArrivalEnrichment.Compute(
            null,
            null,
            incomingIsFromMe: false,
            existingParticipantJid: null,
            incomingParticipantJid: null,
            existingSenderName: "  ",
            incomingSenderName: "Maria");

        Assert.Equal("Maria", patch.SenderName);
    }

    [Fact]
    public void An_existing_sender_name_is_not_replaced()
    {
        DuplicateArrivalPatch patch = DuplicateArrivalEnrichment.Compute(
            null,
            null,
            incomingIsFromMe: false,
            existingParticipantJid: null,
            incomingParticipantJid: null,
            existingSenderName: "Ana",
            incomingSenderName: "Maria");

        Assert.Null(patch.SenderName);
    }

    [Fact]
    public void Apply_writes_only_what_the_patch_named()
    {
        var existing = new ChatMessage
        {
            Status = ChatMessage.StatusSent,
            ParticipantJid = null,
            SenderName = null
        };

        DuplicateArrivalPatch patch = DuplicateArrivalEnrichment.Compute(
            existing.Status,
            ChatMessage.StatusDelivered,
            incomingIsFromMe: true,
            existing.ParticipantJid,
            "5511999999999@s.whatsapp.net",
            existing.SenderName,
            "João");

        Assert.True(DuplicateArrivalEnrichment.Apply(existing, patch));
        Assert.Equal(ChatMessage.StatusDelivered, existing.Status);
        Assert.Equal("5511999999999@s.whatsapp.net", existing.ParticipantJid);
        Assert.Equal("João", existing.SenderName);
    }

    [Fact]
    public void An_empty_patch_changes_nothing()
    {
        var existing = new ChatMessage { SenderName = "Ana" };
        Assert.False(DuplicateArrivalEnrichment.Apply(existing, default(DuplicateArrivalPatch)));
        Assert.Equal("Ana", existing.SenderName);
    }
}
