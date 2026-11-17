using Unison.Uwp.UI.Controls;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.TemplateSelectors
{
    /// <summary>
    /// Picks phone vs PC chrome for <c>ChatThemePreview</c>.
    /// <c>Content</c> must be a <see cref="ChatThemePreviewState"/>.
    /// </summary>
    public sealed class ChatThemePreviewFormFactorTemplateSelector : DataTemplateSelector
    {
        public DataTemplate MobileTemplate { get; set; }

        public DataTemplate DesktopTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item)
        {
            return Select(item);
        }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            return Select(item);
        }

        private DataTemplate Select(object item)
        {
            var state = item as ChatThemePreviewState;
            bool mobile = state != null && state.IsMobile;
            if (mobile)
            {
                return MobileTemplate ?? DesktopTemplate;
            }

            return DesktopTemplate ?? MobileTemplate;
        }
    }
}
