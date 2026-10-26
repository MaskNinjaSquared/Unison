// =============================================================================
// PublishInitialSyncProgressOwner
//
// Owns IPublishInitialSyncProgressHost. First-sync flag fields / channel /
// journal stay as ctor ports.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class PublishInitialSyncProgressOwner : IPublishInitialSyncProgressHost
    {
        private readonly Func<bool> _getAlreadyReleased;
        private readonly Action<bool> _setAlreadyReleased;
        private readonly Func<bool> _getHoldOwned;
        private readonly Action<bool> _setHoldOwned;
        private readonly Func<bool> _getReleaseDeferred;
        private readonly Action<bool> _setReleaseDeferred;
        private readonly Action<bool, bool, int, int, string> _writeJournal;
        private readonly Action<bool, bool, int, int, string> _publishChannel;
        private readonly Action _flushOnComplete;

        public PublishInitialSyncProgressOwner(
            Func<bool> getAlreadyReleased,
            Action<bool> setAlreadyReleased,
            Func<bool> getHoldOwned,
            Action<bool> setHoldOwned,
            Func<bool> getReleaseDeferred,
            Action<bool> setReleaseDeferred,
            Action<bool, bool, int, int, string> writeJournal,
            Action<bool, bool, int, int, string> publishChannel,
            Action flushOnComplete)
        {
            _getAlreadyReleased = getAlreadyReleased
                ?? throw new ArgumentNullException(nameof(getAlreadyReleased));
            _setAlreadyReleased = setAlreadyReleased
                ?? throw new ArgumentNullException(nameof(setAlreadyReleased));
            _getHoldOwned = getHoldOwned
                ?? throw new ArgumentNullException(nameof(getHoldOwned));
            _setHoldOwned = setHoldOwned
                ?? throw new ArgumentNullException(nameof(setHoldOwned));
            _getReleaseDeferred = getReleaseDeferred
                ?? throw new ArgumentNullException(nameof(getReleaseDeferred));
            _setReleaseDeferred = setReleaseDeferred
                ?? throw new ArgumentNullException(nameof(setReleaseDeferred));
            _writeJournal = writeJournal
                ?? throw new ArgumentNullException(nameof(writeJournal));
            _publishChannel = publishChannel
                ?? throw new ArgumentNullException(nameof(publishChannel));
            _flushOnComplete = flushOnComplete
                ?? throw new ArgumentNullException(nameof(flushOnComplete));
        }

        public void Run(
            bool active,
            bool completed,
            int processed,
            int total,
            string stage) =>
            PublishInitialSyncProgressDispatch.Run(
                this,
                active,
                completed,
                processed,
                total,
                stage);

        bool IPublishInitialSyncProgressHost.FirstSyncListAlreadyReleased
        {
            get => _getAlreadyReleased();
            set => _setAlreadyReleased(value);
        }

        bool IPublishInitialSyncProgressHost.FirstSyncListHoldOwned
        {
            get => _getHoldOwned();
            set => _setHoldOwned(value);
        }

        bool IPublishInitialSyncProgressHost.FirstSyncListReleaseDeferred
        {
            get => _getReleaseDeferred();
            set => _setReleaseDeferred(value);
        }

        void IPublishInitialSyncProgressHost.WriteInitialSyncProgressJournal(
            bool active,
            bool completed,
            int processed,
            int total,
            string stage) =>
            _writeJournal(active, completed, processed, total, stage);

        void IPublishInitialSyncProgressHost.PublishInitialSyncChannel(
            bool active,
            bool completed,
            int processed,
            int total,
            string stage) =>
            _publishChannel(active, completed, processed, total, stage);

        void IPublishInitialSyncProgressHost.FireAndForgetFlushDiagnosticsOnComplete() =>
            _flushOnComplete();
    }
}
