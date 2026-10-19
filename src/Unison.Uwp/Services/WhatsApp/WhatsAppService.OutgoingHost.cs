using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Uwp.Client;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// The caches and the socket the send flow on <c>MessageFacade</c> still has to reach.
    /// </summary>
    /// <remarks>
    /// Implemented explicitly so none of it shows on the client's own surface: the only way in
    /// is the <see cref="IOutgoingMessageHost"/> registration, and the only caller is the
    /// façade. Each member is one step the old send methods took inline; the order they run
    /// in is decided by <see cref="OutgoingTextSend"/> and <see cref="OutgoingMediaSend"/>.
    /// </remarks>
    public partial class WhatsAppService : IOutgoingMessageHost
    {
        Task IOutgoingMessageHost.EnsureConnectedAsync() => EnsureConnectedAsync();

        Task IOutgoingMessageHost.SendImageAsync(string jid, byte[] imageBytes, string caption, string messageId)
        {
            return Task.Run(async () => await _socket.SendImageMessageAsync(jid, imageBytes, caption, messageId));
        }

        Task IOutgoingMessageHost.SendAudioAsync(string jid, OutgoingAudio audio, string messageId)
        {
            return _socket.SendAudioMessageAsync(
                jid,
                audio.Bytes,
                audio.MimeType,
                audio.DurationSeconds,
                audio.IsVoiceMessage,
                messageId);
        }

        Task IOutgoingMessageHost.SendDocumentAsync(string jid, OutgoingDocument document, string messageId)
        {
            return _socket.SendDocumentMessageAsync(
                jid,
                document.Bytes,
                document.MimeType,
                document.FileName,
                document.Caption,
                messageId);
        }

        void IOutgoingMessageHost.AcceptOutgoing(string chatJid, ChatMessage message)
        {
            ApplyPendingStateToMessage(chatJid, message);
            MessagesByChat.Insert(chatJid, message, MaxActiveChatMessagesInMemory, DateTime.UtcNow);
        }

        Task IOutgoingMessageHost.ShowLocalSendPreviewAsync(string chatJid, ChatMessage message, ChatPreviewKind kind)
        {
            return UpdateChatPreviewForLocalSendAsync(
                chatJid,
                message.Content,
                message.Timestamp,
                kind,
                message.MentionedJids,
                message.Id,
                message.Status);
        }

        void IOutgoingMessageHost.NotifyChatChanged(string chatJid) => QueueChatMessagesChanged(chatJid);

        Task IOutgoingMessageHost.PersistLiveAsync(string chatJid, ChatMessage message)
        {
            return PersistLiveMessagesAsync(chatJid, new[] { message });
        }

        void IOutgoingMessageHost.QueueForPersist(string chatJid, ChatMessage message)
        {
            QueueOfflineReplayMessageForPersist(chatJid, message);
            SchedulePersist();
        }

        async Task<OutgoingSendAttempt> IOutgoingMessageHost.TrySendTextOnceAsync(OutgoingTextAttempt request)
        {
            await EnsureConnectedAsync((int)request.ConnectTimeout.TotalMilliseconds);
            var socket = _socket;
            if (socket == null || !socket.IsConnected || !socket.IsHandshakeComplete)
            {
                return new OutgoingSendAttempt
                {
                    Outcome = OutgoingSendAttemptOutcome.SocketNotReady,
                    Error = new InvalidOperationException("WhatsApp socket is not ready"),
                    TransportToken = socket
                };
            }

            try
            {
                Task<string> sendTask = socket.SendTextMessageAsync(
                    request.ChatJid,
                    request.Text,
                    request.MessageId,
                    request.Mentions,
                    request.MentionAll,
                    request.Quote);
                Task completed = await Task.WhenAny(sendTask, Task.Delay(request.WriteTimeout));
                if (completed != sendTask)
                {
                    throw new TimeoutException("Timed out writing the message to the WhatsApp transport");
                }

                await sendTask;
                return new OutgoingSendAttempt
                {
                    Outcome = OutgoingSendAttemptOutcome.Accepted,
                    TransportToken = socket
                };
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.RecordException(
                    "send",
                    "text-transport-attempt-failed",
                    ex,
                    "messageId=" + request.MessageId + "; attempt=" + request.Attempt);

                bool socketUsable = socket.IsConnected && socket.IsHandshakeComplete;
                return new OutgoingSendAttempt
                {
                    Outcome = OutgoingFailureClassification.IsTransportFailure(ex, socketUsable)
                        ? OutgoingSendAttemptOutcome.TransportFailure
                        : OutgoingSendAttemptOutcome.MessageRefused,
                    Error = ex,
                    TransportToken = socket
                };
            }
        }

        void IOutgoingMessageHost.InvalidateTransport(OutgoingSendAttempt attempt, string reason)
        {
            InvalidateCurrentSocket(attempt?.TransportToken as IWhatsAppSocket, reason);
        }

        void IOutgoingMessageHost.RecordSendAttempt(string messageId)
        {
            Interlocked.Increment(ref _diagnosticsSendAttemptCount);
            Interlocked.Exchange(ref _diagnosticsLastSendAttemptUtcTicks, DateTime.UtcNow.Ticks);
            RuntimeDiagnosticsService.Instance.Write(
                "send",
                "text-attempt",
                "messageId=" + messageId + "; connected=" + IsConnected);
        }

        void IOutgoingMessageHost.RecordSendSuccess(string messageId)
        {
            Interlocked.Increment(ref _diagnosticsSendSuccessCount);
            Interlocked.Exchange(ref _diagnosticsLastSendSuccessUtcTicks, DateTime.UtcNow.Ticks);
            RuntimeDiagnosticsService.Instance.Write("send", "text-accepted", "messageId=" + messageId);
        }

        void IOutgoingMessageHost.RecordSendFailure(string messageId, Exception error)
        {
            Interlocked.Increment(ref _diagnosticsSendFailureCount);
            Interlocked.Exchange(ref _diagnosticsLastSendFailureUtcTicks, DateTime.UtcNow.Ticks);
            RuntimeDiagnosticsService.Instance.RecordException(
                "send",
                "text-failed",
                error,
                "messageId=" + messageId + "; connected=" + IsConnected);
            Debug.WriteLine($"[WhatsAppService] Message {messageId} failed before/while leaving client: {error?.Message}");
        }

        void IOutgoingMessageHost.ReportError(Exception error) => OnError?.Invoke(this, error);

        Task<List<ChatMessage>> IOutgoingMessageHost.LoadLiveOverlayAsync(string chatJid) =>
            LoadInitialMessagesAsync(chatJid);
    }
}
