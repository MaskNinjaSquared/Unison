using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.TemplateSelectors
{
    /// <summary>
    /// <see cref="Controls.SettingBox"/> layout: compact (title + control on one row)
    /// when there is no description; expanded when <c>HasDescription</c> is true.
    /// Bind <c>Content</c> to <c>HasDescription</c>.
    /// </summary>
    public sealed class SettingBoxLayoutTemplateSelector : DataTemplateSelector
    {
        public DataTemplate CompactTemplate { get; set; }

        public DataTemplate WithDescriptionTemplate { get; set; }

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
            bool hasDescription = item is bool flag && flag;
            if (hasDescription)
            {
                return WithDescriptionTemplate ?? CompactTemplate;
            }

            return CompactTemplate ?? WithDescriptionTemplate;
        }
    }
}
