using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>Picks image / video / audio tile templates for the chat-info media grid.</summary>
    public sealed class ChatInfoMediaTemplateSelector : DataTemplateSelector
    {
        public DataTemplate ImageTemplate { get; set; }

        public DataTemplate VideoTemplate { get; set; }

        public DataTemplate AudioTemplate { get; set; }

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
            var vm = item as ChatMessageViewModel;
            if (vm == null)
            {
                return ImageTemplate;
            }

            if (vm.IsVideo)
            {
                return VideoTemplate;
            }

            if (vm.IsAudio)
            {
                return AudioTemplate;
            }

            return ImageTemplate;
        }
    }
}
