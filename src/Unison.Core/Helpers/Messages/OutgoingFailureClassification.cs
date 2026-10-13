// =============================================================================
// OutgoingFailureClassification and SelfChatStatusPolicy
//
// Three small rules from the send path that were stated inline, where they read
// as incidental conditions rather than as the decisions they are. The third is
// the text-send retry: when to drop the socket and when to stop.
// =============================================================================
using System;
using System.IO;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class OutgoingFailureClassification
    {
        /// <summary>
        /// Whether a failed send means the connection itself is gone, as opposed to the
        /// message having been refused by something above it.
        /// </summary>
        /// <remarks>
        /// The distinction decides whether the socket is torn down. Calling a refused
        /// message a transport failure drops a healthy connection over one bad message;
        /// calling a dead connection a message failure leaves every later send to fail the
        /// same way.
        /// </remarks>
        /// <param name="socketUsable">
        /// Whether the socket is still connected with its handshake complete. Passed in so
        /// the rule stays independent of the socket type.
        /// </param>
        public static bool IsTransportFailure(Exception error, bool socketUsable)
        {
            if (error is TimeoutException || error is IOException || error is TaskCanceledException)
            {
                return true;
            }

            // Whatever the exception was, a socket that is no longer usable is the more
            // likely explanation than the message itself.
            return !socketUsable;
        }
    }

    public static class SelfChatStatusPolicy
    {
        /// <summary>
        /// The status to show in the conversation a user has with themselves.
        /// </summary>
        /// <remarks>
        /// There is no second party to deliver to or to read it, so waiting for a receipt
        /// that will never come would leave the message on one tick forever. Anything that
        /// reached the server is therefore shown as read.
        ///
        /// Pending and failed pass through untouched: those describe whether the message
        /// got out at all, which is still a real question in a self chat.
        /// </remarks>
        public static string Resolve(string status, bool isSelfChat)
        {
            if (!isSelfChat || string.IsNullOrWhiteSpace(status))
            {
                return status;
            }

            bool reachedTheServer =
                string.Equals(status, ChatMessage.StatusSent, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, ChatMessage.StatusDelivered, StringComparison.OrdinalIgnoreCase);

            return reachedTheServer ? ChatMessage.StatusRead : status;
        }
    }

    /// <summary>What the text-send loop observed on one attempt.</summary>
    public enum OutgoingSendAttemptOutcome
    {
        /// <summary>No socket, or the handshake is not done. Nothing was written.</summary>
        SocketNotReady,

        /// <summary>The server took the message.</summary>
        Accepted,

        /// <summary>The pipe itself failed. The socket is no longer trustworthy.</summary>
        TransportFailure,

        /// <summary>Something above the pipe refused this message. The connection is still fine.</summary>
        MessageRefused
    }

    /// <summary>
    /// What to do after one text-send attempt: drop the socket, try again, or stop.
    /// </summary>
    public struct OutgoingTextSendStep
    {
        public bool InvalidateSocket;

        public bool Retry;

        public TimeSpan RetryDelay;
    }

    /// <summary>
    /// The bounded retry around a text send. Two attempts, a shorter connect budget on the
    /// second, and a refusal that must not take the connection down with it.
    /// </summary>
    public static class OutgoingTextSendRetry
    {
        public const int MaxAttempts = 2;

        public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(15);

        public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

        /// <summary>First attempt waits longer for a reconnect; the second does not.</summary>
        public static TimeSpan ConnectTimeout(int attempt)
        {
            return attempt <= 1
                ? TimeSpan.FromMilliseconds(35000)
                : TimeSpan.FromMilliseconds(25000);
        }

        public static OutgoingTextSendStep After(int attempt, OutgoingSendAttemptOutcome outcome)
        {
            if (outcome == OutgoingSendAttemptOutcome.Accepted ||
                outcome == OutgoingSendAttemptOutcome.MessageRefused)
            {
                return new OutgoingTextSendStep();
            }

            bool attemptsRemain = attempt < MaxAttempts;
            return new OutgoingTextSendStep
            {
                InvalidateSocket = outcome == OutgoingSendAttemptOutcome.TransportFailure,
                Retry = attemptsRemain,
                RetryDelay = attemptsRemain ? RetryDelay : TimeSpan.Zero
            };
        }
    }
}
