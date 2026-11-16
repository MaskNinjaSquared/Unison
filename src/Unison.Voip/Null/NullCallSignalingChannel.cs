using System.Threading;
using System.Threading.Tasks;
using Unison.Voip.Abstractions;

namespace Unison.Voip.Null
{
    public sealed class NullCallSignalingChannel : ICallSignalingChannel
    {
        public static NullCallSignalingChannel Instance { get; } = new NullCallSignalingChannel();

        public int RejectCount { get; private set; }

        public int TerminateCount { get; private set; }

        public string LastCallId { get; private set; }

        public string LastReason { get; private set; }

        public Task RejectAsync(
            string callId,
            string toJid,
            string callCreatorJid,
            CancellationToken cancellationToken)
        {
            RejectCount++;
            LastCallId = callId;
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
            LastReason = reason;
            return Task.CompletedTask;
        }
    }
}
