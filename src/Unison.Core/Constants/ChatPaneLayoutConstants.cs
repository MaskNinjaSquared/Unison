namespace Unison.Core.Constants
{
    /// <summary>
    /// Side-by-side (WideBoth) chat list / detail geometry, in effective pixels.
    /// Prefer these over magic numbers in views.
    /// </summary>
    public static class ChatPaneLayoutConstants
    {
        public const double DefaultListWidth = 380;
        public const double MinListWidth = 280;
        public const double MinDetailWidth = 280;
        public const double MaxListWidth = 720;

        /// <summary>Visible / hit resize grip width when hover or pressed.</summary>
        public const double SplitterWidth = 10;

        /// <summary>Grip overlap on the chat-list side of the boundary.</summary>
        public const double SplitterOverlapList = 2;

        /// <summary>Grip overlap on the chat-detail side (<see cref="SplitterWidth"/> − list overlap).</summary>
        public const double SplitterOverlapDetail = SplitterWidth - SplitterOverlapList;
    }
}
