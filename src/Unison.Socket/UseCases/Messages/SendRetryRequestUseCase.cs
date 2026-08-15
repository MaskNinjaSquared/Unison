// =============================================================================
// SendRetryRequestUseCase
//
// Asks the sender to encrypt a message again after we failed to read it.
//
// The receipt itself is small; what matters is what goes with it. On the first
// attempt we just ask. From the second we include a full key bundle, because by
// then the likely cause is that the session is broken and the sender needs new
// material to rebuild it - and when the manager says the session should be
// recreated, we delete our side first so the retry arrives as a fresh pkmsg.
//
// The current implementation gets the escalation roughly right but never deletes
// the session and never consults the error code the peer sent, so a MAC failure
// retries five times against the same broken session and then gives up.
//
// Ports: rc14 sendRetryRequest in src/Socket/messages-recv.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Messages;
using Unison.Socket.Session;
using Unison.Socket.Signal;

namespace Unison.Socket.UseCases.Messages
{
    public sealed class SendRetryRequestUseCase
    {
        private readonly ConnectionHandler _connection;
        private readonly AuthState _authState;
        private readonly MessageRetryManager _retries;
        private readonly ISignalRepository _signal;
        private readonly IPreKeyProvider _preKeys;
        private readonly ISocketLog _log;

        public SendRetryRequestUseCase(
            ConnectionHandler connection,
            AuthState authState,
            MessageRetryManager retries,
            ISignalRepository signal,
            IPreKeyProvider preKeys,
            ISocketLog log = null)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            if (authState == null)
            {
                throw new ArgumentNullException(nameof(authState));
            }

            if (retries == null)
            {
                throw new ArgumentNullException(nameof(retries));
            }

            _connection = connection;
            _authState = authState;
            _retries = retries;
            _signal = signal;
            _preKeys = preKeys;
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>
        /// Whether a failing session may be deleted so the peer rebuilds it. Off by default in
        /// Baileys; worth enabling once the rewrite owns the receive path.
        /// </summary>
        public bool EnableAutoSessionRecreation { get; set; }

        /// <summary>
        /// Called for the first two attempts to ask the phone to resend the message outright,
        /// which recovers messages whose session is beyond repair. Optional.
        /// </summary>
        public Func<BinaryNode, Task> RequestPlaceholderResend { get; set; }

        /// <returns>
        /// True when a retry receipt went out. False when the message has already been retried
        /// too often, in which case the caller should stop asking and nack the stanza.
        /// </returns>
        public async Task<bool> ExecuteAsync(BinaryNode node, bool forceIncludeKeys = false)
        {
            if (node == null)
            {
                return false;
            }

            var messageId = node.GetAttribute("id");
            if (string.IsNullOrEmpty(messageId))
            {
                return false;
            }

            if (_retries.HasExceededMaxRetries(messageId))
            {
                _log.Debug("[Retry] Giving up on " + messageId + ": retry limit reached");
                _retries.MarkRetryFailed(messageId);
                return false;
            }

            var retryCount = _retries.IncrementRetryCount(messageId);
            var from = node.GetAttribute("from");

            var shouldRecreateSession = false;
            if (EnableAutoSessionRecreation && _signal != null && retryCount > 1 && !string.IsNullOrEmpty(from))
            {
                shouldRecreateSession = await TryRecreateSessionAsync(from, retryCount).ConfigureAwait(false);
                if (shouldRecreateSession)
                {
                    forceIncludeKeys = true;
                }
            }

            if (retryCount <= 2 && RequestPlaceholderResend != null)
            {
                // Delayed, so a retry that succeeds on its own cancels the request before it
                // ever reaches the phone.
                _retries.SchedulePhoneRequest(messageId, () => RequestPlaceholderResend(node));
            }

            var content = new List<BinaryNode>
            {
                new BinaryNode(
                    "retry",
                    new Dictionary<string, string>
                    {
                        { "count", retryCount.ToString() },
                        { "id", messageId },
                        { "t", node.GetAttribute("t") },
                        { "v", "1" },
                        { "error", "0" }
                    }),
                new BinaryNode("registration", null, KeyBundleNodes.EncodeBigEndian(_authState.RegistrationId))
            };

            if (retryCount > 1 || forceIncludeKeys)
            {
                var keys = await BuildKeyBundleAsync().ConfigureAwait(false);
                if (keys != null)
                {
                    content.Add(keys);
                }
            }

            var attrs = new Dictionary<string, string>
            {
                { "id", messageId },
                { "type", "retry" },
                { "to", from }
            };

            var recipient = node.GetAttribute("recipient");
            if (!string.IsNullOrEmpty(recipient))
            {
                attrs["recipient"] = recipient;
            }

            var participant = node.GetAttribute("participant");
            if (!string.IsNullOrEmpty(participant))
            {
                attrs["participant"] = participant;
            }

            await _connection.SendNodeAsync(new BinaryNode("receipt", attrs, content)).ConfigureAwait(false);
            _log.Info("[Retry] Asked " + from + " to resend " + messageId + " (attempt " + retryCount + ")");
            return true;
        }

        /// <summary>
        /// Drops our session with the sender when the retry manager judges it unrecoverable, so
        /// the resent message can open a new one.
        /// </summary>
        private async Task<bool> TryRecreateSessionAsync(string from, int retryCount)
        {
            try
            {
                var validation = await _signal.ValidateSessionAsync(from).ConfigureAwait(false);
                var decision = _retries.ShouldRecreateSession(from, validation != null && validation.Exists);
                if (!decision.Recreate)
                {
                    return false;
                }

                _log.Debug("[Retry] Recreating session with " + from + " (attempt " + retryCount + "): " + decision.Reason);
                await _signal.DeleteSessionsAsync(new[] { from }).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _log.Warn("[Retry] Could not evaluate session recreation for " + from, ex);
                return false;
            }
        }

        /// <summary>
        /// Everything the sender needs to open a fresh session with us. Returns null when no
        /// prekey is available, in which case the receipt still goes out without one.
        /// </summary>
        private async Task<BinaryNode> BuildKeyBundleAsync()
        {
            if (_preKeys == null || _authState.SignedIdentityKey == null)
            {
                return null;
            }

            PreKeyRecord preKey;
            try
            {
                preKey = await _preKeys.GetNextPreKeyAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("[Retry] Could not allocate a prekey for the retry receipt", ex);
                return null;
            }

            if (preKey == null || preKey.PublicKey == null)
            {
                return null;
            }

            var signedPreKey = KeyBundleNodes.SignedPreKey(_authState.SignedPreKey);
            if (signedPreKey == null)
            {
                return null;
            }

            return new BinaryNode("keys", null, new List<BinaryNode>
            {
                new BinaryNode("type", null, KeyBundleNodes.KeyBundleType),
                new BinaryNode("identity", null, _authState.SignedIdentityKey.Public),
                KeyBundleNodes.PreKey(preKey.KeyId, preKey.PublicKey),
                signedPreKey,
                new BinaryNode(
                    "device-identity",
                    null,
                    KeyBundleNodes.EncodeSignedDeviceIdentity(_authState.Account, true))
            });
        }
    }
}
