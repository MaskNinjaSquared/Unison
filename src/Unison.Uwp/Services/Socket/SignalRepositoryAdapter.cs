// =============================================================================
// SignalRepositoryAdapter
//
// Presents the app's existing SignalHandler as the socket layer's
// ISignalRepository.
//
// This is the seam that lets the rewrite encrypt and decrypt without owning a
// single ratchet. SignalHandler stays exactly where it is, keeps its key store
// and its persistence, and the new send and receive paths talk to it through the
// interface Baileys defines - so the day the crypto is replaced, only this file
// changes.
//
// The methods are synchronous underneath, so the tasks complete inline; they are
// async in the interface because a future implementation almost certainly is.
//
// This is also where a contact's two addresses become one. rc14 resolves a phone
// number to its LID inside the session store, so every operation - opening a
// session, encrypting, checking whether one exists - lands on the same record no
// matter which address the caller happened to have. SignalHandler keys its
// sessions by the literal JID, so the resolution has to happen here instead:
// without it the receive path advances the ratchet stored under the LID while
// the send path advances the one under the phone number, the two drift apart,
// and everything we send arrives as a message the recipient cannot open.
//
// Ports: rc14 resolveLIDSignalAddress and migrateSession in
// src/Signal/libsignal.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Socket.Signal;
using Unison.Socket.WABinary;

namespace Unison.Uwp.Services.Socket
{
    public sealed class SignalRepositoryAdapter : ISignalRepository
    {
        private readonly SignalHandler _signal;
        private readonly LidMappingStore _lidMapping;

        public SignalRepositoryAdapter(SignalHandler signal, LidMappingStore lidMapping)
        {
            if (signal == null)
            {
                throw new ArgumentNullException(nameof(signal));
            }

            _signal = signal;
            _lidMapping = lidMapping;
        }

        public LidMappingStore LidMapping
        {
            get { return _lidMapping; }
        }

        public async Task<EncryptedPayload> EncryptMessageAsync(string jid, byte[] plaintext)
        {
            var sessionJid = await ResolveSessionJidAsync(jid).ConfigureAwait(false);
            var result = _signal.EncryptMessage(plaintext, sessionJid);
            if (result == null)
            {
                return null;
            }

            // Encrypting advances the sending chain. Leaving that in memory means a restart
            // rewinds the counter and the next message repeats one the peer already consumed,
            // which they answer by showing the message as still on its way.
            await _signal.SaveSessionAsync(sessionJid).ConfigureAwait(false);

            return new EncryptedPayload { Type = result.Type, Ciphertext = result.Ciphertext };
        }

        public Task<GroupEncryptedPayload> EncryptGroupMessageAsync(string groupJid, string senderJid, byte[] plaintext)
        {
            var result = _signal.EncryptGroupMessage(groupJid, senderJid, plaintext);
            return Task.FromResult(result == null
                ? null
                : new GroupEncryptedPayload
                {
                    Ciphertext = result.Ciphertext,
                    SenderKeyDistributionMessage = result.SenderKeyDistributionMessage,
                    KeyId = result.KeyId,
                    CreatedNewSenderKey = result.CreatedNewSenderKey
                });
        }

        public async Task<byte[]> DecryptMessageAsync(
            string senderJid,
            string type,
            byte[] ciphertext,
            string groupJid = null,
            string alternateSenderJid = null)
        {
            // The caller resolved a phone number to its LID and handed us both. Copying the
            // session across before reading keeps the LID record the live one; otherwise this
            // message would advance the phone-number ratchet that the send path no longer uses.
            // Group sender keys are exempt: they are filed under the identity that signed them.
            if (type != "skmsg" &&
                JidUtils.IsAnyLid(senderJid) &&
                !string.IsNullOrEmpty(alternateSenderJid) &&
                JidUtils.IsPnUser(alternateSenderJid))
            {
                await _signal.CloneSessionAliasAsync(alternateSenderJid, senderJid).ConfigureAwait(false);
            }

            return _signal.DecryptMessage(ciphertext, senderJid, type, groupJid, alternateSenderJid);
        }

        public Task<byte[]> GetSenderKeyDistributionMessageAsync(string groupJid, string senderJid)
        {
            return Task.FromResult(_signal.GetSenderKeyDistributionMessage(groupJid, senderJid));
        }

        public Task<bool> HasSenderKeyAsync(string groupJid, string senderJid)
        {
            byte[] distribution;
            return Task.FromResult(_signal.TryGetSenderKeyDistributionMessage(groupJid, senderJid, out distribution));
        }

        public Task ProcessSenderKeyDistributionMessageAsync(
            string authorJid,
            global::Proto.Message.Types.SenderKeyDistributionMessage distribution)
        {
            _signal.ProcessSenderKeyDistribution(authorJid, distribution);
            return Task.FromResult(true);
        }

        public async Task InjectE2ESessionAsync(string jid, PreKeyBundle bundle)
        {
            var sessionJid = await ResolveSessionJidAsync(jid).ConfigureAwait(false);
            _signal.InitializeOutgoingSession(sessionJid, bundle);
            await _signal.SaveSessionAsync(sessionJid).ConfigureAwait(false);
        }

        public async Task<SessionValidation> ValidateSessionAsync(string jid)
        {
            var sessionJid = await ResolveSessionJidAsync(jid).ConfigureAwait(false);
            return _signal.HasSession(sessionJid)
                ? new SessionValidation(true)
                : new SessionValidation(false, "no session record");
        }

        public async Task DeleteSessionsAsync(IEnumerable<string> jids)
        {
            if (jids == null)
            {
                return;
            }

            foreach (var jid in jids)
            {
                if (string.IsNullOrEmpty(jid))
                {
                    continue;
                }

                // Both addresses go, so a stale phone-number record cannot come back as the
                // answer to the next lookup.
                var sessionJid = await ResolveSessionJidAsync(jid).ConfigureAwait(false);
                await _signal.ResetSessionAsync(jid).ConfigureAwait(false);

                if (sessionJid != jid)
                {
                    await _signal.ResetSessionAsync(sessionJid).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// The address a contact's session is filed under: their LID when we know it, and their
        /// phone number otherwise. A session that only exists under the phone number is copied
        /// across the first time, the way rc14 migrates one, so a conversation that predates the
        /// mapping keeps its ratchet instead of starting over.
        /// </summary>
        private async Task<string> ResolveSessionJidAsync(string jid)
        {
            if (string.IsNullOrEmpty(jid) ||
                _lidMapping == null ||
                (!JidUtils.IsPnUser(jid) && !JidUtils.IsHostedPnUser(jid)))
            {
                return jid;
            }

            string lid;
            try
            {
                lid = await _lidMapping.GetLidForPnAsync(jid).ConfigureAwait(false);
            }
            catch
            {
                return jid;
            }

            if (string.IsNullOrEmpty(lid) || lid == jid)
            {
                return jid;
            }

            await _signal.CloneSessionAliasAsync(jid, lid).ConfigureAwait(false);
            return lid;
        }
    }
}
