// =============================================================================
// SentAckWait
//
// One registration with MessageAckHandler: the server's answer for a single
// message we sent. A relay returns as soon as the stanza is on the wire, so
// this is the only way a caller learns the send was actually refused.
//
// Silence is read as taken. A missing ack must not fail a send that may well
// have landed; only an explicit error code is a refusal.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.Socket.Messages
{
    public sealed class SentAckWait : IDisposable
    {
        private readonly MessageAckHandler _owner;
        private readonly TaskCompletionSource<string> _source;
        private readonly string _messageId;
        private bool _disposed;

        internal SentAckWait(MessageAckHandler owner, string messageId, TaskCompletionSource<string> source)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _messageId = messageId;
        }

        /// <summary>
        /// The ack error code, or null when the server took the stanza or said nothing
        /// within <paramref name="timeout"/>.
        /// </summary>
        public async Task<string> WaitAsync(TimeSpan timeout)
        {
            Task<string> answer = _source.Task;
            if (answer.IsCompleted)
            {
                return answer.Result;
            }

            Task finished = await Task.WhenAny(answer, Task.Delay(timeout)).ConfigureAwait(false);
            return ReferenceEquals(finished, answer) ? answer.Result : null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.Forget(_messageId);
        }
    }
}
