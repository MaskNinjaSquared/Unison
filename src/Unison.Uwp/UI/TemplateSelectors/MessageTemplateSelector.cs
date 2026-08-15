using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Unison.Core.Models;
using Unison.Core.ViewModels;

namespace Unison.Uwp.UI.TemplateSelectors
{
    public class MessageTemplateSelector : DataTemplateSelector
    {
        public DataTemplate SentTemplate { get; set; }
        public DataTemplate ReceivedTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            bool? fromMe = null;

            var vm = item as ChatMessageViewModel;
            if (vm != null)
            {
                fromMe = vm.IsFromMe;
            }
            else
            {
                var message = item as ChatMessage;
                if (message != null)
                {
                    fromMe = message.IsFromMe;
                }
            }

            if (fromMe == null)
            {
                return base.SelectTemplateCore(item, container);
            }

            return fromMe.Value ? SentTemplate : ReceivedTemplate;
        }
    }
}
