using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Exceptions;
using Unison.Core.Factories;
using Unison.Core.Helpers;
using Unison.Core.Mappers;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.ViewModels
{
    public partial class ChatDetailViewModel
    {
        /// <summary>
        /// Starts the header presence watch. Idempotent per conversation: a second call for the
        /// chat already watched keeps the running timer (roster flushes must not replay the
        /// subtitle animation).
        /// </summary>
        public void StartPresenceWatch(string jid)
        {
            if (_presenceCts != null && !_presenceCts.IsCancellationRequested &&
                string.Equals(_presenceWatch.WatchingJid, jid?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            StopPresenceWatch();
            if (!_presenceWatch.TryBegin(jid))
            {
                return;
            }

            jid = _presenceWatch.WatchingJid;
            _presenceCts = new CancellationTokenSource();
            _presenceOpenedUtc = DateTime.UtcNow;
            _presenceReceived = false;
            _pendingPresenceText = null;

            EnsurePresenceHandlerAttached();

            if (jid.IndexOf("@g.us", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _presenceReceived = true;
                _pendingPresenceText = _strings.Get(
                    "ChatDetail_SelectForGroupInfo",
                    "select here for group info");
                _ = RunPresenceTimerAsync(_presenceCts.Token, null);
                return;
            }

            _ = RunPresenceTimerAsync(_presenceCts.Token, jid);
        }

        /// <summary>Cancel presence timer and detach handler.</summary>
        public void StopPresenceWatch()
        {
            try
            {
                _presenceCts?.Cancel();
            }
            catch
            {
            }

            _presenceCts?.Dispose();
            _presenceCts = null;
            _presenceWatch.Stop();
            _presenceReceived = false;
            _pendingPresenceText = null;
            DetachPresenceHandler();
        }


        private void EnsurePresenceHandlerAttached()
        {
            if (_presenceHandlerAttached)
            {
                return;
            }

            _messageService.PresenceUpdated += MessageService_PresenceUpdated;
            _presenceHandlerAttached = true;
        }

        private void DetachPresenceHandler()
        {
            if (!_presenceHandlerAttached)
            {
                return;
            }

            _messageService.PresenceUpdated -= MessageService_PresenceUpdated;
            _presenceHandlerAttached = false;
        }

        private void MessageService_PresenceUpdated(object sender, PresenceUpdateEventArgs e)
        {
            if (_presenceCts == null || _presenceCts.IsCancellationRequested)
            {
                return;
            }

            _presenceReceived = true;
            _pendingPresenceText = FormatPresenceText(e?.Presence, e?.LastSeen);
        }

        private async Task RunPresenceTimerAsync(CancellationToken ct, string subscribeJid)
        {
            try
            {
                bool subscribed = false;

                for (int i = 0; i < 30; i++)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    if (_presenceReceived)
                    {
                        break;
                    }

                    if (!subscribed &&
                        !string.IsNullOrEmpty(subscribeJid) &&
                        _connectionService != null &&
                        _connectionService.IsConnected)
                    {
                        await _messageService.SubscribeToPresenceAsync(subscribeJid);
                        subscribed = true;
                    }

                    await Task.Delay(100, ct);
                }

                if (ct.IsCancellationRequested)
                {
                    return;
                }

                string text = null;
                if (_presenceReceived && !string.IsNullOrEmpty(_pendingPresenceText))
                {
                    var elapsed = (DateTime.UtcNow - _presenceOpenedUtc).TotalMilliseconds;
                    if (elapsed < 3000)
                    {
                        await Task.Delay((int)(3000 - elapsed), ct);
                    }

                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    text = _pendingPresenceText;
                }

                await _dispatcher.RunAsync(() =>
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    PresenceAnimationRequested?.Invoke(this, text);
                });
            }
            catch (TaskCanceledException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatDetailViewModel] Presence timer: " + ex.Message);
            }
        }

        private string FormatPresenceText(string presence, long? lastSeenEpoch)
        {
            if (presence == "available" || presence == "composing")
            {
                return presence == "composing"
                    ? _strings.Get("ChatDetail_PresenceTyping", "typing...")
                    : _strings.Get("ChatDetail_PresenceOnline", "online");
            }

            if (lastSeenEpoch.HasValue && lastSeenEpoch.Value > 0)
            {
                // netstandard1.4: no DateTimeOffset.FromUnixTimeSeconds
                var lastSeenLocal = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddSeconds(lastSeenEpoch.Value)
                    .ToLocalTime();
                var now = DateTime.Now;
                var timeStr = lastSeenLocal.ToString("HH:mm");

                if (lastSeenLocal.Date == now.Date)
                {
                    return string.Format(
                        _strings.Get("ChatDetail_PresenceLastSeenToday", "last seen today at {0}"),
                        timeStr);
                }

                if (lastSeenLocal.Date == now.Date.AddDays(-1))
                {
                    return string.Format(
                        _strings.Get("ChatDetail_PresenceLastSeenYesterday", "last seen yesterday at {0}"),
                        timeStr);
                }

                if (lastSeenLocal.Date > now.Date.AddDays(-7))
                {
                    return string.Format(
                        _strings.Get("ChatDetail_PresenceLastSeenWeekday", "last seen on {0} at {1}"),
                        lastSeenLocal.ToString("dddd"),
                        timeStr);
                }

                return string.Format(
                    _strings.Get("ChatDetail_PresenceLastSeenDate", "last seen {0} at {1}"),
                    lastSeenLocal.ToString("dd/MM/yyyy"),
                    timeStr);
            }

            return _strings.Get("ChatDetail_PresenceLastSeenRecently", "last seen recently");
        }
    }
}
