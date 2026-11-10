using Unison.Core.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.TemplateSelectors
{
    /// <summary>
    /// Status viewer: text posts vs photo/video/sticker. Bind <c>Content</c> to
    /// <see cref="ChatPreviewKind"/> so the template swaps when the open item changes.
    /// </summary>
    public sealed class StatusKindTemplateSelector : DataTemplateSelector
    {
        public DataTemplate TextTemplate { get; set; }

        public DataTemplate MediaTemplate { get; set; }

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

            if (kind == ChatPreviewKind.Text)
            {
                return TextTemplate ?? MediaTemplate;
            }

            return MediaTemplate ?? TextTemplate;
        }
    }
}
