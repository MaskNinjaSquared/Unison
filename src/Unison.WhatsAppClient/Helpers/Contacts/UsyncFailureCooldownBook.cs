// =============================================================================
// UsyncFailureCooldownBook
//
// Remembers JIDs whose usync IQ timed out or failed, so the next reconnect does
// not immediately re-fire the same batch and starve the socket again.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class UsyncFailureCooldownBook
    {
        private readonly Dictionary<string, DateTime> _failedUntilUtc =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private readonly TimeSpan _cooldown;
        private readonly object _gate = new object();

        public UsyncFailureCooldownBook(TimeSpan cooldown)
        {
            if (cooldown <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(cooldown));
            }

            _cooldown = cooldown;
        }

        public static UsyncFailureCooldownBook Default { get; } =
            new UsyncFailureCooldownBook(TimeSpan.FromMinutes(15));

        public TimeSpan Cooldown => _cooldown;

        public void NoteFailure(IEnumerable<string> jids, DateTime utcNow)
        {
            if (jids == null)
            {
                return;
            }

            DateTime until = utcNow + _cooldown;
            lock (_gate)
            {
                foreach (string jid in jids)
                {
                    if (string.IsNullOrWhiteSpace(jid))
                    {
                        continue;
                    }

                    _failedUntilUtc[jid.Trim()] = until;
                }
            }
        }

        public void NoteSuccess(IEnumerable<string> jids)
        {
            if (jids == null)
            {
                return;
            }

            lock (_gate)
            {
                foreach (string jid in jids)
                {
                    if (string.IsNullOrWhiteSpace(jid))
                    {
                        continue;
                    }

                    _failedUntilUtc.Remove(jid.Trim());
                }
            }
        }

        public bool ShouldSkip(string jid, DateTime utcNow)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return false;
            }

            lock (_gate)
            {
                DateTime until;
                if (!_failedUntilUtc.TryGetValue(jid.Trim(), out until))
                {
                    return false;
                }

                if (utcNow >= until)
                {
                    _failedUntilUtc.Remove(jid.Trim());
                    return false;
                }

                return true;
            }
        }

        public List<string> FilterReady(IEnumerable<string> jids, DateTime utcNow)
        {
            var ready = new List<string>();
            if (jids == null)
            {
                return ready;
            }

            foreach (string jid in jids)
            {
                if (string.IsNullOrWhiteSpace(jid))
                {
                    continue;
                }

                if (!ShouldSkip(jid, utcNow))
                {
                    ready.Add(jid);
                }
            }

            return ready;
        }
    }
}
