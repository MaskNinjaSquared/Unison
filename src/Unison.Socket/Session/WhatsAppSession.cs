// =============================================================================
// WhatsAppSession
//
// The composition root of the socket layer: it builds the event bus, the
// ConnectionHandler and the feature modules, wires them together and hands the
// caller two things - Connect/Close, and the bus to subscribe to.
//
// Having one place that assembles the graph is what lets a host adopt the new
// stack with a few lines, and lets a test build the same graph over a fake
// transport. Feature modules are added here as each phase migrates; the
// handler itself never learns about them.
//
// Ports: rc14 the assembly performed by makeWASocket over makeSocket
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Socket.Abstractions;
using Unison.Socket.Events;
using Unison.Socket.Session.Pairing;
using Unison.Socket.UseCases.Auth;

namespace Unison.Socket.Session
{
    /// <summary>
    /// A single WhatsApp connection with its feature modules attached.
    /// </summary>
    public sealed class WhatsAppSession : IDisposable
    {
        private readonly WaEventBuffer _ownedBus;
        private readonly PairingFlow _pairing;
        private readonly ConnectionLifecycle _lifecycle;
        private readonly AuthState _auth;
        private bool _disposed;

        public WhatsAppSession(
            IWaTransport transport,
            AuthState auth,
            SocketConfig config = null,
            ISocketLog log = null,
            IClientPayloadFactory payloadFactory = null,
            IWaEventBus events = null)
        {
            Config = config ?? new SocketConfig();
            Log = log ?? NullSocketLog.Instance;
            _auth = auth;

            if (events == null)
            {
                _ownedBus = new WaEventBuffer(Log);
                Events = _ownedBus;
            }
            else
            {
                Events = events;
            }

            Connection = new ConnectionHandler(
                transport,
                auth,
                payloadFactory ?? new ClientPayloadFactory(),
                Events,
                Config,
                Log);

            _pairing = new PairingFlow(Connection, auth, Events, Config, Log);
            _pairing.Attach();

            _lifecycle = new ConnectionLifecycle(Connection, auth, Events, Log);
            _lifecycle.Attach();
        }

        public IWaEventBus Events { get; }

        public ConnectionHandler Connection { get; }

        public SocketConfig Config { get; }

        public ISocketLog Log { get; }

        /// <summary>Post-login hook, forwarded from <see cref="ConnectionLifecycle"/>.</summary>
        public event Func<Task> Opened
        {
            add { _lifecycle.Opened += value; }
            remove { _lifecycle.Opened -= value; }
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            return Connection.ConnectAsync(cancellationToken);
        }

        public Task CloseAsync(Exception reason = null)
        {
            return Connection.EndAsync(reason);
        }

        /// <summary>
        /// Unlinks this device from the account and then closes. Unlike <see cref="CloseAsync"/>,
        /// which the account survives, this is the connection ending for good.
        /// </summary>
        public Task LogoutAsync(string reason = null)
        {
            return new LogoutUseCase(Connection, _auth, Log).ExecuteAsync(reason);
        }

        /// <summary>
        /// Phone-number pairing: sends companion_hello and returns the eight-character code.
        /// The finish handshake arrives later as a notification, handled inside PairingFlow.
        /// </summary>
        public Task<string> RequestPairingCodeAsync(string phoneNumber, string customPairingCode = null)
        {
            return _pairing.RequestPairingCodeAsync(phoneNumber, customPairingCode);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _pairing.Dispose();
            _lifecycle.Dispose();
            Connection.Dispose();

            if (_ownedBus != null)
            {
                _ownedBus.Dispose();
            }
        }
    }
}
