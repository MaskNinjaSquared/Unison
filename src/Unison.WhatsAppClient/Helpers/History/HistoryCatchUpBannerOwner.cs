// =============================================================================
// HistoryCatchUpBannerOwner
//
// Owns IBeginHistoryCatchUpBannerHost + IEndHistoryCatchUpBannerHost. Sync
// channel latch / journal / resolved-names apply stay as ctor ports.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class HistoryCatchUpBannerOwner :
        IBeginHistoryCatchUpBannerHost,
        IEndHistoryCatchUpBannerHost
    {
        private readonly Action _latchBanner;
        private readonly Action<string> _noteProgress;
        private readonly Action<string> _writeBegin;
        private readonly Func<bool> _tryEndBanner;
        private readonly Action<string> _writeEnd;
        private readonly Action _applyResolvedNames;

        public HistoryCatchUpBannerOwner(
            Action latchBanner,
            Action<string> noteProgress,
            Action<string> writeBegin,
            Func<bool> tryEndBanner,
            Action<string> writeEnd,
            Action applyResolvedNames)
        {
            _latchBanner = latchBanner
                ?? throw new ArgumentNullException(nameof(latchBanner));
            _noteProgress = noteProgress
                ?? throw new ArgumentNullException(nameof(noteProgress));
            _writeBegin = writeBegin
                ?? throw new ArgumentNullException(nameof(writeBegin));
            _tryEndBanner = tryEndBanner
                ?? throw new ArgumentNullException(nameof(tryEndBanner));
            _writeEnd = writeEnd
                ?? throw new ArgumentNullException(nameof(writeEnd));
            _applyResolvedNames = applyResolvedNames
                ?? throw new ArgumentNullException(nameof(applyResolvedNames));
        }

        public void Begin(string reason) =>
            BeginHistoryCatchUpBannerDispatch.Run(this, reason);

        public void End(string reason) =>
            EndHistoryCatchUpBannerDispatch.Run(this, reason);

        void IBeginHistoryCatchUpBannerHost.LatchHistoryCatchUpBanner() =>
            _latchBanner();

        void IBeginHistoryCatchUpBannerHost.NoteFullHistoryCatchUpProgress(string reason) =>
            _noteProgress(reason);

        void IBeginHistoryCatchUpBannerHost.WriteCatchUpBannerBegin(string reason) =>
            _writeBegin(reason);

        bool IEndHistoryCatchUpBannerHost.TryEndHistoryCatchUpBanner() =>
            _tryEndBanner();

        void IEndHistoryCatchUpBannerHost.WriteCatchUpBannerEnd(string reason) =>
            _writeEnd(reason);

        void IEndHistoryCatchUpBannerHost.FireAndForgetApplyResolvedNamesToChatList() =>
            _applyResolvedNames();
    }
}
