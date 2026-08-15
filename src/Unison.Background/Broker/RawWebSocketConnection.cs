using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace Unison.Background
{
    internal enum RawWebSocketMessageType
    {
        Binary,
        Text,
        Close,
        Control
    }

    internal sealed class RawWebSocketMessage
    {
        public RawWebSocketMessageType Type { get; set; }
        public byte[] Payload { get; set; }
        public ushort CloseCode { get; set; }
        public string CloseReason { get; set; }
    }

    /// <summary>
    /// Minimal RFC 6455 framing over an already-connected StreamSocket.
    /// Client frames are always masked; server masking is accepted defensively.
    /// </summary>
    internal sealed class RawWebSocketConnection : IDisposable
    {
        private readonly StreamSocket _socket;
        private DataReader _reader;
        private DataWriter _writer;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private MemoryStream _fragmentedPayload;
        private byte _fragmentedOpcode;
        private bool _detached;

        public RawWebSocketConnection(StreamSocket socket)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _reader = new DataReader(socket.InputStream)
            {
                InputStreamOptions = InputStreamOptions.Partial,
                ByteOrder = ByteOrder.BigEndian
            };
            _writer = new DataWriter(socket.OutputStream)
            {
                ByteOrder = ByteOrder.BigEndian
            };
        }

        public async Task<RawWebSocketMessage> ReadMessageAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                byte[] header = await ReadExactAsync(2, cancellationToken);
                byte first = header[0];
                byte second = header[1];
                bool fin = (first & 0x80) != 0;
                byte opcode = (byte)(first & 0x0F);
                bool masked = (second & 0x80) != 0;
                ulong payloadLength = (ulong)(second & 0x7F);

                if (payloadLength == 126)
                {
                    byte[] extended = await ReadExactAsync(2, cancellationToken);
                    payloadLength = (ulong)((extended[0] << 8) | extended[1]);
                }
                else if (payloadLength == 127)
                {
                    byte[] extended = await ReadExactAsync(8, cancellationToken);
                    payloadLength = 0;
                    for (int i = 0; i < extended.Length; i++)
                    {
                        payloadLength = (payloadLength << 8) | extended[i];
                    }
                }

                if (payloadLength > SocketBrokerConstants.MaximumWebSocketMessageBytes)
                {
                    throw new IOException("WebSocket payload exceeds safety limit: " + payloadLength);
                }

                byte[] mask = masked ? await ReadExactAsync(4, cancellationToken) : null;
                byte[] payload = payloadLength == 0
                    ? new byte[0]
                    : await ReadExactAsync((int)payloadLength, cancellationToken);

                if (mask != null)
                {
                    for (int i = 0; i < payload.Length; i++)
                    {
                        payload[i] = (byte)(payload[i] ^ mask[i % 4]);
                    }
                }

                if (opcode == 0x8)
                {
                    ushort closeCode = 1000;
                    string closeReason = string.Empty;
                    if (payload.Length >= 2)
                    {
                        closeCode = (ushort)((payload[0] << 8) | payload[1]);
                        if (payload.Length > 2)
                        {
                            closeReason = Encoding.UTF8.GetString(payload, 2, payload.Length - 2);
                        }
                    }
                    return new RawWebSocketMessage
                    {
                        Type = RawWebSocketMessageType.Close,
                        Payload = payload,
                        CloseCode = closeCode,
                        CloseReason = closeReason
                    };
                }

                if (opcode == 0x9)
                {
                    await SendControlAsync(0xA, payload, cancellationToken);
                    return new RawWebSocketMessage
                    {
                        Type = RawWebSocketMessageType.Control,
                        Payload = payload
                    };
                }

                if (opcode == 0xA)
                {
                    return new RawWebSocketMessage
                    {
                        Type = RawWebSocketMessageType.Control,
                        Payload = payload
                    };
                }

                if (opcode == 0x1 || opcode == 0x2)
                {
                    if (_fragmentedPayload != null)
                    {
                        _fragmentedPayload.Dispose();
                        _fragmentedPayload = null;
                    }
                    _fragmentedOpcode = opcode;
                    if (fin)
                    {
                        return new RawWebSocketMessage
                        {
                            Type = opcode == 0x2 ? RawWebSocketMessageType.Binary : RawWebSocketMessageType.Text,
                            Payload = payload
                        };
                    }
                    _fragmentedPayload = new MemoryStream();
                    _fragmentedPayload.Write(payload, 0, payload.Length);
                    continue;
                }

                if (opcode == 0x0)
                {
                    if (_fragmentedPayload == null)
                    {
                        throw new IOException("Unexpected WebSocket continuation frame");
                    }
                    _fragmentedPayload.Write(payload, 0, payload.Length);
                    if (_fragmentedPayload.Length > SocketBrokerConstants.MaximumWebSocketMessageBytes)
                    {
                        throw new IOException("Fragmented WebSocket message exceeds safety limit");
                    }
                    if (fin)
                    {
                        byte[] complete = _fragmentedPayload.ToArray();
                        _fragmentedPayload.Dispose();
                        _fragmentedPayload = null;
                        return new RawWebSocketMessage
                        {
                            Type = _fragmentedOpcode == 0x2 ? RawWebSocketMessageType.Binary : RawWebSocketMessageType.Text,
                            Payload = complete
                        };
                    }
                    continue;
                }

                // Unknown non-control opcode: ignore it rather than corrupting the stream.
            }
        }

        public Task SendBinaryAsync(byte[] payload, CancellationToken cancellationToken)
        {
            return SendFrameAsync(0x2, payload ?? new byte[0], true, cancellationToken);
        }

        public Task SendPingAsync(byte[] payload, CancellationToken cancellationToken)
        {
            byte[] safe = payload ?? new byte[0];
            if (safe.Length > 125)
            {
                Array.Resize(ref safe, 125);
            }
            return SendControlAsync(0x9, safe, cancellationToken);
        }

        public Task SendCloseAsync(ushort code, string reason, CancellationToken cancellationToken)
        {
            byte[] reasonBytes = Encoding.UTF8.GetBytes(reason ?? string.Empty);
            if (reasonBytes.Length > 123)
            {
                Array.Resize(ref reasonBytes, 123);
            }
            byte[] payload = new byte[2 + reasonBytes.Length];
            payload[0] = (byte)(code >> 8);
            payload[1] = (byte)(code & 0xFF);
            System.Buffer.BlockCopy(reasonBytes, 0, payload, 2, reasonBytes.Length);
            return SendControlAsync(0x8, payload, cancellationToken);
        }

        public async Task<bool> WaitForWriteIdleAsync(int timeoutMilliseconds)
        {
            using (var timeout = new CancellationTokenSource(timeoutMilliseconds))
            {
                try
                {
                    await _writeLock.WaitAsync(timeout.Token);
                    _writeLock.Release();
                    return true;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
        }

        private Task SendControlAsync(byte opcode, byte[] payload, CancellationToken cancellationToken)
        {
            return SendFrameAsync(opcode, payload ?? new byte[0], true, cancellationToken);
        }

        private async Task SendFrameAsync(byte opcode, byte[] payload, bool fin, CancellationToken cancellationToken)
        {
            if (_detached || _writer == null)
            {
                throw new ObjectDisposedException(nameof(RawWebSocketConnection));
            }

            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                byte[] mask = new byte[4];
                using (var random = System.Security.Cryptography.RandomNumberGenerator.Create())
                {
                    random.GetBytes(mask);
                }

                using (var frame = new MemoryStream())
                {
                    frame.WriteByte((byte)((fin ? 0x80 : 0x00) | (opcode & 0x0F)));
                    int length = payload.Length;
                    if (length <= 125)
                    {
                        frame.WriteByte((byte)(0x80 | length));
                    }
                    else if (length <= ushort.MaxValue)
                    {
                        frame.WriteByte((byte)(0x80 | 126));
                        frame.WriteByte((byte)(length >> 8));
                        frame.WriteByte((byte)length);
                    }
                    else
                    {
                        frame.WriteByte((byte)(0x80 | 127));
                        ulong longLength = (ulong)length;
                        for (int shift = 56; shift >= 0; shift -= 8)
                        {
                            frame.WriteByte((byte)(longLength >> shift));
                        }
                    }

                    frame.Write(mask, 0, mask.Length);
                    for (int i = 0; i < payload.Length; i++)
                    {
                        frame.WriteByte((byte)(payload[i] ^ mask[i % 4]));
                    }

                    _writer.WriteBytes(frame.ToArray());
                    await _writer.StoreAsync().AsTask(cancellationToken);
                    await _writer.FlushAsync().AsTask(cancellationToken);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task<byte[]> ReadExactAsync(int count, CancellationToken cancellationToken)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            byte[] result = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                uint requested = (uint)(count - offset);
                uint loaded = await _reader.LoadAsync(requested).AsTask(cancellationToken);
                if (loaded == 0)
                {
                    throw new EndOfStreamException("WebSocket stream closed while reading " + count + " bytes");
                }
                int toRead = (int)Math.Min(loaded, requested);
                byte[] chunk = new byte[toRead];
                _reader.ReadBytes(chunk);
                System.Buffer.BlockCopy(chunk, 0, result, offset, toRead);
                offset += toRead;
            }
            return result;
        }

        public void DetachStreams()
        {
            if (_detached)
            {
                return;
            }
            _detached = true;
            if (_reader != null)
            {
                try { _reader.DetachStream(); } catch { }
                _reader.Dispose();
                _reader = null;
            }
            if (_writer != null)
            {
                try { _writer.DetachStream(); } catch { }
                _writer.Dispose();
                _writer = null;
            }
        }

        public void Dispose()
        {
            DetachStreams();
            if (_fragmentedPayload != null)
            {
                _fragmentedPayload.Dispose();
                _fragmentedPayload = null;
            }
            _writeLock.Dispose();
        }
    }
}
