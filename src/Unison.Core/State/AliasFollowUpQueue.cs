// =============================================================================
// AliasFollowUpQueue
//
// What a burst of new alias pairs leaves owed until the follow-up pass runs:
// the phone addresses whose rows should retry their avatar, and which source
// asked last. One pass covers the whole burst, and only one runs at a time.
//
// Same seam as PersistScheduler: this answers and the host runs the debounce.
// Extracted from WhatsAppService, where the running flag sat outside the lock
// that guarded the rest and the source was re-read without it.
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Core.State
{
    /// <summary>Everything one follow-up pass took from the queue.</summary>
    public sealed class AliasFollowUpBatch
    {
        internal AliasFollowUpBatch(string source, IReadOnlyList<string> avatarTargets)
        {
            Source = source;
            AvatarTargets = avatarTargets;
        }

        public string Source { get; }

        /// <summary>Phone addresses that gained a pair, in the order they did, each once.</summary>
        public IReadOnlyList<string> AvatarTargets { get; }
    }

    public sealed class AliasFollowUpQueue
    {
        private const string DefaultSource = "alias";

        private readonly object _sync = new object();
        private readonly List<string> _targets = new List<string>();
        private readonly HashSet<string> _targetSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _pendingSource;
        private bool _running;

        /// <summary>The latest source asked for since the last pass began; null when none.</summary>
        public string PendingSource
        {
            get { lock (_sync) { return _pendingSource; } }
        }

        public void NoteAvatarTarget(string pnJid)
        {
            if (string.IsNullOrWhiteSpace(pnJid))
            {
                return;
            }

            lock (_sync)
            {
                AddTarget(pnJid);
            }
        }

        /// <summary>Records who asked. The host arms or restarts its debounce.</summary>
        public void Request(string source)
        {
            lock (_sync)
            {
                _pendingSource = source;
            }
        }

        /// <summary>
        /// The debounce elapsed. Takes everything owed and marks a pass running, or refuses
        /// when one already is; what was owed stays for the next pass, which the host re-arms.
        /// </summary>
        public bool TryBegin(out AliasFollowUpBatch batch)
        {
            lock (_sync)
            {
                if (_running)
                {
                    batch = null;
                    return false;
                }

                _running = true;
                batch = new AliasFollowUpBatch(_pendingSource ?? DefaultSource, _targets.ToArray());
                _pendingSource = null;
                _targets.Clear();
                _targetSet.Clear();
                return true;
            }
        }

        public void End()
        {
            lock (_sync)
            {
                _running = false;
            }
        }

        /// <summary>
        /// Puts a failed pass's avatar targets back, after anything noted since.
        /// </summary>
        /// <remarks>
        /// <see cref="TryBegin"/> clears the targets before the pass runs, so a pass that threw
        /// used to lose them and those rows kept their missing picture until the next pair for
        /// the same contact. Same contract as <see cref="PersistScheduler.Restore"/>.
        /// </remarks>
        public void Restore(AliasFollowUpBatch batch)
        {
            if (batch?.AvatarTargets == null)
            {
                return;
            }

            lock (_sync)
            {
                foreach (string target in batch.AvatarTargets)
                {
                    AddTarget(target);
                }
            }
        }

        private void AddTarget(string pnJid)
        {
            if (_targetSet.Add(pnJid))
            {
                _targets.Add(pnJid);
            }
        }
    }
}
