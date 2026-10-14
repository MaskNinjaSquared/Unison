// =============================================================================
// HistoryOnDemandTimeout
//
// Hard deadline for an accepted history request the phone never answers. Armed
// once per request; when the window passes and the request is still current it
// is cleared and the caller gets a copy to log and decide on. A request answered
// in time yields null.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.Core.State
{
    public sealed class HistoryOnDemandTimeout
    {
        private readonly HistoryOnDemandRegistry _registry;
        private readonly TimeSpan _chatWindow;
        private readonly TimeSpan _fullWindow;
        private readonly Func<TimeSpan, Task> _delay;

        public HistoryOnDemandTimeout(
            HistoryOnDemandRegistry registry,
            TimeSpan chatWindow,
            TimeSpan fullWindow,
            Func<TimeSpan, Task> delay = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _chatWindow = chatWindow;
            _fullWindow = fullWindow;
            _delay = delay ?? Task.Delay;
        }

        public TimeSpan WindowFor(HistoryOnDemandRequest request) =>
            request.Kind == HistoryOnDemandKind.FullHistory ? _fullWindow : _chatWindow;

        /// <param name="expiry">Completes with the expired copy, or null when answered in time.</param>
        /// <returns>False when the request is missing, no longer current, or already armed.</returns>
        public bool TryArm(HistoryOnDemandRequest request, out Task<HistoryOnDemandRequest> expiry)
        {
            expiry = null;
            if (request == null || string.IsNullOrWhiteSpace(request.RequestId) ||
                !_registry.TryArmTimeout(request))
            {
                return false;
            }

            expiry = ExpireAfterWindowAsync(request);
            return true;
        }

        private async Task<HistoryOnDemandRequest> ExpireAfterWindowAsync(HistoryOnDemandRequest request)
        {
            await _delay(WindowFor(request)).ConfigureAwait(false);
            return _registry.TryExpire(request);
        }
    }
}
