// =============================================================================
// MessageRevocationContent
//
// What a revoked message looks like on the timeline: flagged, with no body and
// no media. The words the user reads are the UI's, in the user's language.
// Extracted so revoke apply cannot drift from history replay or a future
// façade path.
// =============================================================================
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class MessageRevocationContent
    {
        public static void ApplyTombstone(ChatMessage target)
        {
            if (target == null)
            {
                return;
            }

            target.IsRevoked = true;
            target.Content = string.Empty;
            target.Caption = string.Empty;
            target.Kind = ChatMessageKind.Text;
            target.IsImage = false;
            target.ImageUri = null;
            target.ImageUrl = null;
            target.ImageDirectPath = null;
            target.ImageMediaKeyBase64 = null;
            target.ImageFileEncSha256Base64 = null;
            target.ImageMimeType = null;
            target.VideoUri = null;
            target.VideoPosterUri = null;
            target.VideoUrl = null;
            target.VideoDirectPath = null;
            target.VideoMediaKeyBase64 = null;
            target.VideoFileEncSha256Base64 = null;
            target.VideoMimeType = null;
            target.VideoDurationSeconds = 0;
            target.IsAudio = false;
            target.AudioUri = null;
            target.AudioUrl = null;
            target.AudioDirectPath = null;
            target.AudioMediaKeyBase64 = null;
            target.AudioFileEncSha256Base64 = null;
        }
    }
}
