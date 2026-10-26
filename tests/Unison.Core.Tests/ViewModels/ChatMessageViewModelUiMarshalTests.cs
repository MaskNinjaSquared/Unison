using System.ComponentModel;
using System.Threading.Tasks;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.Tests.Helpers;
using Unison.Core.ViewModels;
using Xunit;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Core.Tests.ViewModels
{
    /// <summary>
    /// Incoming pump mutates <see cref="ChatMessage"/> off the UI thread. After the IsRevoked
    /// tombstone change (post-1.8.3), forwarding PropertyChanged inline into XAML threw
    /// RPC_E_WRONG_THREAD on Mobile. The bubble VM must hop through <see cref="Unison.Core.Contracts.IDispatcher"/>.
    /// </summary>
    public class ChatMessageViewModelUiMarshalTests
    {
        [Fact]
        public async Task Tombstone_IsRevoked_raises_through_dispatcher()
        {
            var ui = new CountingDispatcher();
            var model = new ChatMessage { Content = "hi" };
            var vm = new ChatMessageViewModel(model, ui: ui);
            int raised = 0;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChatMessage.IsRevoked) ||
                    e.PropertyName == nameof(ChatMessageViewModel.IsRevoked))
                {
                    raised++;
                }
            };

            MessageRevocationContent.ApplyTombstone(model);

            // Fire-and-forget RunAsync — give the inline CountingDispatcher a tick.
            await Task.Yield();

            Assert.True(model.IsRevoked);
            Assert.True(ui.Passes >= 1);
            Assert.True(raised >= 1);
            Assert.True(vm.IsRevoked);
        }

        [Fact]
        public async Task HasReactions_change_raises_through_dispatcher()
        {
            var ui = new CountingDispatcher();
            var model = new ChatMessage();
            var vm = new ChatMessageViewModel(model, ui: ui);
            int raised = 0;
            ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChatMessage.HasReactions) ||
                    e.PropertyName == nameof(ChatMessageViewModel.HasReactions))
                {
                    raised++;
                }
            };

            model.Reactions.Add(new MessageReaction
            {
                Emoji = "👍",
                ReactorJid = "1@s.whatsapp.net"
            });
            model.NotifyReactionsChanged();

            await Task.Yield();

            Assert.True(ui.Passes >= 1);
            Assert.True(raised >= 1);
            Assert.True(vm.HasReactions);
        }
    }
}
