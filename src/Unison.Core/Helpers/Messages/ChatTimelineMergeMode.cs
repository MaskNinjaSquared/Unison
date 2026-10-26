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
}
