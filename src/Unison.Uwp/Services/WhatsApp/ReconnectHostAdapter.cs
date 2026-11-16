using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Core.Contracts;
using Unison.Uwp.Client;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Connection;
using Unison.WhatsAppClient.Helpers;
using Unison.WhatsAppClient.Messages;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// Host I/O for soft reconnect, the auto-reconnect loop and the pairing stage-2 trigger.
    /// Owns the <see cref="SoftReconnectCoordinator"/>; policy stays in the WhatsAppClient dispatches.
    /// </summary>
    internal sealed class ReconnectHostAdapter :
        ISoftReconnectIoHost,
        IAutoReconnectLoopHost,
        ITryStartPairingStage2ReconnectHost
    {
        private readonly Func<IReconnectLifecyclePort> _resolveLifecycle;
        private readonly ConnectionSessionFlags _flags;
        private readonly ConnectionSlot<AuthState> _authSlot;
        private readonly IIsCurrentSocketHealthyHost _currentSocketHealth;
        private readonly ConnectionEventHub _events;
        private readonly IRuntimeDiagnostics _diagnostics;

        /// <param name="resolveLifecycle">
        /// Lazy: the connection owner itself takes this adapter's coordinator in its ctor.
        /// </param>
        public ReconnectHostAdapter(
            Func<IReconnectLifecyclePort> resolveLifecycle,
            ConnectionSessionFlags flags,
            ConnectionSlot<AuthState> authSlot,
            IIsCurrentSocketHealthyHost currentSocketHealth,
            ConnectionEventHub events,
            IRuntimeDiagnostics diagnostics)
        {
            _resolveLifecycle = resolveLifecycle ?? throw new ArgumentNullException(nameof(resolveLifecycle));
            _flags = flags ?? throw new ArgumentNullException(nameof(flags));
            _authSlot = authSlot ?? throw new ArgumentNullException(nameof(authSlot));
            _currentSocketHealth = currentSocketHealth ?? throw new ArgumentNullException(nameof(currentSocketHealth));
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            Coordinator = new SoftReconnectCoordinator(this);
        }

        public SoftReconnectCoordinator Coordinator { get; }

        private IReconnectLifecyclePort Lifecycle => _resolveLifecycle();

        public bool SuppressReconnect => _flags.SuppressReconnect;

        public bool FatalSessionEnded => _flags.FatalSessionEnded;

        public bool PairingRestartPending => _flags.PairingRestartPending;

        public bool IsAuthRegistered =>
            _authSlot.Current != null && _authSlot.Current.Registered;

        // --- ISoftReconnectIoHost --------------------------------------------

        bool ISoftReconnectIoHost.IsAuthPresent => _authSlot.Current != null;

        bool ISoftReconnectIoHost.IsConnected => Lifecycle.IsConnected;

        void ISoftReconnectIoHost.LogAutoReconnectScheduled(string reason) =>
            Debug.WriteLine("[WhatsAppService] Scheduling persistent reconnect: " + reason);

        void ISoftReconnectIoHost.WriteAutoReconnectScheduled(string reason) =>
            _diagnostics.Write("connection", "reconnect-scheduled", reason);

        void ISoftReconnectIoHost.TracePairingSkippedForPending(string reason) =>
            Lifecycle.PairingTrace(
                "ScheduleAutoReconnect SKIPPED (pairingRestartPending) reason=" +
                (reason ?? string.Empty));

        void ISoftReconnectIoHost.TraceScheduleAutoReconnectReason(string reason)
        {
            try
            {
                if (SessionLogger.Instance.PairingTraceActive)
                {
                    SessionLogger.Instance.WriteAlways(
                        "[Pairing/WA] ScheduleAutoReconnect reason=" + (reason ?? string.Empty));
                }
            }
            catch
            {
            }
        }

        void ISoftReconnectIoHost.FireAndForgetAutoReconnect(Func<Task> work) =>
            _ = Task.Run(work);

        Task ISoftReconnectIoHost.RunAutoReconnectLoopAsync(string reason) =>
            AutoReconnectLoopDispatch.RunAsync(this, reason);

        void ISoftReconnectIoHost.TryStartPairingStage2Reconnect(string reason) =>
            TryStartPairingStage2ReconnectDispatch.Run(this, reason);

        // --- IAutoReconnectLoopHost ------------------------------------------

        bool IAutoReconnectLoopHost.IsAuthStatePresent => _authSlot.Current != null;

        Task<bool> IAutoReconnectLoopHost.IsCurrentSocketHealthyAsync() =>
            IsCurrentSocketHealthyDispatch.RunAsync(_currentSocketHealth);

        Task IAutoReconnectLoopHost.InitializeAsync() => Lifecycle.InitializeAsync();

        Task IAutoReconnectLoopHost.ConnectAsync() => Lifecycle.ConnectAsync();

        TimeSpan IAutoReconnectLoopHost.ReconnectDelayForAttempt(int attempt) =>
            ConnectionHealthPolicy.ReconnectDelay(attempt);

        void IAutoReconnectLoopHost.PublishReconnectingStatus() =>
            Lifecycle.PublishReconnectingStatus();

        void IAutoReconnectLoopHost.LogAutoReconnect(string message) =>
            Debug.WriteLine("[WhatsAppService] " + message);

        void IAutoReconnectLoopHost.TracePairing(string detail) => Lifecycle.PairingTrace(detail);

        void IAutoReconnectLoopHost.RaiseReconnectLoopError(Exception error) =>
            _events.RaiseError(error);

        // --- ITryStartPairingStage2ReconnectHost -----------------------------

        void ITryStartPairingStage2ReconnectHost.TracePairingStage2(string message) =>
            Lifecycle.PairingTrace(message);

        /// <summary>
        /// Marks the 515 pairing-restart window. Must run as early as possible — on Mobile the
        /// transport often closes with 1006 BEFORE the "restart" status is published, and that
        /// premature close must not schedule the generic AutoReconnect loop.
        /// </summary>
        void ITryStartPairingStage2ReconnectHost.MarkPairingRestartPending(string reason) =>
            Lifecycle.MarkPairingRestartPending(reason);

        bool ITryStartPairingStage2ReconnectHost.TryClaimReconnectSlot() =>
            Coordinator.TryClaimReconnectSlot();

        void ITryStartPairingStage2ReconnectHost.StopConnectionHealthMonitor(string reason) =>
            Lifecycle.StopConnectionHealthMonitor(reason);

        void ITryStartPairingStage2ReconnectHost.FireAndForgetPairingStage2Reconnect() =>
            Lifecycle.FireAndForgetPairingStage2Reconnect();
    }
}
