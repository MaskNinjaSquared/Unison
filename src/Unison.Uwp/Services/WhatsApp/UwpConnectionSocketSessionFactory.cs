// =============================================================================
// UwpConnectionSocketSessionFactory
//
// UWP adapter for IConnectionSocketSessionFactory: news SocketBridge, attaches
// remote façade hooks + SocketEventHandlerWiring, opens WinRT transports.
// Portable ConnectionConnectOpen never names SocketBridge or IWhatsAppSocket.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Socket.Signal;
using Unison.Uwp.Client;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Uwp.Services.WhatsApp
{
    public sealed class UwpConnectionSocketSessionFactory : IConnectionSocketSessionFactory
    {
        private readonly IWhatsAppTransportFactory _transports;
        private readonly ISystemInfoProvider _systemInfo;

        public UwpConnectionSocketSessionFactory(
            IWhatsAppTransportFactory transports,
            ISystemInfoProvider systemInfo)
        {
            _transports = transports ?? throw new ArgumentNullException(nameof(transports));
            _systemInfo = systemInfo ?? throw new ArgumentNullException(nameof(systemInfo));
        }

        public object CreateAndWire(ConnectionSocketSessionRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var owner = request.WireOwner as WhatsAppService;
            if (owner == null)
            {
                throw new ArgumentException(
                    "WireOwner must be WhatsAppService until the wire target is a portable host.",
                    nameof(request));
            }

            var bridge = new SocketBridge(
                request.AuthState,
                request.SharedKeyStore as FileKeyStore,
                request.ReuseLoadedKeyState,
                request.LidMappings as LidMappingStore);

            SocketBridgeRemoteHooks.Attach(
                bridge,
                owner.ChatsFacade,
                owner.ContactsFacade,
                owner.MessagesFacade,
                owner.Groups,
                owner.ResolveSentMessageForRetryAsync);

            SocketEventHandlerWiring.Attach(bridge, owner);

            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                "socket-rewrite-enabled",
                "transport=" + bridge.TransportName);

            return bridge;
        }

        public async Task<object> InitializeKeyStoreAsync(object session)
        {
            var socket = session as IWhatsAppSocket;
            if (socket == null)
            {
                return null;
            }

            await socket.InitializeKeyStoreAsync().ConfigureAwait(false);
            return socket.PersistentKeyStore;
        }

        public Task ConnectTransportAsync(object session)
        {
            var socket = session as IWhatsAppSocket;
            if (socket == null)
            {
                return Task.CompletedTask;
            }

            // Host builds WinRT transports; bridge only runs Noise over them (Init shape).
            var bridge = socket as SocketBridge;
            if (bridge == null)
            {
                throw new InvalidOperationException(
                    "ConnectTransportAsync expects SocketBridge (Init IWaTransport); got " +
                    socket.GetType().FullName);
            }

            bool mobile = _systemInfo.IsMobile();
            WaTransportConnectAttempt attempt = WaTransportConnectAttempt.Create(
                _transports,
                mobile);

            // Mobile must open StreamSocket first (broker / SocketActivity). Experiment on →
            // PreferStreamSocketOnMobile; factory journals so a MessageWebSocket-only mobile
            // path cannot ship silently.
            RuntimeDiagnosticsService.Instance.Write(
                "transport",
                "connect-attempt-plan",
                "mobile=" + mobile +
                "; preferStreamOnMobile=" +
                ConnectionStabilityExperiment.PreferStreamSocketOnMobile +
                "; primary=" + (attempt.Primary != null ? attempt.Primary.Name : "null") +
                "; fallback=" + (attempt.Fallback != null ? attempt.Fallback.Name : "none"));

            return bridge.ConnectWithTransportsAsync(attempt.Primary, attempt.Fallback);
        }

        public void DisposeSession(object session)
        {
            var disposable = session as IDisposable;
            if (disposable == null)
            {
                return;
            }

            try
            {
                disposable.Dispose();
            }
            catch
            {
            }
        }
    }
}
