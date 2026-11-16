using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Uwp.Client;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Unison.WhatsAppClient.Messages;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// Binds one monitored socket into <see cref="IConnectionHealthMonitorHost"/> for
    /// <see cref="ConnectionHealthMonitor"/>.
    /// </summary>
    internal sealed class ConnectionHealthSocketHost :
        IConnectionHealthMonitorHost,
        IForceDisconnectMonitoredSocketHost
    {
        private readonly IConnectionHealthOwnerPort _owner;
        private readonly IWhatsAppSocket _socket;
        private readonly ConnectionSessionFlags _flags;
        private readonly IncomingMessagePump _incomingPump;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly IUsyncGate _usyncGate;

        public ConnectionHealthSocketHost(
            IConnectionHealthOwnerPort owner,
            IWhatsAppSocket socket,
            ConnectionSessionFlags flags,
            IncomingMessagePump incomingPump,
            IRuntimeDiagnostics diagnostics,
            IUsyncGate usyncGate)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _flags = flags ?? throw new ArgumentNullException(nameof(flags));
            _incomingPump = incomingPump ?? throw new ArgumentNullException(nameof(incomingPump));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _usyncGate = usyncGate;
        }

        public bool ShouldAbortLoop =>
            _flags.SuppressReconnect || _flags.FatalSessionEnded;

        public bool IsMonitoredSocketCurrent =>
            ReferenceEquals(_owner.CurrentSocket, _socket);

        public bool IsMonitoredSocketConnected =>
            _socket.IsConnected && _socket.IsHandshakeComplete;

        public Task DelayCheckIntervalAsync(CancellationToken token) =>
            Task.Delay(ConnectionHealthPolicy.CheckInterval, token);

        public void RestartIncomingPumpIfStalled()
        {
            if (!_incomingPump.IsStalled(ConnectionHealthPolicy.IncomingPumpStallLimit))
            {
                return;
            }

            _diagnostics.Write(
                "messages",
                "incoming-pump-health-restart",
                "stage=" + _incomingPump.Stage);
            _incomingPump.Reset("health-stall", requeueCurrent: true);
            _incomingPump.RestartIfNeeded();
        }

        public bool IsNodeProcessingStalled =>
            _socket.HasStalledNodeProcessing(ConnectionHealthPolicy.NodeProcessingStallLimit);

        public bool HasFreshBackgroundConnection =>
            _socket.HasFreshConnection(ConnectionHealthProfile.Background.FreshnessLimit);

        public bool IsUsyncGateHeld =>
            _usyncGate != null && _usyncGate.IsHeld;

        public bool IsHistoryCatchUpActive => _owner.IsHistoryCatchUpActive;

        public Task<bool> ProbeBackgroundAsync() =>
            _socket.ProbeConnectionAsync(ConnectionHealthProfile.Background.ProbeTimeoutMs);

        public int QueuedNodeProcessingCount => _socket.QueuedNodeProcessingCount;

        public int PendingQueryCount => _socket.PendingQueryCount;

        public void ForceDisconnectMonitoredSocket() =>
            ForceDisconnectMonitoredSocketDispatch.Run(this);

        bool IForceDisconnectMonitoredSocketHost.IsMonitoredSocketCurrent =>
            ReferenceEquals(_owner.CurrentSocket, _socket);

        void IForceDisconnectMonitoredSocketHost.DisconnectMonitoredSocketBestEffort()
        {
            try
            {
                _socket.Disconnect();
            }
            catch
            {
            }
        }

        void IForceDisconnectMonitoredSocketHost.ClearCurrentSocketReferenceIfStillMonitored() =>
            _owner.ClearCurrentSocketIf(_socket);

        public void RequestSoftReconnect(string reason) =>
            _owner.RequestSoftReconnect(reason);

        public void WriteHealthJournal(string eventName, string detail) =>
            _diagnostics.Write("connection", eventName, detail);

        public void LogHealth(string message) =>
            Debug.WriteLine("[WhatsAppService] " + message);

        public void RecordHealthException(Exception error) =>
            _diagnostics.RecordException(
                "connection",
                "health-monitor-failed",
                error);

        public string TryGetExplicitLogoutStreamCode(Exception error)
        {
            string fatalCode = FatalStreamCodeFromException.TryRead(error);
            return ExplicitLogoutStreamCode.Matches(fatalCode) ? fatalCode : null;
        }

        public void LatchFatalAndReportStream(string code) =>
            _owner.LatchFatalAndReportStream(code);
    }
}
