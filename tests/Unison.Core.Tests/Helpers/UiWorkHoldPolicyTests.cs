using Unison.Core.Helpers.Chats;
using Unison.Core.Helpers.Ui;

namespace Unison.Core.Tests.Helpers;

public class UiWorkHoldPolicyTests
{
    public static IEnumerable<object[]> AllSnapshots()
    {
        for (int mask = 0; mask < 32; mask++)
        {
            yield return new object[] { mask };
        }
    }

    private static UiHoldSnapshot FromMask(int mask) => new UiHoldSnapshot
    {
        DeepLinkHeld = (mask & 1) != 0,
        TimelineHeld = (mask & 2) != 0,
        WindowVisible = (mask & 4) == 0,
        ColdPopulate = (mask & 8) != 0,
        OfflineTipBusy = (mask & 16) != 0,
    };

    [Theory]
    [MemberData(nameof(AllSnapshots))]
    public void TipSlice_matches_the_former_tip_reconcile_pause_rule(int mask)
    {
        UiHoldSnapshot s = FromMask(mask);

        string expected = ChatTimelineOpenPriority.DescribeTipReconcilePause(
            s.DeepLinkHeld, s.TimelineHeld, s.WindowVisible, s.ColdPopulate, s.OfflineTipBusy);

        Assert.Equal(expected, UiWorkHoldPolicy.Describe(UiWorkKind.TipSlice, s));
    }

    [Theory]
    [MemberData(nameof(AllSnapshots))]
    public void Silent_names_and_listing_only_yield_to_offline_tips(int mask)
    {
        UiHoldSnapshot s = FromMask(mask);
        string expected = TipUiPipelineGate.AllowSilentUi(s.OfflineTipBusy) ? null : "offline-tip";

        Assert.Equal(expected, UiWorkHoldPolicy.Describe(UiWorkKind.SilentNames, s));
        Assert.Equal(expected, UiWorkHoldPolicy.Describe(UiWorkKind.GroupListing, s));
    }

    [Theory]
    [MemberData(nameof(AllSnapshots))]
    public void Metadata_and_alias_follow_up_wait_only_on_timeline(int mask)
    {
        UiHoldSnapshot s = FromMask(mask);
        string expected = s.TimelineHeld ? "timeline" : null;

        Assert.Equal(expected, UiWorkHoldPolicy.Describe(UiWorkKind.GroupMetadata, s));
        Assert.Equal(expected, UiWorkHoldPolicy.Describe(UiWorkKind.AliasFollowUp, s));
    }

    [Fact]
    public void Budgets_keep_former_ceilings_and_cap_silent_work()
    {
        Assert.Equal(ChatTimelineOpenPriority.UiSlicePauseMaxWait, UiWorkHoldPolicy.BudgetFor(UiWorkKind.TipSlice).MaxWait);
        Assert.Equal(ChatTimelineOpenPriority.TimelineHoldWaitMax, UiWorkHoldPolicy.BudgetFor(UiWorkKind.GroupMetadata).MaxWait);
        Assert.Equal(ChatTimelineOpenPriority.TimelineHoldWaitMax, UiWorkHoldPolicy.BudgetFor(UiWorkKind.GroupListing).MaxWait);
        foreach (UiWorkKind kind in Enum.GetValues(typeof(UiWorkKind)))
        {
            UiWaitBudget budget = UiWorkHoldPolicy.BudgetFor(kind);
            Assert.Equal(TimeSpan.FromMilliseconds(40), budget.Poll);
            Assert.True(budget.MaxWait > TimeSpan.Zero && budget.MaxWait <= TimeSpan.FromSeconds(20));
        }
    }
}
