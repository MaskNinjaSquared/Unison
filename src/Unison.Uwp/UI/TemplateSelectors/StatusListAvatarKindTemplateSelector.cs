using Unison.Core.Models;
using Unison.Uwp.UI.Controls;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.TemplateSelectors
{
    /// <summary>
    /// Status list avatar face: text Status vs photo/video thumb.
    /// <c>Content</c> is a <see cref="StatusListAvatarFace"/> (or a raw <see cref="ChatPreviewKind"/>).
    /// </summary>
    public sealed class StatusListAvatarKindTemplateSelector : DataTemplateSelector
    {
        public DataTemplate TextTemplate { get; set; }

        public DataTemplate PhotoTemplate { get; set; }

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
            ChatPreviewKind kind = ChatPreviewKind.Text;
            var face = item as StatusListAvatarFace;
            if (face != null)
            {
                kind = face.Kind;
            }
            else if (item is ChatPreviewKind typed)
            {
                kind = typed;
            }

            if (kind == ChatPreviewKind.Text)
            {
                return TextTemplate ?? PhotoTemplate;
            }

            return PhotoTemplate ?? TextTemplate;
        }
    }
}
