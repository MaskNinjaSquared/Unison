// =============================================================================
// MediaPreviewTag
//
// The "[Image]" / "[Voice Message]" markers that stand in for a media message
// in a chat preview.
//
// These look like display strings but they are closer to a wire format: the
// background task matches them (including their legacy Portuguese spellings) to
// pick an icon and a localized label, and HistoryMessageMapper strips them when
// reading old rows so they never resurface as a caption. Changing the wording
// of one is not a cosmetic edit -- it silently stops being recognised.
//
// Unison.Background keeps its own copy of these strings on purpose: it cannot
// reference Unison.Core. The two lists have to be changed together.
// =============================================================================
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class MediaPreviewTag
    {
        public const string Image = "[Image]";
        public const string Video = "[Video]";
        public const string Sticker = "[Sticker]";
        public const string Document = "[Document]";

        /// <summary>A recorded voice note.</summary>
        public const string Voice = "[Voice Message]";

        /// <summary>An audio file that is not a voice note.</summary>
        public const string Audio = "[Audio]";

        /// <summary>
        /// The marker for an audio message. The distinction is the protocol's `ptt` flag,
        /// not the file type: a voice note and a shared music file are both audio, and
        /// showing "Voice message" for the second one misrepresents what arrived.
        /// </summary>
        public static string ForAudio(bool isVoiceNote)
        {
            return isVoiceNote ? Voice : Audio;
        }

        /// <summary>
        /// A marker followed by the caption, when there is one. A media message without a
        /// caption shows the bare marker rather than a trailing space.
        /// </summary>
        public static string WithCaption(string tag, string caption)
        {
            return string.IsNullOrWhiteSpace(caption) ? tag : tag + " " + caption;
        }

        /// <summary>The preview for an image, with its caption when present.</summary>
        public static string ForImage(string caption)
        {
            return WithCaption(Image, caption);
        }

        /// <summary>The preview for a video, with its caption when present.</summary>
        public static string ForVideo(string caption)
        {
            return WithCaption(Video, caption);
        }

        /// <summary>
        /// The preview for a document, named when the name is known. The file name is the
        /// only useful thing to show for a document, since there is no caption.
        /// </summary>
        public static string ForDocument(string fileName)
        {
            return WithCaption(Document, fileName);
        }

        /// <summary>
        /// The bare marker for a message that carries no text of its own.
        /// </summary>
        /// <remarks>
        /// For the recovery paths, which have the message's kind but not the render info
        /// that normally builds the preview. Audio resolves to the voice marker because the
        /// kind itself is <c>Voice</c> — a plain audio file infers as <c>Text</c> and is
        /// caught by the null return.
        ///
        /// Returns null rather than a marker for a kind that should have carried text, so
        /// the caller decides what to show instead of being handed a made-up label.
        /// </remarks>
        public static string ForKind(ChatPreviewKind kind)
        {
            switch (kind)
            {
                case ChatPreviewKind.Image:
                    return Image;
                case ChatPreviewKind.Video:
                    return Video;
                case ChatPreviewKind.Sticker:
                    return Sticker;
                case ChatPreviewKind.Document:
                    return Document;
                case ChatPreviewKind.Voice:
                    return Voice;
                default:
                    return null;
            }
        }
    }
}
