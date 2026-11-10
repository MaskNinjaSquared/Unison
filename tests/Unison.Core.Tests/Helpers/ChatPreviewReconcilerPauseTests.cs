using System.Reflection;
using Unison.Core.Contracts;
using Unison.Core.Helpers.Ui;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.WhatsAppClient.Chats;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ChatPreviewReconcilerPauseTests
    {
        private const string Chat = "5511999990001@s.whatsapp.net";

        private readonly JidAliasTable _aliases = new JidAliasTable();
        private readonly ChatStateStore _chats = new ChatStateStore(new CountingDispatcher());
        private readonly RecordingDiagnostics _diagnostics = new RecordingDiagnostics();

        private ChatPreviewReconciler Build(
            Func<UiHoldSnapshot> holds,
            IUiWorkStage? stage = null,
            bool frugal = false,
            TimeSpan? pauseMaxWait = null)
        {
            Func<UiWorkKind, UiWaitBudget>? budgets = pauseMaxWait.HasValue
                ? _ => new UiWaitBudget(TimeSpan.FromMilliseconds(10), pauseMaxWait.Value)
                : null;
            return new ChatPreviewReconciler(
                EmptyStore.Create<IHistoryMessageStore>(),
                _aliases,
                _chats,
                new ChatRowIndex(() => _chats.Chats, _aliases.PeekCanonicalJid),
                new CountingDispatcher(),
                new FallbackStrings(),
                _diagnostics,
                preferFrugal: () => frugal,
                uiHolds: new UiWorkHolds(holds, _diagnostics, budgets),
                uiStage: stage);
        }

        private static UiHoldSnapshot OfflineTip() =>
            new UiHoldSnapshot { WindowVisible = true, OfflineTipBusy = true };

        private static UiHoldSnapshot Hidden() => new UiHoldSnapshot { WindowVisible = false };

        [Fact]
        public async Task A_gate_that_timed_out_once_is_not_waited_again_in_the_same_run()
        {
            string[] chats = Enumerable.Range(0, 25)
                .Select(i => "55119999" + i.ToString("D5") + "@s.whatsapp.net")
                .ToArray();
            var reconciler = Build(OfflineTip, frugal: true, pauseMaxWait: TimeSpan.FromMilliseconds(80));

            await reconciler.ReconcileAsync(chats, "post-replay").WaitAsync(TimeSpan.FromSeconds(5));

            List<string> events = _diagnostics.Events;
            Assert.Single(events, e => e.StartsWith("ui-hold-timeout|", StringComparison.Ordinal));
            Assert.Equal(2, events.Count(e => e.StartsWith("ui-hold-sticky|", StringComparison.Ordinal)));
            Assert.Equal(3, events.Count(e => e.StartsWith("ui-slice-enter|", StringComparison.Ordinal)));
        }

        [Fact]
        public async Task A_new_run_waits_on_the_gate_again()
        {
            var reconciler = Build(OfflineTip, pauseMaxWait: TimeSpan.FromMilliseconds(50));

            await reconciler.ReconcileAsync(new[] { Chat }, "list-chunk").WaitAsync(TimeSpan.FromSeconds(5));
            await reconciler.ReconcileAsync(new[] { Chat }, "list-chunk").WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, _diagnostics.Events.Count(e => e.StartsWith("ui-hold-timeout|", StringComparison.Ordinal)));
        }

        [Fact]
        public async Task A_throwing_pause_gate_does_not_strand_the_reconcile()
        {
            var reconciler = Build(() => throw new InvalidOperationException("gate broke"));

            await reconciler.ReconcileAsync(new[] { Chat }, "list-chunk").WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains(_diagnostics.Events, e => e.StartsWith("ui-hold-failed|", StringComparison.Ordinal));
            Assert.Contains(_diagnostics.Events, e => e.StartsWith("ui-slice-enter|", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Paused_slice_logs_the_holding_gate_and_runs_once_it_clears()
        {
            int reads = 0;
            var reconciler = Build(() => Interlocked.Increment(ref reads) <= 3 ? Hidden() : UiHoldSnapshot.Clear);

            await reconciler.ReconcileAsync(new[] { Chat }, "list-chunk").WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains("ui-hold-paused|kind=TipSlice; reason=list-chunk; gate=hidden", _diagnostics.Events);
            int posted = _diagnostics.Events.FindIndex(e => e.StartsWith("ui-slice-posted|", StringComparison.Ordinal));
            int entered = _diagnostics.Events.FindIndex(e => e.StartsWith("ui-slice-enter|", StringComparison.Ordinal));
            Assert.True(posted >= 0 && entered > posted);
        }

        [Fact]
        public async Task Apply_marks_its_stage_on_the_ui_hop()
        {
            _chats.Chats.Add(new ChatItem { JID = Chat });
            var stage = new RecordingStage();

            await Build(() => UiHoldSnapshot.Clear, stage).ReconcileAsync(new[] { Chat }, "list-chunk").WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains(stage.Marks, m => m.StartsWith("tip:", StringComparison.Ordinal));
            // Pick + strip run off the dispatcher; the UI hop only finds rows and applies.
            Assert.DoesNotContain(stage.Marks, m => m.StartsWith("pick:", StringComparison.Ordinal));
            Assert.DoesNotContain(stage.Marks, m => m.StartsWith("strip:", StringComparison.Ordinal));
        }

        private sealed class RecordingStage : IUiWorkStage
        {
            public List<string> Marks { get; } = new List<string>();

            public void Mark(string stage) => Marks.Add(stage);
        }

        private sealed class FallbackStrings : IStringResources
        {
            public string Get(string key, string? fallback = null) => fallback ?? key;
        }

        private sealed class RecordingDiagnostics : IRuntimeDiagnostics
        {
            private readonly object _gate = new object();
            private readonly List<string> _events = new List<string>();

            public List<string> Events
            {
                get { lock (_gate) { return new List<string>(_events); } }
            }

            public bool IsVerboseLoggingEnabled => false;

            public void SetVerboseLogging(bool enabled, string source) { }
            public void Start() { }
            public void StartHealthSampling() { }

            public void Write(string category, string eventName, string? details = null)
            {
                lock (_gate)
                {
                    _events.Add(eventName + "|" + (details ?? string.Empty));
                }
            }

            public void RecordException(string category, string eventName, Exception exception, string? details = null) { }
            public string GetRecentText() => string.Empty;
            public RuntimeDiagnosticsSnapshot CaptureSnapshot() => null!;
            public Task FlushAsync(string reason) => Task.CompletedTask;
            public Task<string> ExportReportAsync() => Task.FromResult(string.Empty);
            public Task<string> SaveReportToLocalFolderAsync() => Task.FromResult(string.Empty);
            public Task ClearAsync() => Task.CompletedTask;
        }

        /// <summary>Interface stand-in whose every call returns an empty / completed result.</summary>
        public class EmptyStore : DispatchProxy
        {
            public static T Create<T>() where T : class => Create<T, EmptyStore>();

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                Type? returnType = targetMethod?.ReturnType;
                if (returnType == null || returnType == typeof(void))
                {
                    return null;
                }

                if (returnType == typeof(Task))
                {
                    return Task.CompletedTask;
                }

                if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    Type result = returnType.GetGenericArguments()[0];
                    object? value = result.IsValueType ? Activator.CreateInstance(result) : null;
                    return typeof(Task)
                        .GetMethod(nameof(Task.FromResult))!
                        .MakeGenericMethod(result)
                        .Invoke(null, new[] { value });
                }

                return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
            }
        }
    }
}
