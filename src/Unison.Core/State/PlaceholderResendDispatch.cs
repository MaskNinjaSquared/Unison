// =============================================================================
// PlaceholderResendDispatch
//
// Asks the phone to resend one missing message. The ask waits a few seconds
// first so a late arrival can cancel it, is marked in flight while it is out,
// and gives up on its answer after the response window. A send that throws
// gives the try back. Every row change goes through the ledger's lock.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.Core.State
{
    public sealed class PlaceholderResendDispatch
    {
        private readonly MissingMessageLedger _ledger;
        private readonly IPlaceholderResendSocket _socket;
        private readonly Action<string> _log;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly TimeSpan _dispatchDelay;
        private readonly TimeSpan _responseTimeout;

        public PlaceholderResendDispatch(
            MissingMessageLedger ledger,
            IPlaceholderResendSocket socket,
            Action<string> log,
            TimeSpan dispatchDelay,
            TimeSpan responseTimeout,
            Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _log = log ?? (_ => { });
            _dispatchDelay = dispatchDelay;
            _responseTimeout = responseTimeout;
            _delay = delay ?? Task.Delay;
        }

        /// <summary>
        /// Arms the resend and starts it in the background. Returns that background work, or
        /// null when the row may not be asked again yet.
        /// </summary>
        public Task TryDispatch(MissingMessageCandidate candidate, string chatJid, string messageId, string trigger)
        {
            CancellationTokenSource schedule = _ledger.TryBeginSchedule(
                candidate,
                DateTime.UtcNow,
                _responseTimeout,
                _dispatchDelay);
            if (schedule == null)
            {
                return null;
            }

            _log($"placeholder resend scheduled for {candidate.MessageId} in {candidate.ChatJid} (trigger={trigger}, dueInMs={(int)_dispatchDelay.TotalMilliseconds})");
            return Task.Run(() => RunAsync(schedule, chatJid, messageId, trigger));
        }

        private async Task RunAsync(CancellationTokenSource schedule, string chatJid, string messageId, string trigger)
        {
            try
            {
                await _delay(_dispatchDelay, schedule.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                _log($"placeholder resend cancelled before send for {messageId} in {chatJid} (trigger={trigger})");
                return;
            }

            MissingMessageCandidate candidate;
            if (!_ledger.TryClaimScheduled(chatJid, messageId, schedule, out candidate))
            {
                return;
            }

            string stanzaId = null;
            try
            {
                var key = new Proto.MessageKey
                {
                    RemoteJid = candidate.ChatJid,
                    Id = candidate.MessageId,
                    FromMe = candidate.IsFromMe,
                    Participant = candidate.Participant ?? string.Empty
                };

                stanzaId = _socket.GenerateMessageId();
                if (!_ledger.RecordSend(chatJid, messageId, stanzaId, trigger, DateTime.UtcNow, out candidate))
                {
                    return;
                }

                string sentStanzaId = await _socket.RequestPlaceholderResendAsync(key, stanzaId).ConfigureAwait(false);
                if (!string.Equals(sentStanzaId, stanzaId, StringComparison.Ordinal))
                {
                    _log($"PLACEHOLDER_MESSAGE_RESEND stanza id changed unexpectedly: tracked={stanzaId}, sent={sentStanzaId}");
                }

                _log($"placeholder resend sent for {messageId} in {chatJid} (trigger={trigger}, stanzaId={stanzaId})");
                string sent = stanzaId;
                _ = Task.Run(() => ExpireAfterWindowAsync(sent));
            }
            catch (Exception ex)
            {
                _ledger.FailSend(chatJid, messageId, stanzaId);
                _log($"PLACEHOLDER_MESSAGE_RESEND send failed for {messageId} in {chatJid}: {ex.Message}");
            }
            finally
            {
                schedule.Dispose();
            }
        }

        private async Task ExpireAfterWindowAsync(string stanzaId)
        {
            await _delay(_responseTimeout, CancellationToken.None).ConfigureAwait(false);

            PlaceholderResendRequestState timedOut = _ledger.Expire(stanzaId);
            if (timedOut == null)
            {
                return;
            }

            string timeoutKind = timedOut.AckAccepted
                ? $"accepted/no-payload, ackAt={timedOut.AckAcceptedUtc:O}"
                : "no-ack";
            _log($"placeholder resend timed out for {timedOut.MessageId} in {timedOut.ChatJid} (stanzaId={stanzaId}, {timeoutKind})");
        }
    }
}
