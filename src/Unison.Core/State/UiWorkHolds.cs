// =============================================================================
// UiWorkHolds
//
// The waiting half of UiWorkHoldPolicy: polls the gates for one kind of UI work
// until they clear or the kind's budget runs out, and journals why it waited.
// The host supplies the gate snapshot (App.ConfigureServices); this type owns
// the poll, the Stopwatch ceiling and the per-run sticky timeouts.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers.Ui;

namespace Unison.Core.State
{
    public sealed class UiWorkHolds : IUiWorkHolds
    {
        private const string Category = "ui-hold";
        private const int OngoingReportMs = 2000;

        private readonly Func<UiHoldSnapshot> _read;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly Func<UiWorkKind, UiWaitBudget> _budgets;

        /// <param name="budgets">Test seam; production uses <see cref="UiWorkHoldPolicy.BudgetFor"/>.</param>
        public UiWorkHolds(
            Func<UiHoldSnapshot> read,
            IRuntimeDiagnostics diagnostics = null,
            Func<UiWorkKind, UiWaitBudget> budgets = null)
        {
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _diagnostics = diagnostics;
            _budgets = budgets ?? UiWorkHoldPolicy.BudgetFor;
        }

        public string Describe(UiWorkKind kind)
        {
            string gate;
            Exception error;
            TryDescribe(kind, out gate, out error);
            return gate;
        }

        public IUiWaitRun BeginRun(string reason) => new Run(this, string.IsNullOrWhiteSpace(reason) ? "unknown" : reason);

        private bool TryDescribe(UiWorkKind kind, out string gate, out Exception error)
        {
            try
            {
                gate = UiWorkHoldPolicy.Describe(kind, _read());
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                gate = null;
                error = ex;
                return false;
            }
        }

        private void Write(string eventName, string details)
        {
            _diagnostics?.Write(Category, eventName, details);
        }

        private sealed class Run : IUiWaitRun
        {
            private readonly UiWorkHolds _owner;
            private readonly string _reason;
            private readonly HashSet<string> _timedOut = new HashSet<string>(StringComparer.Ordinal);

            public Run(UiWorkHolds owner, string reason)
            {
                _owner = owner;
                _reason = reason;
            }

            public async Task<UiWaitOutcome> WaitAsync(UiWorkKind kind, CancellationToken cancellationToken = default(CancellationToken))
            {
                string label = "kind=" + kind + "; reason=" + _reason;
                string gate;
                Exception error;
                if (!_owner.TryDescribe(kind, out gate, out error))
                {
                    _owner.Write("ui-hold-failed", label + "; error=" + error.GetType().Name + ": " + error.Message);
                    return UiWaitOutcome.Failed;
                }

                if (gate == null)
                {
                    return UiWaitOutcome.Clear;
                }

                lock (_timedOut)
                {
                    if (_timedOut.Contains(gate))
                    {
                        _owner.Write("ui-hold-sticky", label + "; gate=" + gate);
                        return UiWaitOutcome.Skipped;
                    }
                }

                _owner.Write("ui-hold-paused", label + "; gate=" + gate);
                UiWaitBudget budget = _owner._budgets(kind);
                long budgetMs = (long)budget.MaxWait.TotalMilliseconds;
                int pollMs = Math.Max(1, (int)budget.Poll.TotalMilliseconds);
                long nextOngoingMs = OngoingReportMs;
                var waited = Stopwatch.StartNew();
                while (gate != null)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return UiWaitOutcome.Cancelled;
                    }

                    long waitedMs = waited.ElapsedMilliseconds;
                    if (waitedMs >= budgetMs)
                    {
                        lock (_timedOut)
                        {
                            _timedOut.Add(gate);
                        }

                        _owner.Write("ui-hold-timeout", label + "; gate=" + gate + "; budgetMs=" + budgetMs);
                        return UiWaitOutcome.TimedOut;
                    }

                    if (waitedMs >= nextOngoingMs)
                    {
                        nextOngoingMs = waitedMs + OngoingReportMs;
                        _owner.Write("ui-hold-ongoing", label + "; gate=" + gate + "; waitedMs=" + waitedMs);
                    }

                    try
                    {
                        await Task.Delay(pollMs, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return UiWaitOutcome.Cancelled;
                    }

                    if (!_owner.TryDescribe(kind, out gate, out error))
                    {
                        _owner.Write("ui-hold-failed", label + "; error=" + error.GetType().Name + ": " + error.Message);
                        return UiWaitOutcome.Failed;
                    }
                }

                _owner.Write("ui-hold-released", label + "; waitedMs=" + waited.ElapsedMilliseconds);
                return UiWaitOutcome.Clear;
            }
        }
    }
}
