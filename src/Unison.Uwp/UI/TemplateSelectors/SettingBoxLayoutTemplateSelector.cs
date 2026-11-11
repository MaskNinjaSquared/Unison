using Unison.Uwp.UI.Controls;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.TemplateSelectors
{
    /// <summary>
    /// <see cref="SettingBox"/> layout: compact vs with-description.
    /// <c>Content</c> must be a <see cref="SettingBoxFace"/> (never a bool, never the box itself).
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
            var face = item as SettingBoxFace;
            bool hasDescription = face != null && face.HasDescription;
            if (hasDescription)
            {
                return WithDescriptionTemplate ?? CompactTemplate;
            }

            return CompactTemplate ?? WithDescriptionTemplate;
        }
    }
}
