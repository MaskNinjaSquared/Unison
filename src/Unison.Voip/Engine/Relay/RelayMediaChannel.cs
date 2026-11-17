// =============================================================================
// RelayMediaChannel
//
// UDP → DTLS 1.2 → SCTP → pre-negotiated DataChannel id=0. STUN/RTP travel as
// binary DataChannel messages. Ports meowcaller relay.ConnectRelayMedia.
// =============================================================================
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Tls;
using Unison.Voip.Abstractions;
using Unison.Voip.Engine.Relay.Sctp;

namespace Unison.Voip.Engine.Relay
{
    public sealed class RelayMediaChannel : IRelayMediaPipe
    {
        private readonly ICallUdpTransport _udp;
        private readonly DtlsTransport _dtls;
        private readonly DtlsByteTransport _bytes;
        private readonly SctpAssociation _sctp;
        private readonly DataChannel _channel;
        private readonly bool _ownsUdp;

        private RelayMediaChannel(
            ICallUdpTransport udp,
            DtlsTransport dtls,
            DtlsByteTransport bytes,
            SctpAssociation sctp,
            DataChannel channel,
            bool ownsUdp)
        {
            _udp = udp;
            _dtls = dtls;
            _bytes = bytes;
            _sctp = sctp;
            _channel = channel;
            _ownsUdp = ownsUdp;
        }

        /// <summary>
        /// Client path against a live (or test) DTLS/SCTP peer at <paramref name="relay"/>.
        /// </summary>
        public static async Task<RelayMediaChannel> ConnectAsync(
            ICallUdpTransport udp,
            IPEndPoint relay,
            CancellationToken cancellationToken,
            bool ownsUdp = true)
        {
            if (udp == null)
            {
                throw new ArgumentNullException(nameof(udp));
            }

            if (relay == null)
            {
                throw new ArgumentNullException(nameof(relay));
            }

            try
            {
                await udp.BindAsync(cancellationToken).ConfigureAwait(false);
                DtlsTransport dtls = await Task.Run(
                    () => DtlsRelayConnector.ConnectClient(udp, relay, TimeSpan.FromSeconds(12)),
                    cancellationToken).ConfigureAwait(false);

                var bytes = new DtlsByteTransport(dtls);
                var sctp = new SctpAssociation(bytes, isClient: true);
                await sctp.ConnectAsync(cancellationToken).ConfigureAwait(false);
                DataChannel channel = sctp.OpenDataChannel(0, RelayPacketClassifier.DataChannelLabel);
                return new RelayMediaChannel(udp, dtls, bytes, sctp, channel, ownsUdp);
            }
            catch (Exception ex)
            {
                throw new RelayTransportException("connect", ex);
            }
        }

        /// <summary>Test helper: accept the DTLS/SCTP server half over the same UDP pair.</summary>
        public static async Task<RelayMediaChannel> AcceptAsync(
            ICallUdpTransport udp,
            IPEndPoint remote,
            CancellationToken cancellationToken,
            bool ownsUdp = true)
        {
            if (udp == null)
            {
                throw new ArgumentNullException(nameof(udp));
            }

            await udp.BindAsync(cancellationToken).ConfigureAwait(false);
            DtlsTransport dtls = await Task.Run(
                () => DtlsRelayConnector.AcceptServer(udp, remote, TimeSpan.FromSeconds(12)),
                cancellationToken).ConfigureAwait(false);

            var bytes = new DtlsByteTransport(dtls);
            var sctp = new SctpAssociation(bytes, isClient: false);
            Task accept = sctp.AcceptAsync(cancellationToken);
            await accept.ConfigureAwait(false);
            DataChannel channel = sctp.OpenDataChannel(0, RelayPacketClassifier.DataChannelLabel);
            return new RelayMediaChannel(udp, dtls, bytes, sctp, channel, ownsUdp);
        }

        public async Task SendAsync(byte[] data, CancellationToken cancellationToken)
        {
            try
            {
                await _channel.SendAsync(data, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new RelayTransportException("send", ex);
            }
        }

        public async Task<byte[]> RecvAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new RelayTransportException("recv", ex);
            }
        }

        public void Dispose()
        {
            try
            {
                _channel.Close();
            }
            catch
            {
            }

            try
            {
                _sctp.Dispose();
            }
            catch
            {
            }

            try
            {
                _bytes.Dispose();
            }
            catch
            {
            }

            try
            {
                _dtls.Close();
            }
            catch
            {
            }

            if (_ownsUdp)
            {
                try
                {
                    _udp.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    public sealed class RelayTransportException : Exception
    {
        public RelayTransportException(string op, Exception inner)
            : base("relay " + op + ": " + (inner != null ? inner.Message : string.Empty), inner)
        {
            Op = op;
        }

        public string Op { get; }
    }
}
