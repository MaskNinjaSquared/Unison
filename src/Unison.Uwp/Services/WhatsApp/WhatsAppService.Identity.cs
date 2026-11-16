using System;
using System.Diagnostics;
using Unison.Core.Helpers;
using Unison.Uwp.Helpers;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {
        private bool TryGetSelfIdentityAliasPair(out string id, out string lid)
        {
            id = null;
            lid = null;
            if (_authState?.Me == null)
            {
                return false;
            }

            return SelfIdentityAliasPair.TryCreate(
                _authState.Me.Id,
                _authState.Me.Lid,
                NormalizeJid,
                out id,
                out lid);
        }

        private void FileSelfIdentityAliases(string source)
        {
            string id;
            string lid;
            if (!TryGetSelfIdentityAliasPair(out id, out lid))
            {
                return;
            }

            JidAlias[id] = lid;
            JidAlias[lid] = id;
            RegisterSocketAlias(id, lid, source);
            Debug.WriteLine("[WhatsAppService] Self identity alias: " + id + " <-> " + lid + " (" + source + ")");
        }

        private void RegisterSocketAlias(string jidA, string jidB, string source)
        {
            _socket?.RegisterJidAlias(jidA, jidB, source);
        }

        internal void RegisterSocketAliases(string source)
        {
            var aliases = JidAlias.Snapshot();
            _socket?.RegisterJidAliases(aliases, source);
        }

        public string ResolveDisplayName(string jid, string context = null) =>
            _displayNames.Resolve(jid, context);

        public string GetCanonicalJid(string jid) => JidAlias.GetCanonicalJid(jid);

        bool Unison.WhatsAppClient.Abstractions.IResolvedNameApplyHost.HasSelfCredentials =>
            _authState?.Me != null && !string.IsNullOrWhiteSpace(_authState.Me.Id);

        string Unison.WhatsAppClient.Abstractions.IResolvedNameApplyHost.SelfId =>
            NormalizeJid(_authState?.Me?.Id);

        string Unison.WhatsAppClient.Abstractions.IResolvedNameApplyHost.SelfLid =>
            NormalizeJid(_authState?.Me?.Lid);

        string Unison.WhatsAppClient.Abstractions.IResolvedNameApplyHost.AuthSelfName =>
            _authState?.Me?.Name;

        string Unison.WhatsAppClient.Abstractions.IResolvedNameApplyHost.SidebarSelfName =>
            CurrentUserName;

        bool Unison.WhatsAppClient.Abstractions.IResolvedNameApplyHost.IsOwnPhoneEchoLabel(string label) =>
            IsOwnPhoneEchoLabel(label);

        void Unison.WhatsAppClient.Abstractions.IResolvedNameApplyHost.WriteSelfDisplayName(
            string sanitized,
            bool persistAuth)
        {
            if (_authState?.Me == null || string.IsNullOrWhiteSpace(sanitized))
            {
                return;
            }

            _authState.Me.Name = sanitized;
            CurrentUserName = sanitized;
            if (persistAuth)
            {
                _ = _persistAuthState.RunAsync(null, "self-push-name");
            }
        }

        /// <summary>
        /// Proactively merges duplicate chats when a new identity mapping is found.
        /// </summary>
        /// <remarks>
        /// With the pair filed, the dedupe pass folds the two rows field by field (pin, mute,
        /// unread, preview, avatar). Without it the fold cannot tell they are one person, so
        /// only the messages move and the LID row goes.
        /// </remarks>

        private string NormalizeJid(string jid)
        {
            return JidHelper.Normalize(jid);
        }

        private bool IsSelfJid(string jid) => JidAlias.IsSelfJid(jid);

        private bool IsSelfLinkedJid(string jid) => JidAlias.IsSelfLinked(jid);

        private bool IsSelfMarkerLabel(string label)
        {
            return SelfChatDisplayHelper.IsSelfMarkerLabel(label);
        }

        private string SanitizeContactLabel(string label, string contextJid)
        {
            ContactLabelResult result = _contactLabels.Sanitize(label, contextJid);

                if (!string.IsNullOrEmpty(contextJid))
                {
                LogContactLabelDecision(result, contextJid);
            }

            return result.IsUsable ? result.Label : null;
        }

        /// <summary>
        /// Says what was turned down and why. Kept out of the rule itself, which has to stay
        /// reachable from tests, but kept verbatim: these lines are how a wrong name in the
        /// list gets traced back to the source that offered it.
        /// </summary>
        private void LogContactLabelDecision(ContactLabelResult result, string contextJid)
        {
            string jid = NormalizeJid(contextJid);

            switch (result.Rejection)
            {
                case ContactLabelRejection.MaskedPhone:
                    Debug.WriteLine($"[WhatsAppService] Ignoring masked phone label for {jid}: '{result.Original}'");
                    break;

                case ContactLabelRejection.SelfMarker:
                    if (IsSelfJid(contextJid))
                    {
                        Log($"[WhatsAppService] Explicit self fallback label observed for SELF JID {jid}. Ignoring and keeping numeric identity.");
                    }
                    else
                    {
                        Log($"[WhatsAppService] Ignoring PushName self-fallback for NON-SELF JID {jid} (spoof prevention).");
                    }
                    break;

                case ContactLabelRejection.PhoneEcho:
                    Debug.WriteLine($"[WhatsAppService] Ignoring phone-echo label for {jid}: '{result.Original}'");
                    break;

                default:
                    if (result.MarkerStripped)
                    {
                        Log($"[WhatsAppService] Sanitized self marker suffix in name for {jid}: '{result.Original}' -> '{result.Label}'");
                    }
                    break;
            }
        }

        /// <summary>
        /// Reconciles our own identity with what the server just said. A companion learns its LID
        /// and its number separately, and a mismatch here is not cosmetic: messages get signed
        /// under an identity the recipients are not expecting.
        /// </summary>
        void ISelfIdentityHealHost.HealOwnIdentityAfterUsync(string normalizedUser, string normalizedLid)
        {
            string meLid = _authState?.Me?.Lid;
            if (string.IsNullOrEmpty(meLid) || _authState?.Me == null)
            {
                return;
            }

            string normalizedMeLid = NormalizeJid(meLid);
            SelfIdentityHealingAction action = SelfIdentityHealingDecision.Decide(
                normalizedUser,
                normalizedLid,
                _authState.Me.Id,
                normalizedMeLid);

            if (action == SelfIdentityHealingAction.HealMeId)
            {
                Log("[WhatsAppService] IDENTITY HEALING (USync): Me.Lid (" + meLid + ") belongs to PN " +
                    normalizedLid + ", but current Me.Id is " + _authState.Me.Id + ". Fixing...");
                _authState.Me.Id = normalizedLid;
                _ = _persistAuthState.RunAsync(null, "usync-identity-heal");
            }
            else if (action == SelfIdentityHealingAction.PurgeForeignMapping)
            {
                Log("[WhatsAppService] IDENTITY CORRUPTION DETECTED (USync): Me.Id (" + normalizedUser +
                    ") is mapped to foreign LID " + normalizedLid + ". PURGING...");
                _authState.Me.Id = meLid;
                JidAlias.Remove(normalizedUser);
                _ = _persistAuthState.RunAsync(null, "usync-identity-purge");
            }
        }

    }
}
