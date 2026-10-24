using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ResyncConversationsDispatchTests
    {
        [Fact]
        public async Task Wipes_then_waits_with_phases()
        {
            var host = new FakeHost();
            var phases = new List<ConversationResyncPhase>();
            // Progress<T> posts async on a sync context; collect synchronously here.
            var progress = new SyncProgress(phases);
            await ResyncConversationsDispatch.RunAsync(host, progress);
            Assert.True(host.Wiped);
            Assert.True(host.CachesCleared);
            Assert.True(host.CatalogEmptied);
            Assert.True(host.NotificationsCleared);
            Assert.True(host.ForceRepairSet);
            Assert.True(host.Waited);
            Assert.Equal(
                new[]
                {
                    ConversationResyncPhase.CleaningHistory,
                    ConversationResyncPhase.PreparingConversations
                },
                phases);
        }

        private sealed class SyncProgress : IProgress<ConversationResyncPhase>
        {
            private readonly List<ConversationResyncPhase> _phases;

            public SyncProgress(List<ConversationResyncPhase> phases) => _phases = phases;

            public void Report(ConversationResyncPhase value) => _phases.Add(value);
        }

        private sealed class FakeHost : IResyncConversationsHost
        {
            public bool Wiped;
            public bool CachesCleared;
            public bool CatalogEmptied;
            public bool NotificationsCleared;
            public bool ForceRepairSet;
            public bool Waited;

            public void LogResync(string message)
            {
            }

            public void ReportResyncPhase(
                IProgress<ConversationResyncPhase> progress,
                ConversationResyncPhase phase) =>
                progress?.Report(phase);

            public Task WipeChatsAndMessagesAsync()
            {
                Wiped = true;
                return Task.CompletedTask;
            }

            public Task ClearConversationCachesAsync()
            {
                CachesCleared = true;
                return Task.CompletedTask;
            }

            public Task PersistEmptyChatCatalogAsync()
            {
                CatalogEmptied = true;
                return Task.CompletedTask;
            }

            public void ClearAllNotificationsBestEffort() => NotificationsCleared = true;

            public void SetForceHistoryRepairPendingBestEffort() => ForceRepairSet = true;

            public Task WaitConversationResyncAsync()
            {
                Waited = true;
                return Task.CompletedTask;
            }
        }
    }
}
