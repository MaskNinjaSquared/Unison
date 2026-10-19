using System;
using System.Diagnostics;
using Unison.Core.Contracts;
using Windows.System.Display;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// Explicit Hold/Release tied to the chat-list first-sync hold (not sqlite-finalized alone).
    /// Hold covers connect→history chunks; Release runs immediately before <c>x:Load</c>.
    /// </summary>
    public sealed class DisplayKeepAwakeService : IDisplayKeepAwakeService
    {
        private readonly AttachedUiDispatcher _ui;
        private readonly object _gate = new object();

        private DisplayRequest _displayRequest;
        private bool _held;
        private bool _wantHeld;
        private string _lastReason;

        public DisplayKeepAwakeService(AttachedUiDispatcher ui)
        {
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        }

        public bool IsHeld
        {
            get { lock (_gate) { return _held; } }
        }

        public void Hold(string reason)
        {
            _lastReason = reason ?? "hold";
            SetWantHeld(true);
            try
            {
                RuntimeDiagnosticsService.Instance.Write(
                    "display-keep-awake",
                    "hold",
                    "reason=" + _lastReason);
                _ = RuntimeDiagnosticsService.Instance.FlushAsync("display:hold");
            }
            catch
            {
            }
        }

        public void Release(string reason)
        {
            _lastReason = reason ?? "release";
            SetWantHeld(false);
            try
            {
                RuntimeDiagnosticsService.Instance.Write(
                    "display-keep-awake",
                    "release",
                    "reason=" + _lastReason);
                _ = RuntimeDiagnosticsService.Instance.FlushAsync("display:release");
            }
            catch
            {
            }
        }

        private void SetWantHeld(bool wantHeld)
        {
            lock (_gate)
            {
                if (_wantHeld == wantHeld)
                {
                    return;
                }

                _wantHeld = wantHeld;
            }

            _ = _ui.RunAsync(SyncOnUi);
        }

        private void SyncOnUi()
        {
            try
            {
                lock (_gate)
                {
                    if (_wantHeld == _held)
                    {
                        return;
                    }

                    if (_wantHeld)
                    {
                        if (_displayRequest == null)
                        {
                            _displayRequest = new DisplayRequest();
                        }

                        _displayRequest.RequestActive();
                        _held = true;
                        Debug.WriteLine(
                            "[DisplayKeepAwakeService] DisplayRequest active (" +
                            (_lastReason ?? string.Empty) + ").");
                    }
                    else
                    {
                        if (_displayRequest != null && _held)
                        {
                            _displayRequest.RequestRelease();
                        }

                        _held = false;
                        Debug.WriteLine(
                            "[DisplayKeepAwakeService] DisplayRequest released (" +
                            (_lastReason ?? string.Empty) + ").");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[DisplayKeepAwakeService] Sync failed: " + ex.Message);
            }
        }
    }
}
