// =============================================================================
// Tests for StartupPhaseTrace — activeMs pauses while the window is hidden.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class StartupPhaseTraceTests
    {
        [Fact]
        public void End_journal_includes_activeMs_separate_from_wall_clock()
        {
            var visibility = new FakeVisibility(visible: true);
            var diagnostics = new FakeDiagnostics();
            var memory = new FakeMemory();

            using (var trace = new StartupPhaseTrace("post-replay-names", memory, diagnostics, visibility))
            {
                Thread.Sleep(30);
                visibility.SetVisible(false);
                Thread.Sleep(80);
                visibility.SetVisible(true);
                Thread.Sleep(20);
            }

            string end = Assert.Single(diagnostics.Lines, line => line.StartsWith("post-replay-names:end|"));
            Assert.Contains("activeMs=", end);
            Assert.Contains("elapsedMs=", end);

            long elapsedMs = ReadField(end, "elapsedMs");
            long activeMs = ReadField(end, "activeMs");
            Assert.True(elapsedMs >= activeMs);
            Assert.True(activeMs < elapsedMs);
            Assert.True(activeMs >= 20);
        }

        private static long ReadField(string line, string name)
        {
            string token = name + "=";
            int start = line.IndexOf(token, StringComparison.Ordinal);
            Assert.True(start >= 0);
            start += token.Length;
            int end = line.IndexOf(';', start);
            string raw = end < 0 ? line.Substring(start) : line.Substring(start, end - start);
            return long.Parse(raw);
        }

        private sealed class FakeVisibility : IAppVisibility
        {
            private bool _visible;

            public FakeVisibility(bool visible)
            {
                _visible = visible;
            }

            public bool IsWindowVisible => _visible;

            public event EventHandler Changed;

            public void SetVisible(bool visible)
            {
                if (_visible == visible)
                {
                    return;
                }

                _visible = visible;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        private sealed class FakeMemory : IAppMemoryInfo
        {
            public string UsageLevel => "Low";
            public ulong UsageBytes => 0;
            public ulong LimitBytes => 0;
            public bool IsLow => true;
        }

        private sealed class FakeDiagnostics : IRuntimeDiagnostics
        {
            public List<string> Lines { get; } = new List<string>();

            public bool IsVerboseLoggingEnabled => false;

            public void SetVerboseLogging(bool enabled, string source)
            {
            }

            public void Start()
            {
            }

            public void StartHealthSampling()
            {
            }

            public void Write(string category, string eventName, string details = null) =>
                Lines.Add(eventName + "|" + (details ?? string.Empty));

            public void RecordException(
                string category,
                string eventName,
                Exception exception,
                string details = null)
            {
            }

            public string GetRecentText() => string.Empty;

            public RuntimeDiagnosticsSnapshot CaptureSnapshot() => null;

            public Task FlushAsync(string reason) => Task.CompletedTask;

            public Task<string> ExportReportAsync() => Task.FromResult(string.Empty);

            public Task<string> SaveReportToLocalFolderAsync() => Task.FromResult(string.Empty);

            public Task ClearAsync() => Task.CompletedTask;
        }
    }
}
