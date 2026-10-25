using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.State;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ActiveChatCacheSideEffectsTests
    {
        [Fact]
        public void Changing_active_chat_unloads_previous_cache_and_clears_status()
        {
            var ui = new ImmediateDispatcher();
            var chats = new ChatStateStore(ui);
            var active = new ActiveChat(jid => jid);
            var sync = new SyncStatusChannel(active, () => true);
            sync.Raise("Synchronizing…");

            chats.MessagesByChat.Set(
                "old@s.whatsapp.net",
                new System.Collections.Generic.List<Unison.Core.Models.ChatMessage>
                {
                    new Unison.Core.Models.ChatMessage { Id = "1" }
                });

            string lastStatus = "Synchronizing…";
            sync.Changed += (s, status) => lastStatus = status;

            using (var side = new ActiveChatCacheSideEffects(active, chats, sync))
            {
                active.Open("old@s.whatsapp.net");
                active.Open("new@s.whatsapp.net");
            }

            Assert.False(chats.MessagesByChat.TryGet("old@s.whatsapp.net", out _));
            Assert.True(string.IsNullOrEmpty(lastStatus));
        }

        private sealed class ImmediateDispatcher : IDispatcher
        {
            public Task RunAsync(Action action)
            {
                action?.Invoke();
                return Task.CompletedTask;
            }
        }
    }
}
