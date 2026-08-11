using System;
using System.Text;

namespace Unison.Core.Models
{
    /// <summary>
    /// Immutable-at-capture diagnostic snapshot. It deliberately contains counters and
    /// transport state only; message bodies and contact names are never included.
    /// </summary>
    public sealed class RuntimeDiagnosticsSnapshot
    {
        public DateTime CapturedUtc { get; set; }
        public string ConnectionStatus { get; set; }
        public bool IsServiceConnected { get; set; }
        public bool IsSocketConnected { get; set; }
        public bool IsHandshakeComplete { get; set; }
        public string TransportName { get; set; }
        public bool IsSocketOwnedByBroker { get; set; }
        public bool IsConnecting { get; set; }
        public bool IsReconnecting { get; set; }
        public bool SuppressReconnect { get; set; }
        public bool IncomingPumpRunning { get; set; }
        public int IncomingPumpGeneration { get; set; }
        public string IncomingPumpStage { get; set; }
        public string IncomingPumpMessageId { get; set; }
        public DateTime IncomingPumpStageUtc { get; set; }
        public bool HistorySyncProcessing { get; set; }
        public bool PersistPending { get; set; }
        public bool OfflineReplayFlushRequested { get; set; }
        public int LiveIncomingQueueDepth { get; set; }
        public int OfflineIncomingQueueDepth { get; set; }
        public int SocketNodeQueueDepth { get; set; }
        public int PendingQueryCount { get; set; }
        public int OfflinePersistPendingMessageCount { get; set; }
        public int LoadedChatCount { get; set; }
        public int LoadedMessageCount { get; set; }
        public long InboundFrameCount { get; set; }
        public long DecodedNodeCount { get; set; }
        public long DecryptedEventCount { get; set; }
        public long AppliedMessageCount { get; set; }
        public long SendAttemptCount { get; set; }
        public long SendSuccessCount { get; set; }
        public long SendFailureCount { get; set; }
        public DateTime LastConnectionEventUtc { get; set; }
        public DateTime LastInboundFrameUtc { get; set; }
        public DateTime LastNodeProgressUtc { get; set; }
        public DateTime LastDecryptedEventUtc { get; set; }
        public DateTime LastAppliedMessageUtc { get; set; }
        public DateTime LastSendAttemptUtc { get; set; }
        public DateTime LastSendSuccessUtc { get; set; }
        public DateTime LastSendFailureUtc { get; set; }
        public ulong MemoryUsageBytes { get; set; }
        public ulong MemoryLimitBytes { get; set; }
        public string MemoryUsageLevel { get; set; }

        public bool IsPotentiallyStalled
        {
            get
            {
                if (!IsServiceConnected || IsSocketOwnedByBroker)
                {
                    return false;
                }

                if (LastInboundFrameUtc != DateTime.MinValue &&
                    CapturedUtc - LastInboundFrameUtc > TimeSpan.FromSeconds(70))
                {
                    return true;
                }

                if (SocketNodeQueueDepth > 0 && LastNodeProgressUtc != DateTime.MinValue &&
                    CapturedUtc - LastNodeProgressUtc > TimeSpan.FromSeconds(75))
                {
                    return true;
                }

                if ((LiveIncomingQueueDepth + OfflineIncomingQueueDepth) > 0 && !IncomingPumpRunning)
                {
                    return true;
                }

                if (IncomingPumpRunning &&
                    IncomingPumpStageUtc != DateTime.MinValue &&
                    CapturedUtc - IncomingPumpStageUtc > TimeSpan.FromSeconds(18) &&
                    (!string.IsNullOrWhiteSpace(IncomingPumpMessageId) ||
                     LiveIncomingQueueDepth + OfflineIncomingQueueDepth > 0))
                {
                    return true;
                }

                return false;
            }
        }

        public string ToCompactLine()
        {
            return string.Format(
                "status={0}; service={1}; socket={2}; handshake={3}; transport={4}; brokerOwned={5}; connecting={6}; reconnecting={7}; " +
                "qLive={8}; qOffline={9}; qNode={10}; pendingIq={11}; pump={12}; pumpStage={13}; pumpGen={14}; history={15}; " +
                "frames={16}; nodes={17}; decrypted={18}; applied={19}; send={20}/{21}/{22}; mem={23}/{24}MB; stalled={25}",
                ConnectionStatus ?? string.Empty,
                IsServiceConnected,
                IsSocketConnected,
                IsHandshakeComplete,
                TransportName ?? string.Empty,
                IsSocketOwnedByBroker,
                IsConnecting,
                IsReconnecting,
                LiveIncomingQueueDepth,
                OfflineIncomingQueueDepth,
                SocketNodeQueueDepth,
                PendingQueryCount,
                IncomingPumpRunning,
                IncomingPumpStage ?? string.Empty,
                IncomingPumpGeneration,
                HistorySyncProcessing,
                InboundFrameCount,
                DecodedNodeCount,
                DecryptedEventCount,
                AppliedMessageCount,
                SendAttemptCount,
                SendSuccessCount,
                SendFailureCount,
                MemoryUsageBytes / (1024UL * 1024UL),
                MemoryLimitBytes / (1024UL * 1024UL),
                IsPotentiallyStalled);
        }

        public string ToDisplayText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("UNISON RUNTIME HEALTH");
            sb.AppendLine("Captured (UTC): " + FormatUtc(CapturedUtc));
            sb.AppendLine();
            sb.AppendLine("Connection");
            sb.AppendLine("  Status: " + (ConnectionStatus ?? "<none>"));
            sb.AppendLine("  Service ready: " + YesNo(IsServiceConnected));
            sb.AppendLine("  WebSocket: " + YesNo(IsSocketConnected));
            sb.AppendLine("  Handshake: " + YesNo(IsHandshakeComplete));
            sb.AppendLine("  Transport: " + (TransportName ?? "<none>"));
            sb.AppendLine("  Socket owned by broker: " + YesNo(IsSocketOwnedByBroker));
            sb.AppendLine("  Connecting / reconnecting: " + YesNo(IsConnecting) + " / " + YesNo(IsReconnecting));
            sb.AppendLine("  Reconnect suppressed: " + YesNo(SuppressReconnect));
            sb.AppendLine();
            sb.AppendLine("Queues");
            sb.AppendLine("  Live / offline messages: " + LiveIncomingQueueDepth + " / " + OfflineIncomingQueueDepth);
            sb.AppendLine("  Incoming pump running: " + YesNo(IncomingPumpRunning));
            sb.AppendLine("  Incoming pump stage: " + (IncomingPumpStage ?? "<none>"));
            sb.AppendLine("  Incoming pump generation: " + IncomingPumpGeneration);
            sb.AppendLine("  Incoming pump message: " + (IncomingPumpMessageId ?? "<none>"));
            sb.AppendLine("  Incoming pump stage time: " + FormatUtc(IncomingPumpStageUtc));
            sb.AppendLine("  Protocol nodes: " + SocketNodeQueueDepth);
            sb.AppendLine("  Pending IQ queries: " + PendingQueryCount);
            sb.AppendLine("  Pending persistence messages: " + OfflinePersistPendingMessageCount);
            sb.AppendLine("  History sync: " + YesNo(HistorySyncProcessing));
            sb.AppendLine();
            sb.AppendLine("Counters");
            sb.AppendLine("  Inbound frames / decoded nodes: " + InboundFrameCount + " / " + DecodedNodeCount);
            sb.AppendLine("  Decrypted / applied: " + DecryptedEventCount + " / " + AppliedMessageCount);
            sb.AppendLine("  Send attempts / success / failure: " + SendAttemptCount + " / " + SendSuccessCount + " / " + SendFailureCount);
            sb.AppendLine("  Loaded chats / messages: " + LoadedChatCount + " / " + LoadedMessageCount);
            sb.AppendLine();
            sb.AppendLine("Last activity (UTC)");
            sb.AppendLine("  Connection event: " + FormatUtc(LastConnectionEventUtc));
            sb.AppendLine("  Inbound frame: " + FormatUtc(LastInboundFrameUtc));
            sb.AppendLine("  Protocol progress: " + FormatUtc(LastNodeProgressUtc));
            sb.AppendLine("  Decrypted event: " + FormatUtc(LastDecryptedEventUtc));
            sb.AppendLine("  Applied message: " + FormatUtc(LastAppliedMessageUtc));
            sb.AppendLine("  Send attempt: " + FormatUtc(LastSendAttemptUtc));
            sb.AppendLine("  Send success: " + FormatUtc(LastSendSuccessUtc));
            sb.AppendLine("  Send failure: " + FormatUtc(LastSendFailureUtc));
            sb.AppendLine();
            sb.AppendLine("Memory");
            sb.AppendLine("  Usage / limit: " + (MemoryUsageBytes / (1024UL * 1024UL)) + " / " + (MemoryLimitBytes / (1024UL * 1024UL)) + " MB");
            sb.AppendLine("  Level: " + (MemoryUsageLevel ?? "unknown"));
            sb.AppendLine();
            sb.AppendLine("Potential stall detected: " + YesNo(IsPotentiallyStalled));
            return sb.ToString();
        }

        private static string YesNo(bool value)
        {
            return value ? "yes" : "no";
        }

        private static string FormatUtc(DateTime value)
        {
            return value == DateTime.MinValue ? "<none>" : value.ToUniversalTime().ToString("O");
        }
    }
}
