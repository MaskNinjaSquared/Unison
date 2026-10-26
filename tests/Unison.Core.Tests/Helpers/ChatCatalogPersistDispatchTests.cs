using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ChatCatalogPersistDispatchTests
    {
        [Fact]
        public void Schedule_deferred_does_not_arm_timer()
        {
            var host = new FakeHost { NextRequest = PersistScheduleAction.Deferred };

            ChatCatalogPersistDispatch.Schedule(host);

            Assert.Equal(new[] { "request", "deferred" }, host.Log);
            Assert.DoesNotContain("arm", host.Log);
        }

        [Fact]
        public void Schedule_arms_when_not_suppressed()
        {
            var host = new FakeHost { NextRequest = PersistScheduleAction.ArmTimer };

            ChatCatalogPersistDispatch.Schedule(host);

            Assert.Equal(new[] { "request", "arm" }, host.Log);
        }

        [Fact]
        public async Task Debounce_skips_persist_when_try_begin_false()
        {
            var host = new FakeHost { BeginPersist = false };

            await ChatCatalogPersistDispatch.OnDebounceElapsedAsync(host);

            Assert.Equal(new[] { "try-begin" }, host.Log);
            Assert.DoesNotContain("persist", host.Log);
        }

        [Fact]
        public async Task Debounce_restores_debt_when_persist_fails()
        {
            var host = new FakeHost { BeginPersist = true, PersistOk = false };

            await ChatCatalogPersistDispatch.OnDebounceElapsedAsync(host);

            Assert.Equal(
                new[] { "try-begin", "persist", "restore" },
                host.Log);
        }

        [Fact]
        public void Enable_with_deferred_save_schedules()
        {
            var host = new FakeHost
            {
                EnableResult = PersistEnableResult.LiftedWithDeferredSave,
                NextRequest = PersistScheduleAction.ArmTimer
            };

            ChatCatalogPersistDispatch.EnableAfterStartup(host, "offline");

            Assert.Equal(
                new[]
                {
                    "enable",
                    "lifted:offline",
                    "flushing:offline",
                    "request",
                    "arm"
                },
                host.Log);
        }

        [Fact]
        public void Enable_already_enabled_is_noop()
        {
            var host = new FakeHost { EnableResult = PersistEnableResult.AlreadyEnabled };

            ChatCatalogPersistDispatch.EnableAfterStartup(host, "x");

            Assert.Equal(new[] { "enable" }, host.Log);
        }

        private sealed class FakeHost : IChatCatalogPersistHost
        {
            public List<string> Log { get; } = new List<string>();

            public PersistScheduleAction NextRequest { get; set; } =
                PersistScheduleAction.ArmTimer;

            public bool BeginPersist { get; set; } = true;

            public bool PersistOk { get; set; } = true;

            public PersistEnableResult EnableResult { get; set; } =
                PersistEnableResult.Lifted;

            public PersistScheduleAction RequestPersist()
            {
                Log.Add("request");
                return NextRequest;
            }

            public void LogDeferredDuringWarmup() => Log.Add("deferred");

            public void ArmDebounceTimer() => Log.Add("arm");

            public bool TryBeginPersist()
            {
                Log.Add("try-begin");
                return BeginPersist;
            }

            public Task<bool> RunFullPersistAsync()
            {
                Log.Add("persist");
                return Task.FromResult(PersistOk);
            }

            public void RestorePersistDebt() => Log.Add("restore");

            public PersistEnableResult EnableAfterStartup()
            {
                Log.Add("enable");
                return EnableResult;
            }

            public void LogSuppressionLifted(string reason) =>
                Log.Add("lifted:" + reason);

            public void LogFlushingDeferred(string reason) =>
                Log.Add("flushing:" + reason);
        }
    }
}