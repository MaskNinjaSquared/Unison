using System.Threading;
using System.Threading.Tasks;
using Unison.Voip.Abstractions;

namespace Unison.Voip.Null
{
    public sealed class NullCallSignalingChannel : ICallSignalingChannel
    {
        public static NullCallSignalingChannel Instance { get; } = new NullCallSignalingChannel();

        public int OfferCount { get; private set; }

        public int PreacceptCount { get; private set; }

        public int AcceptCount { get; private set; }

        public int RejectCount { get; private set; }

        public int TerminateCount { get; private set; }

        public string LastCallId { get; private set; }

        public string LastToJid { get; private set; }

        public string LastReason { get; private set; }

        public bool LastAcceptWasVideo { get; private set; }

        public CallOfferWireResult LastOffer { get; private set; }

        public Task<CallOfferWireResult> OfferAudioAsync(
            string peerJid,
            CancellationToken cancellationToken)
        {
            OfferCount++;
            LastToJid = peerJid;
            LastOffer = new CallOfferWireResult
            {
                CallId = "test-call",
                PeerJid = peerJid,
                CallCreatorJid = "self@lid",
                CallKey = new byte[32]
            };
            LastCallId = LastOffer.CallId;
            return Task.FromResult(LastOffer);
        }

        public Task PreacceptAsync(
            string callId,
            string toJid,
            string callCreatorJid,
            CancellationToken cancellationToken)
        {
            PreacceptCount++;
            LastCallId = callId;
            LastToJid = toJid;
            return Task.CompletedTask;
        }

        public Task AcceptAsync(
            string callId,
            string toJid,
            string callCreatorJid,
            bool isVideo,
            CancellationToken cancellationToken)
        {
            AcceptCount++;
            LastCallId = callId;
            LastToJid = toJid;
            LastAcceptWasVideo = isVideo;
            return Task.CompletedTask;
        }

        public Task RejectAsync(
            string callId,
            string toJid,
            string callCreatorJid,
            CancellationToken cancellationToken)
        {
            RejectCount++;
            LastCallId = callId;
            LastToJid = toJid;
            return Task.CompletedTask;
        }

        public Task TerminateAsync(
            string callId,
            string toJid,
            string callCreatorJid,
            string reason,
            CancellationToken cancellationToken)
        {
            TerminateCount++;
            LastCallId = callId;
            LastToJid = toJid;
            LastReason = reason;
            return Task.CompletedTask;
        }

        public int RelayLatencyCount { get; private set; }

        public string LastRelayName { get; private set; }

        public uint LastLatencyMs { get; private set; }

        public Task SendRelayLatencyAsync(
            string callId,
            string toJid,
            string callCreatorJid,
            uint latencyMs,
            string relayName,
            byte[] addressBytes,
            CancellationToken cancellationToken)
        {
            RelayLatencyCount++;
            LastCallId = callId;
            LastToJid = toJid;
            LastRelayName = relayName;
            LastLatencyMs = latencyMs;
            return Task.CompletedTask;
        }
    }
}
