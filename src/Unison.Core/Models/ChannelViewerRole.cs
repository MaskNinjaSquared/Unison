namespace Unison.Core.Models
{
    /// <summary>
    /// This account's role on a WhatsApp Channel (<c>viewer_metadata.role</c> in WMex newsletter
    /// replies; Baileys <c>NewsletterViewRole</c>). Only <see cref="Owner"/> and
    /// <see cref="Admin"/> may post.
    /// </summary>
    public enum ChannelViewerRole
    {
        /// <summary>Not fetched yet — treated as read-only.</summary>
        Unknown = 0,

        Owner = 1,

        Admin = 2,

        Subscriber = 3,

        Guest = 4
    }
}
