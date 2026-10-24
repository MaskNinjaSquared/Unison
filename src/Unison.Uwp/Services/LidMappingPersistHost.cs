// =============================================================================
// LidMappingPersistHost
//
// Host half of LID write-behind: arms the debounce timer and implements the
// history gate HistoryFacade releases when SQLite is free again.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Socket.Signal;

namespace Unison.Uwp.Services
{
    public sealed class LidMappingPersistHost : ILidMappingDiskPersistGate, IDisposable
    {
        private readonly LidMappingStore _store;
        private readonly object _timerGate = new object();
        private Timer _debounceTimer;
        private int _released;

        public LidMappingPersistHost(LidMappingStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _store.ArmDebounce = ArmDebounce;
        }

        public void ReleaseAfterHistory(string reason)
        {
            if (Interlocked.Exchange(ref _released, 1) == 1)
            {
                return;
            }

            Debug.WriteLine(
                "[LidMappingPersist] Gate released" +
                (string.IsNullOrEmpty(reason) ? string.Empty : ": " + reason));
            _ = ReleaseCoreAsync(reason);
        }

        public Task FlushAsync(string reason = null)
        {
            CancelDebounce();
            Debug.WriteLine(
                "[LidMappingPersist] Flush" +
                (string.IsNullOrEmpty(reason) ? string.Empty : ": " + reason));
            return _store.FlushPendingToDiskAsync();
        }

        public void ArmDebounce(int delayMs)
        {
            if (delayMs <= 0)
            {
                _ = _store.FlushPendingToDiskAsync();
                return;
            }

            lock (_timerGate)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = new Timer(
                    _ =>
                    {
                        CancelDebounce();
                        _ = SafeFlushAsync("debounce");
                    },
                    null,
                    delayMs,
                    Timeout.Infinite);
            }
        }

        public void Dispose()
        {
            CancelDebounce();
            _store.ArmDebounce = null;
        }

        private async Task ReleaseCoreAsync(string reason)
        {
            try
            {
                CancelDebounce();
                await _store.EnableDiskPersistAsync(reason).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[LidMappingPersist] Release flush failed: " + ex.Message);
            }
        }

        private async Task SafeFlushAsync(string reason)
        {
            try
            {
                await _store.FlushPendingToDiskAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[LidMappingPersist] " + reason + " flush failed: " + ex.Message);
            }
        }

        private void CancelDebounce()
        {
            lock (_timerGate)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = null;
            }
        }
    }
}
