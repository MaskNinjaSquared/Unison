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
        public DataTemplate GroupJoinTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            bool? fromMe = null;
            bool isSystemChrome = false;

            var vm = item as ChatMessageViewModel;
            if (vm != null)
            {
                fromMe = vm.IsFromMe;
                isSystemChrome = vm.IsGroupJoin || vm.IsCallEvent;
            }
            else
            {
                var message = item as ChatMessage;
                if (message != null)
                {
                    fromMe = message.IsFromMe;
                    isSystemChrome = message.IsGroupJoin || message.IsCallEvent;
                }
            }

            if (isSystemChrome && GroupJoinTemplate != null)
            {
                return GroupJoinTemplate;
            }

            if (fromMe == null)
            {
                return base.SelectTemplateCore(item, container);
            }

            return fromMe.Value ? SentTemplate : ReceivedTemplate;
        }
    }
}
