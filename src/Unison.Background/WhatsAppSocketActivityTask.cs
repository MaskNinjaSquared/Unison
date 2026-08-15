using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.Networking.Sockets;
using Unison.Baileys.Protocol;

namespace Unison.Background
{
    /// <summary>
    /// Out-of-process SocketActivityTrigger task. It never initializes App/XAML.
    /// It checkpoints Noise frames, attempts Signal preview over an isolated snapshot,
    /// and retains the proven generic toast as a mandatory failure fallback.
    /// </summary>
    public sealed class WhatsAppSocketActivityTask : IBackgroundTask
    {
        private CancellationTokenSource _cancellation;

        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            BackgroundTaskDeferral deferral = taskInstance.GetDeferral();
            _cancellation = new CancellationTokenSource();
            taskInstance.Canceled += OnCanceled;
            BrokerInterprocessLease lease = null;
            try
            {
                var details = taskInstance.TriggerDetails as SocketActivityTriggerDetails;
                if (details == null || details.SocketInformation == null)
                {
                    await BrokerLog.AppendAsync(
                        "background",
                        "activation-without-socket-details");
                    return;
                }

                SocketActivityInformation information = details.SocketInformation;
                string socketId = information.Id ?? string.Empty;
                await BrokerLog.AppendAsync(
                    "background",
                    "background-activated reason=" + details.Reason +
                    " id=" + socketId);

                if (!BrokerOwnershipStore.IsManagedSocketId(socketId))
                {
                    await BrokerLog.AppendAsync(
                        "background",
                        "foreign-socket-id id=" + socketId);
                    return;
                }

                lease = await BrokerInterprocessLock.AcquireAsync(
                    "background:" + details.Reason,
                    TimeSpan.FromSeconds(8),
                    _cancellation.Token);
                if (lease == null)
                {
                    await TryReturnWithoutProcessingAsync(information);
                    return;
                }

                switch (details.Reason)
                {
                    case SocketActivityTriggerReason.SocketActivity:
                        await HandleSocketActivityAsync(
                            information,
                            _cancellation.Token);
                        break;
                    case SocketActivityTriggerReason.KeepAliveTimerExpired:
                        await HandleKeepAliveAsync(
                            information,
                            _cancellation.Token);
                        break;
                    case SocketActivityTriggerReason.SocketClosed:
                        await HandleSocketClosedAsync(information);
                        break;
                    default:
                        await BrokerLog.AppendAsync(
                            "background",
                            "unhandled-reason=" + details.Reason);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                await BrokerLog.AppendAsync("background", "background-cancelled");
            }
            catch (Exception ex)
            {
                await BrokerLog.AppendAsync(
                    "background",
                    "background-fatal error=" + ex.GetType().Name +
                    " hresult=0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture));
            }
            finally
            {
                if (lease != null)
                {
                    await lease.ReleaseAsync();
                }
                taskInstance.Canceled -= OnCanceled;
                _cancellation.Dispose();
                deferral.Complete();
            }
        }

        private static async Task HandleSocketActivityAsync(
            SocketActivityInformation information,
            CancellationToken token)
        {
            StreamSocket socket = information.StreamSocket;
            if (socket == null)
            {
                await BrokerLog.AppendAsync(
                    "background",
                    "socket-activity-missing-streamsocket");
                await BrokerOwnershipStore.MarkReconnectRequiredAsync(
                    information.Id,
                    "missing-streamsocket");
                return;
            }

            await SaveOwnerAsync(information.Id, "background", "socket-activity");
            RawWebSocketConnection connection = null;
            bool shouldReturn = true;
            try
            {
                connection = new RawWebSocketConnection(socket);
                RawWebSocketMessage message = await connection.ReadMessageAsync(token);
                if (message.Type == RawWebSocketMessageType.Binary)
                {
                    await HandleBinaryMessageAsync(
                        information.Id,
                        message.Payload);
                }
                else if (message.Type == RawWebSocketMessageType.Close)
                {
                    shouldReturn = false;
                    await BrokerLog.AppendAsync(
                        "background",
                        "websocket-close code=" + message.CloseCode);
                    await BrokerOwnershipStore.MarkReconnectRequiredAsync(
                        information.Id,
                        "websocket-close-" + message.CloseCode);
                    string toastError;
                    BackgroundToastPresenter.ShowReconnectRequired(out toastError);
                }
                else
                {
                    await BrokerLog.AppendAsync(
                        "background",
                        "control-frame type=" + message.Type +
                        " bytes=" + (message.Payload == null ? 0 : message.Payload.Length));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                shouldReturn = false;
                await BrokerLog.AppendAsync(
                    "background",
                    "socket-read-failed error=" + ex.GetType().Name +
                    " hresult=0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) +
                    " msg=" + (ex.Message ?? string.Empty) +
                    " (EndOfStream during foreground reclaim is often expected)");
                await BrokerOwnershipStore.MarkReconnectRequiredAsync(
                    information.Id,
                    "socket-read-failed");
            }
            finally
            {
                if (connection != null)
                {
                    connection.DetachStreams();
                    connection.Dispose();
                }

                if (shouldReturn)
                {
                    await ReturnOwnershipAsync(
                        socket,
                        information.Id,
                        "socket-activity");
                }
                else
                {
                    try { socket.Dispose(); } catch { }
                }
            }
        }

        private static async Task HandleBinaryMessageAsync(
            string socketId,
            byte[] payload)
        {
            NoiseSessionState effectiveState =
                await LoadEffectiveNoiseStateAsync(socketId);
            if (effectiveState == null)
            {
                await JournalRawAndShowFallbackAsync(
                    payload,
                    "noise-state-unavailable");
                return;
            }

            BackgroundNoiseDecodeResult decoded;
            try
            {
                decoded = BackgroundNoiseDecoder.Decode(
                    payload,
                    effectiveState);
            }
            catch (Exception ex)
            {
                await BrokerLog.AppendAsync(
                    "background",
                    "noise-decode-failed error=" +
                    ex.GetType().Name +
                    " hresult=0x" +
                    ex.HResult.ToString(
                        "X8",
                        CultureInfo.InvariantCulture));
                await JournalRawAndShowFallbackAsync(
                    payload,
                    "noise-decode-failed");
                return;
            }

            // Journal first. The embedded post-state repairs a process termination
            // between this atomic rename and the standalone Noise-state rename.
            byte[] checkpoint;
            try
            {
                checkpoint = BrokerDecodedFrameEnvelope.Pack(
                    socketId,
                    decoded.Frames,
                    decoded.State);
            }
            catch (Exception ex)
            {
                await BrokerLog.AppendAsync(
                    "background",
                    "decoded-checkpoint-pack-failed error=" +
                    ex.GetType().Name +
                    " hresult=0x" +
                    ex.HResult.ToString(
                        "X8",
                        CultureInfo.InvariantCulture));
                await JournalRawAndShowFallbackAsync(
                    payload,
                    "decoded-checkpoint-pack-failed");
                return;
            }
            ulong sequence =
                await BrokerFrameJournal.EnqueueAndGetSequenceAsync(
                    checkpoint);
            await BrokerLog.AppendAsync(
                "background",
                "decoded-frame-journaled sequence=" + sequence +
                " websocketBytes=" + (payload == null ? 0 : payload.Length) +
                " noiseFrames=" + decoded.Frames.Count +
                " readCounter=" + decoded.State.ReadCounter);

            try
            {
                await BrokerNoiseSessionStore.SaveAsync(
                    decoded.State,
                    socketId);
                await BrokerLog.AppendAsync(
                    "background",
                    "noise-checkpoint-persisted sequence=" + sequence +
                    " readCounter=" + decoded.State.ReadCounter);
            }
            catch (Exception ex)
            {
                // The journal batch remains the authoritative recovery point.
                await BrokerLog.AppendAsync(
                    "background",
                    "noise-checkpoint-store-failed sequence=" + sequence +
                    " error=" + ex.GetType().Name +
                    " hresult=0x" +
                    ex.HResult.ToString(
                        "X8",
                        CultureInfo.InvariantCulture));
            }

            string replacementTag = BuildToastReplacementTag(sequence);
            bool senderPreviewShown = false;
            bool pendingToastShown = false;
            bool envelopeResolutionFailed = false;
            bool hasNotifiableMessageEnvelope = false;
            int messageEnvelopeCount = 0;
            int notifiableMessageEnvelopeCount = 0;
            try
            {
                BackgroundEnvelopePreviewResult envelopePreview =
                    await BackgroundEnvelopePreviewResolver
                        .ResolveNewestDetailedAsync(decoded.Frames);
                BackgroundNotificationContent senderPreview =
                    envelopePreview.Notification;
                messageEnvelopeCount =
                    envelopePreview.MessageEnvelopeCount;
                notifiableMessageEnvelopeCount =
                    envelopePreview.NotifiableMessageEnvelopeCount;
                hasNotifiableMessageEnvelope =
                    envelopePreview.HasNotifiableMessageEnvelope;
                string senderPreviewError = string.Empty;
                if (senderPreview != null)
                {
                    senderPreviewShown =
                        BackgroundToastPresenter.ShowRealMessage(
                            senderPreview,
                            replacementTag,
                            out senderPreviewError);
                }
                else if (hasNotifiableMessageEnvelope)
                {
                    string pendingToastError;
                    pendingToastShown =
                        BackgroundToastPresenter.ShowGenericFallback(
                            replacementTag,
                            out pendingToastError);
                    await BrokerLog.AppendAsync(
                        "notifications",
                        "generic-fallback-toast shown=" +
                        pendingToastShown +
                        " reason=message-preview-pending" +
                        " sequence=" + sequence +
                        " replaceable=true" +
                        " error=" +
                        (pendingToastError ?? string.Empty));
                }
                await BrokerLog.AppendAsync(
                    "notifications",
                    "sender-preview-attempt sequence=" + sequence +
                    " messageEnvelopes=" + messageEnvelopeCount +
                    " notifiableEnvelopes=" +
                    notifiableMessageEnvelopeCount +
                    " candidate=" + (senderPreview != null) +
                    " shown=" + senderPreviewShown +
                    " error=" + senderPreviewError);
            }
            catch (Exception ex)
            {
                envelopeResolutionFailed = true;
                await BrokerLog.AppendAsync(
                    "notifications",
                    "sender-preview-failed sequence=" + sequence +
                    " error=" + ex.GetType().Name +
                    ":0x" + ex.HResult.ToString(
                        "X8",
                        CultureInfo.InvariantCulture));
            }

            bool fullPreviewShown = false;
            string realError = string.Empty;
            int realCandidates = 0;
            int currentSequenceMessageNodes = 0;
            try
            {
                IList<BrokerJournalPendingEntry> pending =
                    await BrokerFrameJournal.ReadPendingAsync();
                BackgroundPreviewReplayResult preview =
                    await BackgroundMessagePreviewEngine
                        .ReplayForSequenceAsync(
                            pending,
                            sequence,
                            DateTime.UtcNow.AddSeconds(6));
                realCandidates = preview.Notifications.Count;
                foreach (BackgroundNotificationContent content in
                        preview.Notifications)
                {
                    string candidateError;
                    bool candidateShown =
                        BackgroundToastPresenter.ShowRealMessage(
                            content,
                            replacementTag,
                            out candidateError);
                    fullPreviewShown =
                        fullPreviewShown || candidateShown;
                    if (!candidateShown &&
                        !string.IsNullOrWhiteSpace(candidateError))
                    {
                        realError = candidateError;
                    }
                }
                currentSequenceMessageNodes =
                    preview.CurrentSequenceMessageNodes;

                await BrokerLog.AppendAsync(
                    "notifications",
                    "real-preview-attempt sequence=" + sequence +
                    " candidates=" + realCandidates +
                    " shown=" + fullPreviewShown +
                    " replayEntries=" + preview.ReplayedEntries +
                    " replayFrames=" + preview.ReplayedFrames +
                    " messageNodes=" + preview.DecodedMessageNodes +
                    " currentMessageNodes=" +
                    preview.CurrentSequenceMessageNodes +
                    " timedOut=" + preview.TimedOut +
                    " error=" + realError);
            }
            catch (Exception ex)
            {
                realError = ex.GetType().Name + ":0x" +
                            ex.HResult.ToString(
                                "X8",
                                CultureInfo.InvariantCulture);
                await BrokerLog.AppendAsync(
                    "notifications",
                    "real-preview-failed sequence=" + sequence +
                    " error=" + realError);
            }

            bool anyRealPreviewShown =
                senderPreviewShown || fullPreviewShown;
            if (!anyRealPreviewShown &&
                !pendingToastShown &&
                (hasNotifiableMessageEnvelope ||
                 (envelopeResolutionFailed &&
                  currentSequenceMessageNodes > 0)))
            {
                string fallbackError;
                pendingToastShown =
                    BackgroundToastPresenter.ShowGenericFallback(
                        replacementTag,
                        out fallbackError);
                await BrokerLog.AppendAsync(
                    "notifications",
                    "generic-fallback-toast shown=" +
                    pendingToastShown +
                    " reason=message-preview-unavailable" +
                    " sequence=" + sequence +
                    " replaceable=true" +
                    " error=" + (fallbackError ?? string.Empty));
            }

            if (!anyRealPreviewShown)
            {
                if (pendingToastShown)
                {
                    await BrokerLog.AppendAsync(
                        "notifications",
                        "generic-fallback-retained sequence=" + sequence +
                        " reason=" +
                        (realCandidates == 0
                            ? "message-content-unavailable"
                            : "real-toast-not-shown") +
                        " initiallyShown=true");
                }
                else
                {
                    await BrokerLog.AppendAsync(
                        "notifications",
                        "generic-fallback-suppressed sequence=" + sequence +
                        " reason=" +
                        (envelopeResolutionFailed
                            ? "unclassified-frame-without-message-node"
                            : (messageEnvelopeCount == 0
                                ? "non-message-frame"
                                : "non-notifiable-message")) +
                        " messageEnvelopes=" + messageEnvelopeCount +
                        " notifiableEnvelopes=" +
                        notifiableMessageEnvelopeCount);
                }
            }
            else if (senderPreviewShown && !fullPreviewShown)
            {
                await BrokerLog.AppendAsync(
                    "notifications",
                    "sender-preview-retained sequence=" + sequence +
                    " reason=full-content-unavailable");
            }
        }

        private static async Task<NoiseSessionState>
            LoadEffectiveNoiseStateAsync(string socketId)
        {
            BrokerNoiseSessionSnapshot snapshot =
                await BrokerNoiseSessionStore.LoadSnapshotAsync();
            NoiseSessionState effective =
                snapshot != null &&
                string.Equals(
                    snapshot.SocketId,
                    socketId,
                    StringComparison.Ordinal)
                    ? snapshot.State
                    : null;

            // A journal-first checkpoint can be newer than the standalone file if
            // Windows terminated the previous task at exactly that boundary.
            IList<BrokerJournalPendingEntry> pending =
                await BrokerFrameJournal.ReadPendingAsync();
            foreach (BrokerJournalPendingEntry entry in
                     pending.OrderBy(item => item.Sequence))
            {
                BrokerDecodedFrameBatch batch;
                if (!BrokerDecodedFrameEnvelope.TryUnpack(
                        entry.Payload,
                        out batch) ||
                    !string.Equals(
                        batch.SocketId,
                        socketId,
                        StringComparison.Ordinal) ||
                    batch.PostNoiseState == null ||
                    !batch.PostNoiseState.IsValidEstablishedState())
                {
                    continue;
                }

                if (effective == null ||
                    batch.PostNoiseState.ReadCounter >=
                    effective.ReadCounter)
                {
                    effective = batch.PostNoiseState;
                }
            }

            return BrokerNoiseSessionStore.CloneState(effective);
        }

        private static async Task JournalRawAndShowFallbackAsync(
            byte[] payload,
            string reason)
        {
            await BrokerFrameJournal.EnqueueAsync(payload);
            await BrokerLog.AppendAsync(
                "background",
                "raw-frame-journaled bytes=" +
                (payload == null ? 0 : payload.Length) +
                " reason=" + reason);
            await ShowFallbackAsync(reason);
        }

        private static async Task ShowFallbackAsync(string reason)
        {
            string toastError;
            bool shown = BackgroundToastPresenter.ShowGenericFallback(
                out toastError);
            await BrokerLog.AppendAsync(
                "notifications",
                "generic-fallback-toast shown=" + shown +
                " reason=" + reason +
                " error=" + (toastError ?? string.Empty));
        }

        private static string BuildToastReplacementTag(ulong sequence)
        {
            return "m" + sequence.ToString(
                "X",
                CultureInfo.InvariantCulture);
        }

        private static async Task HandleKeepAliveAsync(
            SocketActivityInformation information,
            CancellationToken token)
        {
            StreamSocket socket = information.StreamSocket;
            if (socket == null)
            {
                await BrokerOwnershipStore.MarkReconnectRequiredAsync(
                    information.Id,
                    "keepalive-missing-socket");
                return;
            }

            await SaveOwnerAsync(information.Id, "background", "keepalive");
            RawWebSocketConnection connection = null;
            try
            {
                connection = new RawWebSocketConnection(socket);
                byte[] payload = Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("O"));
                await connection.SendPingAsync(payload, token);
                await BrokerLog.AppendAsync(
                    "background",
                    "websocket-ping-sent");
            }
            finally
            {
                if (connection != null)
                {
                    connection.DetachStreams();
                    connection.Dispose();
                }
                await ReturnOwnershipAsync(socket, information.Id, "keepalive");
            }
        }

        private static async Task HandleSocketClosedAsync(
            SocketActivityInformation information)
        {
            await BrokerLog.AppendAsync(
                "background",
                "socket-closed id=" + information.Id);
            await BrokerOwnershipStore.MarkReconnectRequiredAsync(
                information.Id,
                "socket-closed-trigger");

            string toastError;
            bool alreadyActive;
            bool shown = BackgroundToastPresenter.ShowReconnectRequired(
                out alreadyActive,
                out toastError);
            await BrokerLog.AppendAsync(
                "notifications",
                "reconnect-required-toast shown=" + shown +
                " suppressed=" + alreadyActive +
                " error=" + (toastError ?? string.Empty));
        }

        private static async Task ReturnOwnershipAsync(
            StreamSocket socket,
            string socketId,
            string reason)
        {
            try
            {
                socket.TransferOwnership(socketId);
                await SaveOwnerAsync(socketId, "broker", reason);
                await BrokerLog.AppendAsync(
                    "background",
                    "ownership-returned id=" + socketId +
                    " reason=" + reason);
            }
            catch (Exception ex)
            {
                await BrokerLog.AppendAsync(
                    "background",
                    "ownership-return-failed id=" + socketId +
                    " reason=" + reason +
                    " error=" + ex.GetType().Name +
                    " hresult=0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture));
                await BrokerOwnershipStore.MarkReconnectRequiredAsync(
                    socketId,
                    "ownership-return-failed");
                try { socket.Dispose(); } catch { }
            }
        }

        private static async Task TryReturnWithoutProcessingAsync(
            SocketActivityInformation information)
        {
            try
            {
                StreamSocket socket = information.StreamSocket;
                if (socket != null)
                {
                    socket.TransferOwnership(information.Id);
                    await BrokerLog.AppendAsync(
                        "background",
                        "ownership-returned-without-processing id=" + information.Id);
                }
            }
            catch (Exception ex)
            {
                await BrokerLog.AppendAsync(
                    "background",
                    "ownership-emergency-return-failed error=" + ex.GetType().Name +
                    " hresult=0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture));
            }
        }

        private static async Task SaveOwnerAsync(
            string socketId,
            string owner,
            string reason)
        {
            BrokerOwnershipState state = await BrokerOwnershipStore.LoadAsync();
            if (state == null ||
                !string.Equals(state.SocketId, socketId, StringComparison.Ordinal))
            {
                state = BrokerOwnershipStore.Create(
                    socketId,
                    Guid.NewGuid().ToString("N"),
                    owner,
                    reason);
            }

            state.Owner = owner;
            state.ReconnectRequired = false;
            state.LastReason = reason;
            await BrokerOwnershipStore.SaveAsync(state);
        }

        private void OnCanceled(
            IBackgroundTaskInstance sender,
            BackgroundTaskCancellationReason reason)
        {
            try { _cancellation.Cancel(); } catch { }
            _ = BrokerLog.AppendAsync(
                "background",
                "cancellation-requested reason=" + reason);
        }
    }
}
