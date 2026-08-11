using Unison.Core.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.TemplateSelectors
{
    /// <summary>
    /// Selects the chat-list preview chip (icon + media label) for
    /// <see cref="ChatPreviewKind"/>. Bind <c>Content</c> to
    /// <c>LastMessageKind</c> so template swaps when the kind changes.
    /// </summary>
    public sealed class ChatPreviewKindTemplateSelector : DataTemplateSelector
    {
        public DataTemplate TextTemplate { get; set; }
        public DataTemplate ImageTemplate { get; set; }
        public DataTemplate VideoTemplate { get; set; }
        public DataTemplate StickerTemplate { get; set; }
        public DataTemplate VoiceTemplate { get; set; }
        public DataTemplate DocumentTemplate { get; set; }
        public DataTemplate ReactionTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item)
        {
            return SelectForKind(item);
        }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            return SelectForKind(item);
        }

        private DataTemplate SelectForKind(object item)
        {
            ChatPreviewKind kind = item is ChatPreviewKind typed
                ? typed
                : ChatPreviewKind.Text;

            switch (kind)
            {
                case ChatPreviewKind.Image:
                    return ImageTemplate ?? TextTemplate;
                case ChatPreviewKind.Video:
                    return VideoTemplate ?? TextTemplate;
                case ChatPreviewKind.Sticker:
                    return StickerTemplate ?? TextTemplate;
                case ChatPreviewKind.Voice:
                    return VoiceTemplate ?? TextTemplate;
                case ChatPreviewKind.Document:
                    return DocumentTemplate ?? TextTemplate;
                case ChatPreviewKind.Reaction:
                    return ReactionTemplate ?? TextTemplate;
                default:
                    return TextTemplate;
            }
        }
    }
}
