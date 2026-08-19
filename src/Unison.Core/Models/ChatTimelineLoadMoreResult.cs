namespace Unison.Core.Models
{
    /// <summary>
    /// Outcome of a top-of-timeline "load older" request from <see cref="ViewModels.ChatDetailViewModel"/>.
    /// The view uses <see cref="PrependedCount"/> to keep scroll offset stable after insert.
    /// </summary>
    public sealed class ChatTimelineLoadMoreResult
    {
        /// <summary>How many bubble VMs were inserted (may be less than the page if some rows already existed).</summary>
        public int PrependedCount { get; set; }

        /// <summary>True when SQLite/cache was empty and on-demand history was requested or is still pending.</summary>
        public bool WaitingForOnDemand { get; set; }

        /// <summary>True when further top-scroll load-more should stop for this chat.</summary>
        public bool ReachedStart { get; set; }
    }
}
