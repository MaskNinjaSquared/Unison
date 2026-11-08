// =============================================================================
// DebouncedUiRebuild
//
// Coalesces bursty catalog / list rebuilds into one UI hop after a quiet window.
// Same 400ms idea as ChatDetailInfo / NewConversation ScheduleRebuild.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// One scheduled rebuild per quiet window. Call <see cref="Schedule"/> from event
    /// storms; keep open/search paths on an immediate rebuild outside this type.
    /// </summary>
    public sealed class DebouncedUiRebuild
    {
        public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(400);

        private readonly TimeSpan _delay;
        private readonly Func<bool> _canRun;
        private readonly Action _rebuild;
        private readonly IDispatcher _dispatcher;
        private readonly string _logTag;
        private bool _scheduled;

        public DebouncedUiRebuild(
            Func<bool> canRun,
            Action rebuild,
            IDispatcher dispatcher = null,
            TimeSpan? delay = null,
            string logTag = null)
        {
            _canRun = canRun ?? (() => true);
            _rebuild = rebuild ?? throw new ArgumentNullException(nameof(rebuild));
            _dispatcher = dispatcher;
            _delay = delay ?? DefaultDelay;
            _logTag = string.IsNullOrWhiteSpace(logTag) ? "DebouncedUiRebuild" : logTag;
        }

        /// <summary>Arm a single rebuild after <see cref="DefaultDelay"/> (or the ctor delay).</summary>
        public void Schedule()
        {
            if (_scheduled || !_canRun())
            {
                return;
            }

            _scheduled = true;
            _ = RunScheduledAsync();
        }

        /// <summary>
        /// Drops the coalesce flag so a closed overlay / detach does not keep the window
        /// armed. An in-flight delay still ends and re-checks <c>canRun</c>.
        /// </summary>
        public void Cancel()
        {
            _scheduled = false;
        }

        private async Task RunScheduledAsync()
        {
            try
            {
                try
                {
                    await Task.Delay(_delay).ConfigureAwait(false);
                }
                finally
                {
                    _scheduled = false;
                }

                if (!_canRun())
                {
                    return;
                }

                if (_dispatcher != null)
                {
                    await _dispatcher.RunAsync(_rebuild).ConfigureAwait(false);
                    return;
                }

                _rebuild();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[" + _logTag + "] Scheduled rebuild failed: " + ex.Message);
            }
        }
    }
}
