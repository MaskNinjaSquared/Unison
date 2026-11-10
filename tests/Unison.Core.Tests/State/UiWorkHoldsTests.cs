using Unison.Core.Contracts;
using Unison.Core.Helpers.Ui;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.Tests.State;

public class UiWorkHoldsTests
{
    private readonly RecordingDiagnostics _diagnostics = new RecordingDiagnostics();

    private UiWorkHolds Build(Func<UiHoldSnapshot> read, int maxWaitMs = 80) =>
        new UiWorkHolds(read, _diagnostics, _ => new UiWaitBudget(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(maxWaitMs)));

    private static UiHoldSnapshot TipBusy() => new UiHoldSnapshot { WindowVisible = true, OfflineTipBusy = true };

    [Fact]
    public async Task Clear_gates_return_immediately_without_journal()
    {
        var holds = Build(() => UiHoldSnapshot.Clear);

        UiWaitOutcome outcome = await holds.BeginRun("t").WaitAsync(UiWorkKind.SilentNames);

        Assert.Equal(UiWaitOutcome.Clear, outcome);
        Assert.Empty(_diagnostics.Events);
    }

    [Fact]
    public async Task Waits_until_the_gate_clears_and_journals_the_gate()
    {
        int reads = 0;
        var holds = Build(() => Interlocked.Increment(ref reads) <= 3 ? TipBusy() : UiHoldSnapshot.Clear, maxWaitMs: 5000);

        UiWaitOutcome outcome = await holds.BeginRun("names").WaitAsync(UiWorkKind.SilentNames);

        Assert.Equal(UiWaitOutcome.Clear, outcome);
        Assert.Contains("ui-hold-paused|kind=SilentNames; reason=names; gate=offline-tip", _diagnostics.Events);
        Assert.Contains(_diagnostics.Events, e => e.StartsWith("ui-hold-released|", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stuck_gate_times_out_once_then_is_skipped_for_the_rest_of_the_run()
    {
        var holds = Build(TipBusy);
        IUiWaitRun run = holds.BeginRun("post-replay");

        Assert.Equal(UiWaitOutcome.TimedOut, await run.WaitAsync(UiWorkKind.TipSlice));
        Assert.Equal(UiWaitOutcome.Skipped, await run.WaitAsync(UiWorkKind.TipSlice));
        Assert.Equal(UiWaitOutcome.Skipped, await run.WaitAsync(UiWorkKind.SilentNames));

        Assert.Equal(UiWaitOutcome.TimedOut, await holds.BeginRun("next").WaitAsync(UiWorkKind.TipSlice));
    }

    [Fact]
    public async Task A_throwing_gate_read_fails_open()
    {
        var holds = Build(() => throw new InvalidOperationException("boom"));

        UiWaitOutcome outcome = await holds.BeginRun("t").WaitAsync(UiWorkKind.TipSlice);

        Assert.Equal(UiWaitOutcome.Failed, outcome);
        Assert.Contains(_diagnostics.Events, e => e.StartsWith("ui-hold-failed|", StringComparison.Ordinal));
        Assert.Null(holds.Describe(UiWorkKind.TipSlice));
    }

    [Fact]
    public async Task Cancellation_ends_the_wait()
    {
        var holds = Build(TipBusy, maxWaitMs: 10000);
        using var cts = new CancellationTokenSource(30);

        UiWaitOutcome outcome = await holds.BeginRun("t").WaitAsync(UiWorkKind.SilentNames, cts.Token);

        Assert.Equal(UiWaitOutcome.Cancelled, outcome);
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
            lock (_gate) { _events.Add(eventName + "|" + (details ?? string.Empty)); }
        }

        public void RecordException(string category, string eventName, Exception exception, string? details = null) { }
        public string GetRecentText() => string.Empty;
        public RuntimeDiagnosticsSnapshot CaptureSnapshot() => null!;
        public Task FlushAsync(string reason) => Task.CompletedTask;
        public Task<string> ExportReportAsync() => Task.FromResult(string.Empty);
        public Task<string> SaveReportToLocalFolderAsync() => Task.FromResult(string.Empty);
        public Task ClearAsync() => Task.CompletedTask;
    }
}
