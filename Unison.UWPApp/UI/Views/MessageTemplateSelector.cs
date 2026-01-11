using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Unison.UWPApp.Models;

namespace Unison.UWPApp.UI.Views
{
    public class MessageTemplateSelector : DataTemplateSelector
    {
        public DataTemplate SentTemplate { get; set; }
        public DataTemplate ReceivedTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            var message = item as ChatMessage;
            if (message == null) return base.SelectTemplateCore(item, container);

            return message.IsFromMe ? SentTemplate : ReceivedTemplate;
        }
    }
}
