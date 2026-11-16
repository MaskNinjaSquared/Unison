using System;
using System.Threading;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.Socket.Signal;
using Unison.WhatsAppClient.Chats;
using Unison.WhatsAppClient.Helpers;
using Unison.Uwp.Helpers;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {
        RuntimeDiagnosticsSnapshot IRuntimeSnapshotSource.CaptureRuntimeSnapshot()
        {
            var snapshot = new RuntimeDiagnosticsSnapshot
            {
                CapturedUtc = DateTime.UtcNow,
                ConnectionStatus = CurrentConnectionStatus,
                IsServiceConnected = IsConnected,
                IsConnecting = _isConnecting,
                SuppressReconnect = _sessionFlags.SuppressReconnect || _sessionFlags.FatalSessionEnded,
                HistorySyncProcessing = _initialSync.IsActive ||
                                        IsHistoryCatchUpBannerActive ||
                                        _historyRequests.IsFullClaimed,
                HistoryCatchUpBannerActive = IsHistoryCatchUpBannerActive,
                FullHistoryTriggerReason = null,
                FullHistoryAckAccepted = false,
                FullHistoryProgressSignalCount = 0,
                FullHistoryLastProgressReason = null,
                FullHistoryLastProgressUtc = DateTime.MinValue,
                EnrichmentAwaitingHeavyHistory = _historyCatchUp.HeavyHistoryEnrichment.IsAwaitingHeavyHistory,
                LastHistorySyncReceivedUtc = _historyCatchUp.LastHistorySyncReceivedUtc,
                LastHistorySyncType = _historyCatchUp.LastHistorySyncType,
                SqliteHistoryConversationsAccumulated = _historyCatchUp.HistoryChunks.ConversationsAccumulated,
                InitialSyncProcessedConversations = _initialSync.ProcessedConversations,
                InitialSyncTotalConversations = _initialSync.TotalConversations,
                DecryptedEventCount = _decryptedEvents.Count,
                AppliedMessageCount = _incomingPump.AppliedCount,
                SendAttemptCount = Interlocked.Read(ref _diagnosticsSendAttemptCount),
                SendSuccessCount = Interlocked.Read(ref _diagnosticsSendSuccessCount),
                SendFailureCount = Interlocked.Read(ref _diagnosticsSendFailureCount),
                LastConnectionEventUtc = DiagnosticUtcTicks.ToDateTime(Interlocked.Read(ref _diagnosticsLastConnectionEventUtcTicks)),
                LastDecryptedEventUtc = DiagnosticUtcTicks.ToDateTime(_decryptedEvents.LastUtcTicks),
                LastAppliedMessageUtc = DiagnosticUtcTicks.ToDateTime(
                    _incomingPump.LastAppliedUtcTicks),
                LastSendAttemptUtc = DiagnosticUtcTicks.ToDateTime(Interlocked.Read(ref _diagnosticsLastSendAttemptUtcTicks)),
                LastSendSuccessUtc = DiagnosticUtcTicks.ToDateTime(Interlocked.Read(ref _diagnosticsLastSendSuccessUtcTicks)),
                LastSendFailureUtc = DiagnosticUtcTicks.ToDateTime(Interlocked.Read(ref _diagnosticsLastSendFailureUtcTicks)),
                MemoryUsageBytes = _appMemory.UsageBytes,
                MemoryLimitBytes = _appMemory.LimitBytes,
                MemoryUsageLevel = _appMemory.UsageLevel,
                WebPSupportEnabled = Unison.Uwp.Helpers.WebPHelpers.HasWebPCodec,
                WebPApiContract7 = Unison.Uwp.Helpers.WebPHelpers.HasApiContract7,
                WebPDecoderIdListed = Unison.Uwp.Helpers.WebPHelpers.WebPDecoderIdListed,
                WebPDecodeStatus = Unison.Uwp.Helpers.WebPDecoder.LastDecodeStatus,
                WebPLibInPackage = Unison.Uwp.Helpers.WebPDecoder.LibWebPInPackage,
                WebPSharpYuvInPackage = Unison.Uwp.Helpers.WebPDecoder.LibSharpYuvInPackage
            };

            snapshot.IsReconnecting = _softReconnect.IsReconnecting;

            IncomingQueueSnapshot incoming = _incomingPump.Snapshot();
            snapshot.LiveIncomingQueueDepth = incoming.LiveDepth;
            snapshot.OfflineIncomingQueueDepth = incoming.OfflineDepth;
            snapshot.IncomingPumpRunning = incoming.Running;
            snapshot.IncomingPumpGeneration = incoming.Generation;
            snapshot.IncomingPumpStage = incoming.Stage;
            snapshot.IncomingPumpMessageId = incoming.CurrentMessageId;
            snapshot.IncomingPumpStageUtc = DiagnosticUtcTicks.ToDateTime(incoming.StageUtcTicks);

            snapshot.PersistPending = _catalogPersistence.IsPersistPending;

            snapshot.OfflinePersistPendingMessageCount = _offlineReplayFlush.PendingCount;
            snapshot.OfflineReplayFlushRequested = _offlineReplayFlush.IsFlushClaimed;

            var socket = _socketSlot.Current;
            if (socket != null)
            {
                snapshot.IsSocketConnected = socket.IsConnected;
                snapshot.IsHandshakeComplete = socket.IsHandshakeComplete;
                snapshot.TransportName = socket.TransportName;
                snapshot.IsSocketOwnedByBroker = socket.IsSocketOwnedByBroker;
                snapshot.LastInboundFrameUtc = socket.LastInboundFrameUtc;
                snapshot.LastNodeProgressUtc = socket.LastNodeProcessingProgressUtc;
                snapshot.SocketNodeQueueDepth = socket.QueuedNodeProcessingCount;
                snapshot.PendingQueryCount = socket.PendingQueryCount;
                snapshot.InboundFrameCount = socket.InboundFrameCount;
                snapshot.DecodedNodeCount = socket.DecodedNodeCount;
            }

            LidMappingStore lidStore = _lidMappings;
            if (lidStore != null)
            {
                snapshot.LidCacheEntries = lidStore.MemoryCacheCount;
                snapshot.LidCacheMaxLockWaitMs = lidStore.MemoryCacheMaxLockWaitMs;
            }

            try
            {
                IChatService chatsFacade = ChatFacade;
                snapshot.OfflineTipBusy = chatsFacade != null && chatsFacade.IsOfflineReplayTipBusy;
            }
            catch
            {
                snapshot.OfflineTipBusy = false;
            }

            try
            {
                snapshot.LoadedChatCount = Chats.Count;
                snapshot.LoadedMessageCount = _chatState.MessagesByChat.TotalMessageCount();
            }
            catch
            {
                // Collection ownership is still being refactored. A diagnostic read
                // must never interfere with the current UI/protocol paths.
            }

            _historyRequests.ReadFullDiagnostics(out bool fullPending, out string fullId, out HistoryOnDemandRequest fullState);
            snapshot.FullHistoryOnDemandPending = fullPending;
            snapshot.FullHistoryOnDemandRequestId = fullId;
            if (fullState != null)
            {
                snapshot.FullHistoryTriggerReason = fullState.TriggerReason ?? fullState.Marker;
                snapshot.FullHistoryAckAccepted = fullState.AckAccepted;
                snapshot.FullHistoryProgressSignalCount = fullState.ProgressSignalCount;
                snapshot.FullHistoryLastProgressReason = fullState.LastProgressReason;
                snapshot.FullHistoryLastProgressUtc = fullState.LastProgressUtc;
            }

            return snapshot;
        }
    }
}