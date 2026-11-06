// =============================================================================
// NewsletterMetadataResult
//
// Flattened channel identity from a WMex newsletterMetadata reply. Picture may
// arrive as a full URL or a CDN direct_path (resolve with NewsletterPictureUrl).
// =============================================================================
namespace Unison.Socket.UseCases.Newsletter
{
    public sealed class NewsletterMetadataResult
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string PictureId { get; set; }

        public string PictureUrl { get; set; }

        public string PictureDirectPath { get; set; }

        /// <summary>
        /// <c>viewer_metadata.role</c> as sent: OWNER, ADMIN, SUBSCRIBER or GUEST.
        /// Null when the reply did not carry viewer metadata.
        /// </summary>
        public string ViewerRole { get; set; }

        public bool IsNotFound { get; set; }

        public string FailureReason { get; set; }

        /// <summary>HTTPS URL suitable for AvatarCache download, when any picture hint exists.</summary>
        public string ResolvedPictureUrl
        {
            get { return NewsletterPictureUrl.Resolve(PictureUrl, PictureDirectPath); }
        }

        public bool HasPicture
        {
            get { return !string.IsNullOrEmpty(ResolvedPictureUrl); }
        }
    }
}
