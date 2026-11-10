// =============================================================================
// SessionOpenOnDemandCrashLatch
//
// LocalSettings I/O for the session-open OnDemand pending JID list. Pure list
// math stays on SessionOpenOnDemandPendingList.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Helpers.History;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class SessionOpenOnDemandCrashLatch
    {
        private readonly ILocalSettings _settings;
        private readonly object _gate = new object();

        public SessionOpenOnDemandCrashLatch(ILocalSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public void Arm(string jid)
        {
            string key = Canonical(jid);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            lock (_gate)
            {
                List<string> list = ReadUnlocked();
                if (!SessionOpenOnDemandPendingList.TryAdd(
                        list,
                        key,
                        SessionOpenOnDemandPendingList.Cap))
                {
                    return;
                }

                WriteUnlocked(list);
                Debug.WriteLine(
                    "[SessionOpenOnDemandCrashLatch] Armed " + key +
                    " count=" + list.Count);
            }
        }

        public void Disarm(IReadOnlyList<string> jids)
        {
            if (jids == null || jids.Count == 0)
            {
                return;
            }

            var keys = new List<string>(jids.Count);
            for (int i = 0; i < jids.Count; i++)
            {
                string key = Canonical(jids[i]);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    keys.Add(key);
                }
            }

            if (keys.Count == 0)
            {
                return;
            }

            lock (_gate)
            {
                List<string> list = ReadUnlocked();
                if (!SessionOpenOnDemandPendingList.RemoveAny(list, keys))
                {
                    return;
                }

                WriteUnlocked(list);
                Debug.WriteLine(
                    "[SessionOpenOnDemandCrashLatch] Disarmed count=" + keys.Count +
                    " remaining=" + list.Count);
            }
        }

        /// <summary>Reads and clears the latch in one step (consume path).</summary>
        public IReadOnlyList<string> TakeAll()
        {
            lock (_gate)
            {
                List<string> list = ReadUnlocked();
                if (list.Count == 0)
                {
                    return Array.Empty<string>();
                }

                WriteUnlocked(new List<string>());
                return list;
            }
        }

        public IReadOnlyList<string> Peek()
        {
            lock (_gate)
            {
                return ReadUnlocked();
            }
        }

        private List<string> ReadUnlocked()
        {
            try
            {
                return SessionOpenOnDemandPendingList.Parse(
                    _settings.Get<string>(LocalSettingsConstants.SessionOpenOnDemandPendingJids));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[SessionOpenOnDemandCrashLatch] Read failed: " + ex.Message);
                return new List<string>();
            }
        }

        private void WriteUnlocked(List<string> jids)
        {
            try
            {
                _settings.Set(
                    LocalSettingsConstants.SessionOpenOnDemandPendingJids,
                    SessionOpenOnDemandPendingList.Format(jids));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[SessionOpenOnDemandCrashLatch] Write failed: " + ex.Message);
            }
        }

        private static string Canonical(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            return JidHelper.Normalize(jid);
        }
    }
}
