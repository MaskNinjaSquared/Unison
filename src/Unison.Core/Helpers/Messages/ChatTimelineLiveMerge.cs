using System;

namespace Unison.Core.Helpers
{
    /// <summary>How a live reload of the recent page lands on the open timeline.</summary>
    public enum ChatTimelineMergeMode
    {
        /// <summary>Window reaches the latest message and the user follows it: append, trim the oldest.</summary>
        FollowEnd,

        /// <summary>Window lost its newest rows to load-more and the user asked for the end: show the latest page.</summary>
        RejoinEnd,

        /// <summary>User is reading older messages: refresh rows in place, never trim under them.</summary>
        HoldPosition
    }

    /// <summary>
    /// Rules for merging the recent page while the user may be scrolled up. Loading older messages
    /// trims the newest rows, so the recent page is no longer contiguous with the window.
    /// </summary>
    public static class ChatTimelineLiveMerge
    {
        public static ChatTimelineMergeMode Choose(bool followingEnd, bool isDetachedFromEnd)
        {
            if (!followingEnd)
            {
                return ChatTimelineMergeMode.HoldPosition;
            }

            return isDetachedFromEnd ? ChatTimelineMergeMode.RejoinEnd : ChatTimelineMergeMode.FollowEnd;
        }

        /// <summary>
        /// A row newer than the newest one shown belongs past the trimmed edge; adding it back would
        /// leave a gap between it and the window.
        /// </summary>
        public static bool ShouldInsert(DateTime timestamp, DateTime? newestShown, bool isDetachedFromEnd)
        {
            return !isDetachedFromEnd || newestShown == null || timestamp <= newestShown.Value;
        }
    }
}
