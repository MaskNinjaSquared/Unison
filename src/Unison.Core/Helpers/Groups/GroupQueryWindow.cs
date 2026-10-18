using System;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// How often a participating-groups listing may run, and which rows still need their own
    /// metadata query.
    /// </summary>
    /// <remarks>
    /// Several callers ask for the listing and they overlap. Each pass is one listing plus up to
    /// twenty-five metadata queries, and two of them at once were enough to stall everything else
    /// waiting on the socket. The list does not change by the second, so a pass that got an answer
    /// holds the next one off for <see cref="Reuse"/>. A pass that timed out must not buy the full
    /// reuse window (groups stay nameless), but it does get a short <see cref="FailureBackoff"/> so
    /// overlapping callers cannot stack another 60s IQ while the socket is still recovering.
    /// In-flight overlap is also gated in GroupFacade (single-flight).
    /// </remarks>
    public sealed class GroupQueryWindow
    {
        public static readonly TimeSpan Reuse = TimeSpan.FromMinutes(2);

        /// <summary>Quiet after a listing that did not answer (timeout / null), before another pass.</summary>
        public static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(45);

        private DateTime _suppressUntilUtc = DateTime.MinValue;
        private DateTime _lastAnsweredUtc = DateTime.MinValue;

        public bool ShouldSkip(bool force, DateTime nowUtc)
        {
            if (force)
            {
                return false;
            }

            return nowUtc < _suppressUntilUtc;
        }

        public TimeSpan Since(DateTime nowUtc)
        {
            return nowUtc - _lastAnsweredUtc;
        }

        /// <summary>Call when the participating-groups listing came back, and only then.</summary>
        public void Arm(DateTime nowUtc)
        {
            _lastAnsweredUtc = nowUtc;
            _suppressUntilUtc = nowUtc + Reuse;
        }

        /// <summary>Call when the listing timed out or returned null — short cooldown only.</summary>
        public void ArmFailure(DateTime nowUtc)
        {
            DateTime until = nowUtc + FailureBackoff;
            if (until > _suppressUntilUtc)
            {
                _suppressUntilUtc = until;
            }
        }
    }

    /// <summary>Which chat rows the per-group metadata fallback still has to ask about.</summary>
    public static class GroupQuery
    {
        private const string GroupServer = "@g.us";

        public static bool NeedsMetadata(bool isGroup, string jid, bool hasMeaningfulName)
        {
            if (hasMeaningfulName)
            {
                return false;
            }

            if (isGroup)
            {
                return true;
            }

            return !string.IsNullOrEmpty(jid) &&
                   jid.EndsWith(GroupServer, StringComparison.OrdinalIgnoreCase);
        }
    }
}
