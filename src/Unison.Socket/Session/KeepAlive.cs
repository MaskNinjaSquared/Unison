// =============================================================================
// KeepAlive
//
// Sends a periodic ping and decides when the connection is dead. Liveness is
// judged by silence since the last frame received from the server, not by a
// missing pong, so a slow reply never costs the user their session.
//
// Ports: rc14 startKeepAliveRequest in src/Socket/socket.ts
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Socket.Abstractions;

namespace Unison.Socket.Session
{
    /// <summary>
    /// Periodic ping plus an inactivity check. Following rc14, a ping is fire-and-forget: the
    /// connection is declared lost based on silence since the last received frame, not on a
    /// missing pong. The legacy client instead required an IQ reply within 12s.
    /// </summary>
    internal sealed class KeepAlive : IDisposable
    {
        private readonly TimeSpan _interval;
        private readonly TimeSpan _grace;
        private readonly Func<bool> _isOpen;
        private readonly Func<DateTimeOffset?> _lastReceived;
        private readonly Func<Task> _sendPing;
        private readonly Func<Exception, Task> _onConnectionLost;
        private readonly ISocketLog _log;

        private Timer _timer;
        private int _running;

        public KeepAlive(
            TimeSpan interval,
            TimeSpan grace,
            Func<bool> isOpen,
            Func<DateTimeOffset?> lastReceived,
            Func<Task> sendPing,
            Func<Exception, Task> onConnectionLost,
            ISocketLog log)
        {
            _interval = interval;
            _grace = grace;
            _isOpen = isOpen;
            _lastReceived = lastReceived;
            _sendPing = sendPing;
            _onConnectionLost = onConnectionLost;
            _log = log ?? NullSocketLog.Instance;
        }

        public void Start()
        {
            if (_timer != null)
            {
                return;
            }

            _timer = new Timer(OnTick, null, _interval, _interval);
        }

        public void Stop()
        {
            var timer = Interlocked.Exchange(ref _timer, null);
            if (timer != null)
            {
                timer.Dispose();
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void OnTick(object state)
        {
            // A slow tick must not stack up behind the previous one.
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                return;
            }

            RunTickAsync().ContinueWith(
                t =>
                {
                    Interlocked.Exchange(ref _running, 0);
                    if (t.IsFaulted)
                    {
                        _log.Error("Keep-alive tick failed", t.Exception);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async Task RunTickAsync()
        {
            var last = _lastReceived();
            if (last.HasValue)
            {
                var silence = DateTimeOffset.UtcNow - last.Value;
                if (silence > _interval + _grace)
                {
                    _log.Warn($"No frame received for {silence.TotalSeconds:F0}s, declaring connection lost");
                    await _onConnectionLost(new WaConnectionException("Connection was lost", DisconnectReason.ConnectionLost))
                        .ConfigureAwait(false);
                    return;
                }
            }

            if (!_isOpen())
            {
                _log.Warn("Keep-alive fired while the transport was not open");
                return;
            }

            try
            {
                await _sendPing().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("Error sending keep alive", ex);
            }
        }
    }
}
