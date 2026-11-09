// =============================================================================
// UiWorkHoldPolicy
//
// One rule for "may this kind of UI work run now?". Six wait loops used to ask
// it separately, each with its own poll, ceiling and clock (some had none). The
// gates and budgets live here; IUiWorkHolds is the only place that waits.
// =============================================================================
using System;

namespace Unison.Core.Helpers.Ui
{
    /// <summary>Kinds of deferrable UI work, each with its own gates and wait budget.</summary>
    public enum UiWorkKind
    {
        /// <summary>Chat-list tip reconcile slices (ChatPreviewReconciler).</summary>
        TipSlice,

        /// <summary>Silent row relabels (ChatNameRefresh, resolved-name apply).</summary>
        SilentNames,

        /// <summary>Participating-groups listing apply.</summary>
        GroupListing,

        /// <summary>Per-group metadata / roster apply.</summary>
        GroupMetadata,

        /// <summary>Alias follow-up (avatar collect, deduper) after alias imports.</summary>
        AliasFollowUp,
    }

    /// <summary>What every gate says at one instant.</summary>
    public struct UiHoldSnapshot
    {
        public bool DeepLinkHeld;
        public bool TimelineHeld;
        public bool WindowVisible;
        public bool ColdPopulate;
        public bool OfflineTipBusy;

        public static UiHoldSnapshot Clear => new UiHoldSnapshot { WindowVisible = true };
    }

    /// <summary>How long a kind polls and how long it may wait before it runs anyway.</summary>
    public struct UiWaitBudget
    {
        public UiWaitBudget(TimeSpan poll, TimeSpan maxWait)
        {
            Poll = poll;
            MaxWait = maxWait;
        }

        public TimeSpan Poll { get; }

        public TimeSpan MaxWait { get; }
    }

    public enum UiWaitOutcome
    {
        /// <summary>Nothing held the work (or the gate cleared).</summary>
        Clear,

        /// <summary>The budget ran out; the caller proceeds.</summary>
        TimedOut,

        /// <summary>A gate already timed out in this run; not waited again.</summary>
        Skipped,

        /// <summary>Reading the gates threw; the caller proceeds.</summary>
        Failed,

        Cancelled,
    }

    public static class UiWorkHoldPolicy
    {
        public static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(40);

        /// <summary>
        /// Same on every device. TipSlice: one 5s settle hold + buffer. Metadata / listing:
        /// two settle holds + buffer. Silent work had no ceiling and parked a whole session
        /// behind a stuck offline-tip gate (26b3431b); now it proceeds after 20s.
        /// </summary>
        public static UiWaitBudget BudgetFor(UiWorkKind kind)
        {
            switch (kind)
            {
                case UiWorkKind.TipSlice:
                    return new UiWaitBudget(Poll, TimeSpan.FromMilliseconds(8000));
                case UiWorkKind.GroupListing:
                case UiWorkKind.GroupMetadata:
                    return new UiWaitBudget(Poll, TimeSpan.FromMilliseconds(12000));
                default:
                    return new UiWaitBudget(Poll, TimeSpan.FromMilliseconds(20000));
            }
        }

        /// <summary>The gate holding <paramref name="kind"/> right now, or null when it may run.</summary>
        public static string Describe(UiWorkKind kind, UiHoldSnapshot s)
        {
            switch (kind)
            {
                case UiWorkKind.TipSlice:
                    if (s.DeepLinkHeld)
                    {
                        return "deeplink";
                    }

                    if (s.TimelineHeld)
                    {
                        return "timeline";
                    }

                    if (!s.WindowVisible)
                    {
                        return "hidden";
                    }

                    if (s.ColdPopulate)
                    {
                        return "cold-populate";
                    }

                    return s.OfflineTipBusy ? "offline-tip" : null;

                case UiWorkKind.GroupMetadata:
                case UiWorkKind.AliasFollowUp:
                    // Alias follow-up yields (re-arms) on offline-tip instead of waiting; it only
                    // waits out a chat-open timeline paint here.
                    return s.TimelineHeld ? "timeline" : null;

                default:
                    // SilentNames / GroupListing: tip-first only.
                    return s.OfflineTipBusy ? "offline-tip" : null;
            }
        }
    }
}
