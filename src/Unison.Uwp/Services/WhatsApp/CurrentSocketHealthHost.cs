using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Uwp.Client;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Unison.WhatsAppClient.Messages;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// <see cref="IIsCurrentSocketHealthyHost"/> over the shared socket slot and session flags,
    /// for the on-demand health probe in <see cref="IsCurrentSocketHealthyDispatch"/>.
    /// </summary>
    internal sealed class CurrentSocketHealthHost : IIsCurrentSocketHealthyHost
    {
        private readonly ConnectionSlot<IWhatsAppSocket> _socketSlot;
        private readonly ConnectionSessionFlags _flags;

        public CurrentSocketHealthHost(
            ConnectionSlot<IWhatsAppSocket> socketSlot,
            ConnectionSessionFlags flags)
        {
            _socketSlot = socketSlot ?? throw new ArgumentNullException(nameof(socketSlot));
            _flags = flags ?? throw new ArgumentNullException(nameof(flags));
        }

        public object CurrentSocket => _socketSlot.Current;

        public bool SuppressReconnect
        {
            get => _flags.SuppressReconnect;
            set => _flags.SuppressReconnect = value;
        }

        public bool SocketHasStalledNodeProcessing(object socket)
        {
            var typed = socket as IWhatsAppSocket;
            return typed != null &&
                typed.HasStalledNodeProcessing(ConnectionHealthPolicy.NodeProcessingStallLimit);
        }

        public bool SocketHasFreshConnection(object socket)
        {
            var typed = socket as IWhatsAppSocket;
            return typed != null &&
                typed.HasFreshConnection(ConnectionHealthProfile.OnDemand.FreshnessLimit);
        }

        public bool SocketIsConnected(object socket) =>
            socket is IWhatsAppSocket typed && typed.IsConnected;

        public bool SocketIsHandshakeComplete(object socket) =>
            socket is IWhatsAppSocket typed && typed.IsHandshakeComplete;

        public int SocketQueuedNodeProcessingCount(object socket) =>
            socket is IWhatsAppSocket typed ? typed.QueuedNodeProcessingCount : 0;

        public void LogNodeQueueStalled(int depth) =>
            Debug.WriteLine(
                "[WhatsAppService] Socket node queue is stalled (depth=" + depth + ")");

        public Task<bool> ProbeSocketConnectionAsync(object socket)
        {
            var typed = socket as IWhatsAppSocket;
            if (typed == null)
            {
                return Task.FromResult(false);
            }

            return typed.ProbeConnectionAsync(ConnectionHealthProfile.OnDemand.ProbeTimeoutMs);
        }

        public void DisconnectSocket(object socket)
        {
            var typed = socket as IWhatsAppSocket;
            if (typed == null)
            {
                return;
            }

            try
            {
                typed.Disconnect();
            }
            catch
            {
            }
        }

        public void ClearCurrentSocketIfSame(object socket)
        {
            if (ReferenceEquals(_socketSlot.Current, socket))
            {
                _socketSlot.Current = null;
            }
        }
    }
}
