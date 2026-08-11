using Unison.Core.Models;
using Unison.Uwp.Helpers;
using Windows.UI;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Helpers
{
    /// <summary>
    /// Localized labels / colors for chat-list last-message kind chips.
    /// </summary>
    public static class ChatPreviewKindPresentation
    {
        private static readonly SolidColorBrush DefaultLabelBrush =
            new SolidColorBrush(Color.FromArgb(0xFF, 0x9F, 0x9F, 0x9F));

        private static readonly SolidColorBrush VoiceLabelBrush =
            new SolidColorBrush(Color.FromArgb(0xFF, 0x26, 0x95, 0xD9));

        public static string GetLabel(ChatPreviewKind kind)
        {
            switch (kind)
            {
                case ChatPreviewKind.Image:
                    return LocalizedStrings.Get("ChatList_PreviewPhoto");
                case ChatPreviewKind.Video:
                    return LocalizedStrings.Get("ChatList_PreviewVideo");
                case ChatPreviewKind.Sticker:
                    return LocalizedStrings.Get("ChatList_PreviewSticker");
                case ChatPreviewKind.Voice:
                    return LocalizedStrings.Get("ChatList_PreviewVoice");
                case ChatPreviewKind.Document:
                    return LocalizedStrings.Get("ChatList_PreviewDocument");
                case ChatPreviewKind.Reaction:
                    return LocalizedStrings.Get("ChatList_PreviewReaction");
                default:
                    return string.Empty;
            }
        }

        public static Brush GetLabelBrush(ChatPreviewKind kind)
        {
            return kind == ChatPreviewKind.Voice ? VoiceLabelBrush : DefaultLabelBrush;
        }

        public static Brush GetIconBrush(ChatPreviewKind kind)
        {
            return kind == ChatPreviewKind.Voice ? VoiceLabelBrush : DefaultLabelBrush;
        }
    }
}
