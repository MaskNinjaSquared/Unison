// =============================================================================
// SctpAssociation
//
// Minimal WebRTC SCTP association: INIT/COOKIE handshake, one DATA stream,
// SACK, HEARTBEAT. Enough for WA's pre-negotiated DataChannel id=0.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.Voip.Engine.Relay.Sctp
{
    public sealed class SctpAssociation : IDisposable
    {
        private readonly IByteTransport _transport;
        private readonly bool _isClient;
        private readonly ushort _localPort;
        private readonly ushort _remotePort;
        private readonly object _gate = new object();
        private readonly Dictionary<ushort, DataChannel> _channels = new Dictionary<ushort, DataChannel>();
        private readonly Dictionary<ushort, ushort> _nextSsn = new Dictionary<ushort, ushort>();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

        private uint _localTag;
        private uint _peerTag;
        private uint _nextTsn;
        private uint _cumulativePeerTsn;
        private bool _established;
        private bool _peerTsnInit;
        private TaskCompletionSource<bool> _establishedTcs =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _reader;
        private byte[] _pendingCookie;
        private uint _myInitiateTag;
        private uint _myInitialTsn;

        public SctpAssociation(IByteTransport transport, bool isClient, ushort port = SctpPacket.DefaultPort)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _isClient = isClient;
            _localPort = port;
            _remotePort = port;
        }

        public bool IsEstablished
        {
            get
            {
                lock (_gate)
                {
                    return _established;
                }
            }
        }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            if (!_isClient)
            {
                throw new InvalidOperationException("ConnectAsync is for the SCTP client.");
            }

            EnsureReader();
            _myInitiateTag = RandomUInt32();
            _myInitialTsn = RandomUInt32();
            _localTag = _myInitiateTag;
            _nextTsn = _myInitialTsn;

            byte[] init = SctpPacket.EncodeInit(
                _localPort,
                _remotePort,
                _myInitiateTag,
                aRwnd: 262144,
                outboundStreams: 16,
                inboundStreams: 16,
                _myInitialTsn);
            _transport.Send(init, 0, init.Length);

            using (cancellationToken.Register(() => _establishedTcs.TrySetCanceled()))
            {
                await _establishedTcs.Task.ConfigureAwait(false);
            }
        }

        public async Task AcceptAsync(CancellationToken cancellationToken)
        {
            if (_isClient)
            {
                throw new InvalidOperationException("AcceptAsync is for the SCTP server.");
            }

            EnsureReader();
            using (cancellationToken.Register(() => _establishedTcs.TrySetCanceled()))
            {
                await _establishedTcs.Task.ConfigureAwait(false);
            }
        }

        public DataChannel OpenDataChannel(ushort streamId, string label)
        {
            lock (_gate)
            {
                DataChannel existing;
                if (_channels.TryGetValue(streamId, out existing))
                {
                    return existing;
                }

                var channel = new DataChannel(this, streamId, label);
                _channels[streamId] = channel;
                if (!_nextSsn.ContainsKey(streamId))
                {
                    _nextSsn[streamId] = 0;
                }

                return channel;
            }
        }

        internal Task SendDataAsync(ushort streamId, byte[] payload, CancellationToken cancellationToken)
        {
            ThrowIfNotEstablished();
            payload = payload ?? new byte[0];
            uint ppid = payload.Length == 0
                ? SctpPayloadProtocol.BinaryEmpty
                : SctpPayloadProtocol.Binary;

            uint tsn;
            ushort ssn;
            uint peerTag;
            lock (_gate)
            {
                tsn = _nextTsn;
                unchecked
                {
                    _nextTsn++;
                }

                ushort current;
                if (!_nextSsn.TryGetValue(streamId, out current))
                {
                    current = 0;
                }

                ssn = current;
                unchecked
                {
                    _nextSsn[streamId] = (ushort)(current + 1);
                }

                peerTag = _peerTag;
            }

            byte[] packet = SctpPacket.EncodeData(
                _localPort,
                _remotePort,
                peerTag,
                tsn,
                streamId,
                ssn,
                ppid,
                payload,
                beginning: true,
                ending: true);
            _transport.Send(packet, 0, packet.Length);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (!_lifetime.IsCancellationRequested)
            {
                _lifetime.Cancel();
            }

            lock (_gate)
            {
                foreach (DataChannel channel in _channels.Values)
                {
                    channel.Close();
                }

                _channels.Clear();
            }

            try
            {
                _transport.Dispose();
            }
            catch
            {
            }
        }

        private void EnsureReader()
        {
            if (_reader != null)
            {
                return;
            }

            _reader = Task.Run(() => ReadLoopAsync(_lifetime.Token));
        }

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                byte[] packet;
                try
                {
                    packet = await _transport.ReceiveAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    break;
                }

                if (packet == null)
                {
                    break;
                }

                HandlePacket(packet);
            }
        }

        private void HandlePacket(byte[] packet)
        {
            ushort sourcePort;
            ushort destPort;
            uint verificationTag;
            List<SctpChunk> chunks;
            // TryParse mutates checksum bytes temporarily; copy first.
            var copy = new byte[packet.Length];
            Buffer.BlockCopy(packet, 0, copy, 0, packet.Length);
            if (!SctpPacket.TryParse(copy, out sourcePort, out destPort, out verificationTag, out chunks))
            {
                return;
            }

            foreach (SctpChunk chunk in chunks)
            {
                switch (chunk.Type)
                {
                    case SctpChunkType.Init:
                        HandleInit(chunk);
                        break;
                    case SctpChunkType.InitAck:
                        HandleInitAck(chunk);
                        break;
                    case SctpChunkType.CookieEcho:
                        HandleCookieEcho(chunk);
                        break;
                    case SctpChunkType.CookieAck:
                        MarkEstablished();
                        break;
                    case SctpChunkType.Data:
                        HandleData(chunk);
                        break;
                    case SctpChunkType.Sack:
                        break;
                    case SctpChunkType.Heartbeat:
                        HandleHeartbeat(chunk);
                        break;
                    case SctpChunkType.Abort:
                        _establishedTcs.TrySetException(new InvalidOperationException("SCTP ABORT"));
                        break;
                }
            }
        }

        private void HandleInit(SctpChunk chunk)
        {
            if (_isClient || chunk.Value == null || chunk.Value.Length < 16)
            {
                return;
            }

            uint peerTag = SctpPacket.ReadUInt32(chunk.Value, 0);
            uint peerTsn = SctpPacket.ReadUInt32(chunk.Value, 12);
            _peerTag = peerTag;
            _myInitiateTag = RandomUInt32();
            _myInitialTsn = RandomUInt32();
            _localTag = _myInitiateTag;
            _nextTsn = _myInitialTsn;
            _cumulativePeerTsn = peerTsn;
            _peerTsnInit = true;

            // Cookie: our tag || peer tag || peer initial TSN (opaque to the peer).
            var cookie = new byte[12];
            SctpPacket.WriteUInt32(cookie, 0, _myInitiateTag);
            SctpPacket.WriteUInt32(cookie, 4, peerTag);
            SctpPacket.WriteUInt32(cookie, 8, peerTsn);
            _pendingCookie = cookie;

            byte[] initAck = SctpPacket.EncodeInitAck(
                _localPort,
                _remotePort,
                verificationTag: peerTag,
                initiateTag: _myInitiateTag,
                aRwnd: 262144,
                outboundStreams: 16,
                inboundStreams: 16,
                initialTsn: _myInitialTsn,
                stateCookie: cookie);
            _transport.Send(initAck, 0, initAck.Length);
        }

        private void HandleInitAck(SctpChunk chunk)
        {
            if (!_isClient || chunk.Value == null || chunk.Value.Length < 16)
            {
                return;
            }

            _peerTag = SctpPacket.ReadUInt32(chunk.Value, 0);
            byte[] cookie;
            if (!SctpPacket.TryReadInitAckCookie(chunk.Value, out cookie))
            {
                _establishedTcs.TrySetException(new InvalidOperationException("INIT-ACK without cookie"));
                return;
            }

            byte[] echo = SctpPacket.EncodeCookieEcho(_localPort, _remotePort, _peerTag, cookie);
            _transport.Send(echo, 0, echo.Length);
        }

        private void HandleCookieEcho(SctpChunk chunk)
        {
            if (_isClient)
            {
                return;
            }

            byte[] ack = SctpPacket.EncodeCookieAck(_localPort, _remotePort, _peerTag);
            _transport.Send(ack, 0, ack.Length);
            MarkEstablished();
        }

        private void HandleData(SctpChunk chunk)
        {
            if (chunk.Value == null || chunk.Value.Length < 12)
            {
                return;
            }

            uint tsn = SctpPacket.ReadUInt32(chunk.Value, 0);
            ushort streamId = SctpPacket.ReadUInt16(chunk.Value, 4);
            uint ppid = SctpPacket.ReadUInt32(chunk.Value, 8);
            int payloadLen = chunk.Value.Length - 12;
            var payload = new byte[payloadLen];
            if (payloadLen > 0)
            {
                Buffer.BlockCopy(chunk.Value, 12, payload, 0, payloadLen);
            }

            lock (_gate)
            {
                // Minimal receiver: always advance cumulative TSN to the latest seen.
                // Enough for a single ordered stream without reordering.
                if (!_peerTsnInit || tsn == unchecked(_cumulativePeerTsn + 1) || tsn == _cumulativePeerTsn)
                {
                    _cumulativePeerTsn = tsn;
                    _peerTsnInit = true;
                }
                else if (unchecked(tsn - _cumulativePeerTsn) < 0x80000000u)
                {
                    _cumulativePeerTsn = tsn;
                }
            }

            byte[] sack = SctpPacket.EncodeSack(
                _localPort,
                _remotePort,
                _peerTag,
                _cumulativePeerTsn,
                aRwnd: 262144);
            _transport.Send(sack, 0, sack.Length);

            if (ppid == SctpPayloadProtocol.Binary || ppid == SctpPayloadProtocol.BinaryEmpty)
            {
                DataChannel channel = OpenDataChannel(streamId, RelayPacketClassifier.DataChannelLabel);
                channel.Enqueue(payload);
            }
        }

        private void HandleHeartbeat(SctpChunk chunk)
        {
            byte[] ack = SctpPacket.EncodeHeartbeatAck(
                _localPort,
                _remotePort,
                _peerTag,
                chunk.Value);
            _transport.Send(ack, 0, ack.Length);
        }

        private void MarkEstablished()
        {
            lock (_gate)
            {
                _established = true;
            }

            _establishedTcs.TrySetResult(true);
        }

        private void ThrowIfNotEstablished()
        {
            if (!IsEstablished)
            {
                throw new InvalidOperationException("SCTP association is not established.");
            }
        }

        private static uint RandomUInt32()
        {
            var bytes = new byte[4];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            return (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
        }
    }
}
