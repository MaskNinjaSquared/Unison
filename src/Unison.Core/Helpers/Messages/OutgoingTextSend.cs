// =============================================================================
// OutgoingTextSend
//
// Sending a text, start to finish: the bubble goes on screen and into the
// outbox before the transport is touched, then one bounded reconnect-and-retry
// with the same id, then the bubble is settled as sent or failed and queued for
// the normal flush. A send never throws for a transport problem; the failure is
// the bubble's status.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Baileys.Crypto;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public sealed class OutgoingTextSend
    {
        private readonly IOutgoingMessageHost _host;
        private readonly IJidResolver _jids;
        private readonly Func<string> _newMessageId;
        private readonly Func<DateTime> _utcNow;
        private readonly Func<TimeSpan, Task> _delay;

        public OutgoingTextSend(
            IOutgoingMessageHost host,
            IJidResolver jids,
            Func<string> newMessageId = null,
            Func<DateTime> utcNow = null,
            Func<TimeSpan, Task> delay = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _newMessageId = newMessageId ?? NewRandomMessageId;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _delay = delay ?? Task.Delay;
        }

        // No ConfigureAwait(false) anywhere below: the host mutates the in-memory timeline,
        // which is only safe from the thread the caller (the UI) started on.
        public Task<ChatMessage> SendAsync(string jid, string text)
        {
            return SendAsync(jid, new OutgoingTextMentions { Text = text });
        }

        public async Task<ChatMessage> SendAsync(string jid, OutgoingTextMentions message)
        {
            string text = message != null ? message.Text : null;
            if (string.IsNullOrWhiteSpace(jid))
            {
                throw new ArgumentException("A destination JID is required", nameof(jid));
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("Message text is empty", nameof(text));
            }

            string chatJid = _jids.GetCanonicalJid(JidHelper.Normalize(jid));
            string messageId = _newMessageId();
            _host.RecordSendAttempt(messageId);

            ChatMessage bubble = OutgoingChatMessage.Text(
                new OutgoingMessageStamp
                {
                    Id = messageId,
                    ChatJid = chatJid,
                    ParticipantJid = _jids.Self?.Id,
                    TimestampUtc = _utcNow(),
                    Status = ChatMessage.StatusPending
                },
                text,
                message.Mentions,
                message.MentionAll,
                message.Quote);

            _host.AcceptOutgoing(chatJid, bubble);
            await _host.ShowLocalSendPreviewAsync(chatJid, bubble, ChatPreviewKind.Text);
            _host.NotifyChatChanged(chatJid);
            await _host.PersistLiveAsync(chatJid, bubble);

            Exception failure = await SendWithRetryAsync(chatJid, message, messageId);
            if (failure == null)
            {
                if (OutgoingSendSettlement.ShouldPromotePending(bubble.Status))
                {
                    bubble.Status = SelfChatStatusPolicy.Resolve(
                        ChatMessage.StatusSent,
                        _jids.IsSelfLinked(chatJid));
                }

                _host.RecordSendSuccess(messageId);
            }
            else
            {
                bubble.Status = ChatMessage.StatusFailed;
                _host.RecordSendFailure(messageId, failure);
                _host.ReportError(failure);
            }

            await _host.PersistLiveAsync(chatJid, bubble);
            _host.QueueForPersist(chatJid, bubble);
            _host.NotifyChatChanged(chatJid);
            return bubble;
        }

        /// <summary>Null when the server took the message; otherwise the last error seen.</summary>
        private async Task<Exception> SendWithRetryAsync(string chatJid, OutgoingTextMentions message, string messageId)
        {
            Exception lastError = null;
            for (int attempt = 1; attempt <= OutgoingTextSendRetry.MaxAttempts; attempt++)
            {
                OutgoingSendAttempt result = await _host.TrySendTextOnceAsync(new OutgoingTextAttempt
                {
                    ChatJid = chatJid,
                    Text = message.Text,
                    Mentions = message.Mentions,
                    MentionAll = message.MentionAll,
                    Quote = message.Quote,
                    MessageId = messageId,
                    Attempt = attempt,
                    ConnectTimeout = OutgoingTextSendRetry.ConnectTimeout(attempt),
                    WriteTimeout = OutgoingTextSendRetry.WriteTimeout
                });

                if (result.Outcome == OutgoingSendAttemptOutcome.Accepted)
                {
                    return null;
                }

                lastError = result.Error ?? lastError;
                OutgoingTextSendStep step = OutgoingTextSendRetry.After(attempt, result.Outcome);
                if (step.InvalidateSocket)
                {
                    _host.InvalidateTransport(result, "send-attempt-" + attempt);
                }

                if (!step.Retry)
                {
                    break;
                }

                await _delay(step.RetryDelay);
            }

            return lastError ?? new InvalidOperationException("Unable to send the message");
        }

        private static string NewRandomMessageId()
        {
            return OutgoingMessageId.FromRandomBytes(CryptoUtils.RandomBytes(OutgoingMessageId.RandomByteCount));
        }
    }
}
