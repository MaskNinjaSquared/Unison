// =============================================================================
// PersistScheduler
//
// Whether the catalogue needs saving, and whether now is the time.
//
// The second hand-rolled debounce in the client, and the one that matters: it
// decides when chat rows reach disk. Extracted from WhatsAppService, where the
// pending flag lived under one lock and the startup-suppression flag lived
// beside it under no lock at all.
//
// Same seam as PendingMessageQueue: this answers with an action and the host
// runs the timer. The Timer is platform and stays there; the question of
// whether a save is owed is not, and is what was untestable.
// =============================================================================
namespace Unison.Core.State
{
    /// <summary>What the caller should do after asking for a save.</summary>
    public enum PersistScheduleAction
    {
        /// <summary>Recorded as owed, but startup is still warming up. Do not arm anything.</summary>
        Deferred,

        /// <summary>Start or restart the debounce timer.</summary>
        ArmTimer
    }

    /// <summary>The outcome of lifting startup suppression.</summary>
    public enum PersistEnableResult
    {
        /// <summary>Suppression had already been lifted; nothing happened.</summary>
        AlreadyEnabled,

        /// <summary>Suppression lifted, and no save had been asked for while it was on.</summary>
        Lifted,

        /// <summary>Suppression lifted, and a save deferred during startup is now owed.</summary>
        LiftedWithDeferredSave
    }

    public sealed class PersistScheduler
    {
        private readonly object _sync = new object();

        private bool _pending;
        private bool _suppressedForStartup = true;

        /// <summary>A save is owed. Exposed for the diagnostics snapshot.</summary>
        public bool IsPending
        {
            get { lock (_sync) { return _pending; } }
        }

        public bool IsSuppressedForStartup
        {
            get { lock (_sync) { return _suppressedForStartup; } }
        }

        /// <summary>
        /// Records that the catalogue changed and says whether to arm the debounce.
        /// </summary>
        /// <remarks>
        /// During startup the request is remembered but nothing is armed. A first sync
        /// dirties the catalogue thousands of times, and each one used to restart a timer
        /// whose only job was to rewrite every row — during the minute the app has least
        /// to spare. <see cref="EnableAfterStartup"/> settles the debt once.
        /// </remarks>
        public PersistScheduleAction Request()
        {
            lock (_sync)
            {
                _pending = true;
                return _suppressedForStartup
                    ? PersistScheduleAction.Deferred
                    : PersistScheduleAction.ArmTimer;
            }
        }

        /// <summary>
        /// The debounce elapsed. True when the save is still owed and the caller should run
        /// it; false when someone else already did, which is the whole point of the flag.
        /// </summary>
        public bool TryBeginPersist()
        {
            lock (_sync)
            {
                if (!_pending)
                {
                    return false;
                }

                _pending = false;
                return true;
            }
        }

        /// <summary>
        /// Puts the save back on the books after an attempt failed.
        /// </summary>
        /// <remarks>
        /// <see cref="TryBeginPersist"/> clears the flag before the write runs, so a write that
        /// throws used to consume the debt and leave the change on disk stale until some
        /// unrelated edit happened to schedule another save. The message queue already restores
        /// its drain on failure; this is the same contract for the catalogue.
        /// </remarks>
        public void Restore()
        {
            lock (_sync)
            {
                _pending = true;
            }
        }

        /// <summary>
        /// Lifts startup suppression and reports whether a save deferred during startup is
        /// now owed.
        /// </summary>
        /// <remarks>
        /// Checking and lifting under one lock is deliberate: the flag used to be read and
        /// written outside the lock that guarded the pending flag beside it, so two callers
        /// lifting suppression at once could both decide they owed the deferred save.
        /// </remarks>
        public PersistEnableResult EnableAfterStartup()
        {
            lock (_sync)
            {
                if (!_suppressedForStartup)
                {
                    return PersistEnableResult.AlreadyEnabled;
                }

                _suppressedForStartup = false;
                return _pending
                    ? PersistEnableResult.LiftedWithDeferredSave
                    : PersistEnableResult.Lifted;
            }
        }

        /// <summary>Forgets the owed save. For shutdown, once the host has killed the timer.</summary>
        public void Reset()
        {
            lock (_sync)
            {
                _pending = false;
            }
        }
    }
}
