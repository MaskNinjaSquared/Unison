using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Unison.Uwp.Client;
using Unison.Core.Helpers;
using Unison.Core.Mappers;
using Unison.Core.Models;
using Unison.Baileys.Protocol;
using Unison.Uwp.Data;
using Unison.Baileys.Crypto;
using Unison.Uwp.Transport;
using Proto;
using Google.Protobuf;
using Windows.UI.Core;
using System.Threading;
using Windows.Storage;
using Windows.ApplicationModel.Core;
using Windows.Networking.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unison.Background;
using Unison.Baileys.Diagnostics;
using Unison.Baileys.Client;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Unison.Socket.UseCases.Contacts;
using Unison.Uwp.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {

        /// <summary>True when a JID already has a resolved display name in the local name cache.</summary>
        public bool HasResolvedContactName(string jid) => !string.IsNullOrWhiteSpace(GetBestWhatsAppName(jid, GetCanonicalJid(jid)));

        /// <summary>Re-applies <see cref="ResolveDisplayName"/> to each chat's Name where it changed.</summary>
        public Task ApplyResolvedDisplayNamesToChatsAsync() => ApplyResolvedNamesToChatsAsync();

        /// <summary>Raises <see cref="OnDisplayNamesUpdated"/>, and the store's equivalent.</summary>
        public void RaiseDisplayNamesUpdated()
        {
            OnDisplayNamesUpdated?.Invoke(this, EventArgs.Empty);
            _chatState.NotifyChangedExternally(null);
        }

        private void RegisterSocketAlias(string jidA, string jidB, string source)
        {
            _socket?.RegisterJidAlias(jidA, jidB, source);
        }

        private void RegisterSocketAliases(string source)
        {
            var aliases = JidAlias.Snapshot();
            _socket?.RegisterJidAliases(aliases, source);
        }

        private async Task PersistJidAliasesAsync(string reason)
        {
            try
            {
                List<string> chatJids = null;
                await RunOnUiThreadAsync(() =>
                    {
                        chatJids = Chats
                            .Where(c => c != null && !string.IsNullOrWhiteSpace(c.JID))
                            .Select(c => NormalizeJid(c.JID))
                            .Where(j => !string.IsNullOrWhiteSpace(j))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    });

                if (chatJids == null || chatJids.Count == 0)
                {
                    return;
                }

                var aliasSnapshot = JidAlias.Snapshot();
                await _messageStore.SaveJidAliasesAsync(aliasSnapshot, chatJids);
                Debug.WriteLine($"[WhatsAppService] Persisted {aliasSnapshot.Count} alias entries immediately ({reason})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to persist aliases immediately ({reason}): {ex.Message}");
            }
        }

        private async Task PersistChatIdentityStateAsync(string reason)
        {
            try
            {
                List<ChatItem> chatSnapshot = null;
                Dictionary<string, string> contactSnapshot = null;
                Dictionary<string, string> phoneSnapshot = null;
                Dictionary<string, string> aliasSnapshot = null;

                await RunOnUiThreadAsync(() =>
                    {
                        chatSnapshot = Chats
                            .Where(c => c != null && !string.IsNullOrWhiteSpace(c.JID))
                            .Select(c => new ChatItem
                            {
                                Id = c.Id,
                                JID = NormalizeJid(c.JID),
                                Name = c.Name,
                                LastMessage = c.LastMessage,
                                LastMessageKind = c.LastMessageKind,
                                Timestamp = c.Timestamp,
                                LastMessageTimestampUtc = c.LastMessageTimestampUtc,
                                UnreadCount = c.UnreadCount,
                                AvatarUrl = c.AvatarUrl,
                                AvatarHighUrl = c.AvatarHighUrl,
                                AvatarFetchedAtUtc = c.AvatarFetchedAtUtc,
                                AvatarFetchFailedAtUtc = c.AvatarFetchFailedAtUtc,
                                AvatarFetchFailureReason = c.AvatarFetchFailureReason,
                                Kind = c.Kind,
                                IsArchived = c.IsArchived,
                                IsChatPinned = c.IsChatPinned,
                                MutedUntil = c.MutedUntil,
                                GroupMemberCount = c.GroupMemberCount,
                                MyGroupRole = c.MyGroupRole,
                                IsAnnounceOnly = c.IsAnnounceOnly,
                                GroupMembers = CloneGroupMembers(c.GroupMembers)
                            })
                            .ToList();

                        contactSnapshot = new Dictionary<string, string>(ContactNames, StringComparer.OrdinalIgnoreCase);
                        phoneSnapshot = new Dictionary<string, string>(PhoneContactNamesByJid, StringComparer.OrdinalIgnoreCase);
                        aliasSnapshot = JidAlias.Snapshot();
                    });

                if (chatSnapshot == null || chatSnapshot.Count == 0)
                {
                    return;
                }

                var chatJids = chatSnapshot
                    .Select(c => NormalizeJid(c.JID))
                    .Where(j => !string.IsNullOrWhiteSpace(j))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                await PersistChatCatalogAsync(chatSnapshot);
                await _messageStore.SaveContactNamesAsync(contactSnapshot ?? new Dictionary<string, string>(), chatJids);
                await _messageStore.SavePhoneContactNamesAsync(phoneSnapshot ?? new Dictionary<string, string>(), chatJids);
                await _messageStore.SaveJidAliasesAsync(aliasSnapshot ?? new Dictionary<string, string>(), chatJids);
                Debug.WriteLine($"[WhatsAppService] Persisted chat identity state immediately ({reason})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to persist chat identity state immediately ({reason}): {ex.Message}");
            }
        }

        public string ResolveDisplayName(string jid, string context = null)
        {
            if (string.IsNullOrEmpty(jid)) return "";

            string normalized = NormalizeJid(jid);
            string canonical = GetCanonicalJid(normalized);
            bool isGroup = canonical.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);

            // Self naming uses explicit "(You)" marker with graceful fallback.
            if (IsSelfLinkedJid(canonical) || IsSelfLinkedJid(normalized))
            {
                return ResolveSelfDisplayName(canonical, normalized, context);
            }

            // Person in-memory cache (SQLite-backed store) Ã¢â‚¬â€ same idea as Redis in front of Dynamo.
            string personName = TryGetPersonDisplayName(canonical) ?? TryGetPersonDisplayName(normalized);
            if (!string.IsNullOrWhiteSpace(personName))
            {
                return personName;
            }

            // Cold cache: warm from disk without blocking the UI name path.
            if (_personStore != null)
            {
                _ = WarmPersonIntoCacheAsync(canonical);
                if (!string.Equals(canonical, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    _ = WarmPersonIntoCacheAsync(normalized);
                }
            }

            if (PhoneContactNamesByJid.TryGetValue(canonical, out var phoneName) && !string.IsNullOrWhiteSpace(phoneName))
            {
                string cleanPhoneName = SanitizeContactLabel(phoneName, canonical);
                if (!string.IsNullOrWhiteSpace(cleanPhoneName))
                {
                    return cleanPhoneName;
                }
            }
            if (PhoneContactNamesByJid.TryGetValue(normalized, out var phoneNameNorm) && !string.IsNullOrWhiteSpace(phoneNameNorm))
            {
                string cleanPhoneName = SanitizeContactLabel(phoneNameNorm, normalized);
                if (!string.IsNullOrWhiteSpace(cleanPhoneName))
                {
                    return cleanPhoneName;
                }
            }

            string waName = GetBestWhatsAppName(canonical, normalized);
            if (!string.IsNullOrWhiteSpace(waName))
            {
                string clean = waName.Trim();
                bool senderContext = string.Equals(context, "sender", StringComparison.OrdinalIgnoreCase);
                if (!senderContext && !isGroup && !clean.StartsWith("~", StringComparison.Ordinal))
                {
                    return "~" + clean;
                }
                return clean;
            }

            return canonical.Split('@')[0];
        }

        private async Task PersistPersonNameAsync(string jid, string displayName)
        {
            try
            {
                await _personStore.InitializeAsync().ConfigureAwait(false);
                await _personStore.UpsertIfChangedAsync(
                    jid,
                    displayName,
                    null,
                    JidHelper.TryPhoneFromJid(jid),
                    PersonSource.Observed).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Person name upsert failed: " + ex.Message);
            }
        }

        private string GetBestWhatsAppName(params string[] jids)
        {
            var candidates = ExpandNameLookupCandidates(jids);
            foreach (var candidate in candidates)
            {
                var name = GetWhatsAppNameFromCache(candidate);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }

            return null;
        }

        public string GetCanonicalJid(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return jid;
            string normalized = NormalizeJid(jid);

            if (JidAlias.TryGetValue(normalized, out var alias))
            {
                string normalizedAlias = NormalizeJid(alias);

                bool isBidirectionalSelfAlias =
                    IsSelfLinkedJid(normalizedAlias) &&
                    JidAlias.TryGetValue(normalizedAlias, out var reverseAlias) &&
                    string.Equals(NormalizeJid(reverseAlias), normalized, StringComparison.OrdinalIgnoreCase);

                // Guard: never canonicalize a non-self contact to our own JID.
                if (!IsSelfLinkedJid(normalized) && IsSelfLinkedJid(normalizedAlias) && !isBidirectionalSelfAlias)
                {
                    Debug.WriteLine($"[WhatsAppService] Ignoring alias that maps contact to self: {normalized} -> {normalizedAlias}");
                    return normalized;
                }

                // Some devices surface LID-like identifiers on @s.whatsapp.net (e.g. 931....1@s.whatsapp.net).
                // If both ends are @s.whatsapp.net, prefer the non-instance form as canonical.
                bool normalizedIsPn = normalized.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase);
                bool aliasIsPn = normalizedAlias.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase);
                if (normalizedIsPn && aliasIsPn)
                {
                    bool normalizedIsLidLike = IsLidLikeJid(normalized);
                    bool aliasIsLidLike = IsLidLikeJid(normalizedAlias);
                    if (normalizedIsLidLike && !aliasIsLidLike) return normalizedAlias;
                    if (!normalizedIsLidLike && aliasIsLidLike) return normalized;
                }
                
                // Favor @s.whatsapp.net (PN) as the canonical JID if both are available
                if (normalizedAlias.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) && !IsLidLikeJid(normalizedAlias)) return normalizedAlias;
                if (normalized.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) && !IsLidLikeJid(normalized)) return normalized;
                
                return normalizedAlias;
            }

            string lidLikeAlias = GetCanonicalForLidLikeSWhatsappJid(normalized);
            if (!string.IsNullOrWhiteSpace(lidLikeAlias))
            {
                return lidLikeAlias;
            }

            if (IsSelfLinkedJid(normalized))
            {
                string selfJid = GetCanonicalSelfPnJid();
                if (!string.IsNullOrWhiteSpace(selfJid))
                {
                    return selfJid;
                }
            }

            return normalized;
        }

        private string GetCanonicalSelfPnJid()
        {
            string meId = NormalizeJid(_authState?.Me?.Id);
            if (!string.IsNullOrWhiteSpace(meId) &&
                meId.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) &&
                !IsLidLikeJid(meId))
            {
                return meId;
            }

            string meLid = NormalizeJid(_authState?.Me?.Lid);
            if (!string.IsNullOrWhiteSpace(meLid) &&
                JidAlias.TryGetValue(meLid, out var alias))
            {
                string normalizedAlias = NormalizeJid(alias);
                if (!string.IsNullOrWhiteSpace(normalizedAlias) &&
                    normalizedAlias.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) &&
                    !IsLidLikeJid(normalizedAlias))
                {
                    return normalizedAlias;
                }
            }

            if (!string.IsNullOrWhiteSpace(meId))
            {
                return meId;
            }

            return string.IsNullOrWhiteSpace(meLid) ? null : meLid;
        }

        private string GetCanonicalForLidLikeSWhatsappJid(string normalized)
        {
            if (string.IsNullOrWhiteSpace(normalized) ||
                !normalized.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string user = normalized.Split('@')[0];
            int dotIndex = user.IndexOf('.');
            if (dotIndex <= 0)
            {
                return null;
            }

            string baseLid = $"{user.Substring(0, dotIndex)}@lid";
            if (JidAlias.TryGetValue(baseLid, out var alias))
            {
                string canonical = NormalizeJid(alias);
                if (!string.IsNullOrWhiteSpace(canonical))
                {
                    bool isBidirectionalSelfAlias =
                        IsSelfLinkedJid(canonical) &&
                        JidAlias.TryGetValue(canonical, out var reverseAlias) &&
                        string.Equals(NormalizeJid(reverseAlias), baseLid, StringComparison.OrdinalIgnoreCase);

                    if (!IsSelfLinkedJid(baseLid) && IsSelfLinkedJid(canonical) && !isBidirectionalSelfAlias)
                    {
                        Debug.WriteLine($"[WhatsAppService] Ignoring dotted alias that maps contact to self: {normalized} -> {canonical}");
                        return null;
                    }

                    return GetCanonicalJid(canonical);
                }
            }

            if (IsSelfLinkedJid(baseLid))
            {
                return GetCanonicalSelfPnJid();
            }

            return null;
        }

        private bool TryGetCanonicalNonSelfDirectJid(string jid, out string canonical)
        {
            canonical = null;
            string normalized = NormalizeJid(jid);
            if (string.IsNullOrWhiteSpace(normalized) || normalized.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string resolved = GetCanonicalJid(normalized);
            if (string.IsNullOrWhiteSpace(resolved) || IsSelfLinkedJid(resolved) || IsSelfLinkedJid(normalized))
            {
                return false;
            }

            canonical = resolved;
            return true;
        }

        private async Task MergeTransientDirectChatIntoCanonicalAsync(string transientJid, string canonicalJid, string reason)
        {
            string normalizedTransient = NormalizeJid(transientJid);
            string normalizedCanonical = NormalizeJid(canonicalJid);
            if (string.IsNullOrWhiteSpace(normalizedTransient) ||
                string.IsNullOrWhiteSpace(normalizedCanonical) ||
                string.Equals(normalizedTransient, normalizedCanonical, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            bool merged = false;
            List<ChatMessage> canonicalSnapshot = null;

            await RunOnUiThreadAsync(() =>
            {
                if (MessagesByChat.TryGetValue(normalizedTransient, out var transientMessages) && transientMessages != null)
                {
                    if (!MessagesByChat.TryGetValue(normalizedCanonical, out var canonicalMessages) || canonicalMessages == null)
                    {
                        canonicalMessages = new List<ChatMessage>();
                        MessagesByChat[normalizedCanonical] = canonicalMessages;
                    }

                    var canonicalIds = GetOrBuildMessageIdIndex(normalizedCanonical);
                    foreach (var msg in transientMessages.ToList())
                    {
                        if (msg == null) continue;

                        if (string.IsNullOrEmpty(msg.Id))
                        {
                            if (!canonicalMessages.Contains(msg))
                            {
                                canonicalMessages.Add(msg);
                            }
                        }
                        else if (canonicalIds.Add(msg.Id))
                        {
                            canonicalMessages.Add(msg);
                        }
                    }

                    MessagesByChat.Remove(normalizedTransient);
                    _messageIdIndexByChat.Remove(normalizedTransient);
                    _pendingMissingMessagesByChat.Remove(normalizedTransient);
                    merged = true;
                    canonicalSnapshot = canonicalMessages.ToList();
                }

                var transientChat = Chats.FirstOrDefault(c => NormalizeJid(c.JID) == normalizedTransient);
                var canonicalChat = Chats.FirstOrDefault(c => NormalizeJid(c.JID) == normalizedCanonical);
                if (transientChat != null)
                {
                    if (canonicalChat == null)
                    {
                        transientChat.JID = normalizedCanonical;
                        InvalidateChatRowIndex();
                        canonicalChat = transientChat;
                    }
                    else
                    {
                        DateTime canonicalPreviewUtc = canonicalChat.LastMessageTimestampUtc.HasValue
                            ? ToComparableUtc(canonicalChat.LastMessageTimestampUtc.Value)
                            : DateTime.MinValue;
                        DateTime transientPreviewUtc = transientChat.LastMessageTimestampUtc.HasValue
                            ? ToComparableUtc(transientChat.LastMessageTimestampUtc.Value)
                            : DateTime.MinValue;
                        if ((transientPreviewUtc > canonicalPreviewUtc || string.IsNullOrWhiteSpace(canonicalChat.LastMessage)) &&
                            !string.IsNullOrWhiteSpace(transientChat.LastMessage))
                        {
                            canonicalChat.LastMessage = transientChat.LastMessage;
                            canonicalChat.LastMessageKind = transientChat.LastMessageKind;
                            canonicalChat.Timestamp = transientChat.Timestamp;
                            canonicalChat.LastMessageTimestampUtc = transientChat.LastMessageTimestampUtc;
                        }

                        if (canonicalChat.UnreadCount < transientChat.UnreadCount)
                        {
                            canonicalChat.UnreadCount = transientChat.UnreadCount;
                        }

                        if (string.IsNullOrWhiteSpace(canonicalChat.AvatarUrl) && !string.IsNullOrWhiteSpace(transientChat.AvatarUrl))
                        {
                            canonicalChat.AvatarUrl = transientChat.AvatarUrl;
                            canonicalChat.AvatarFetchedAtUtc = transientChat.AvatarFetchedAtUtc;
                            canonicalChat.AvatarFetchFailedAtUtc = transientChat.AvatarFetchFailedAtUtc;
                            canonicalChat.AvatarFetchFailureReason = transientChat.AvatarFetchFailureReason;
                        }

                        string canonicalBare = normalizedCanonical.Split('@')[0];
                        string transientBare = normalizedTransient.Split('@')[0];
                        if ((string.IsNullOrWhiteSpace(canonicalChat.Name) ||
                             canonicalChat.Name == canonicalBare ||
                             IsSelfMarkerLabel(canonicalChat.Name)) &&
                            !string.IsNullOrWhiteSpace(transientChat.Name) &&
                            transientChat.Name != transientBare)
                        {
                            canonicalChat.Name = transientChat.Name;
                        }

                        Chats.Remove(transientChat);
                    }

                    merged = true;
                }

                if (ContactNames.TryGetValue(normalizedTransient, out var transientName))
                {
                    if (!ContactNames.ContainsKey(normalizedCanonical))
                    {
                        ContactNames[normalizedCanonical] = transientName;
                    }

                    ContactNames.Remove(normalizedTransient);
                    merged = true;
                }

                if (PhoneContactNamesByJid.TryGetValue(normalizedTransient, out var transientPhoneName))
                {
                    if (!PhoneContactNamesByJid.ContainsKey(normalizedCanonical))
                    {
                        PhoneContactNamesByJid[normalizedCanonical] = transientPhoneName;
                    }

                    PhoneContactNamesByJid.Remove(normalizedTransient);
                    merged = true;
                }
            });

            if (!merged)
            {
                return;
            }

            Debug.WriteLine($"[WhatsAppService] Collapsed transient direct chat {normalizedTransient} into {normalizedCanonical} ({reason})");
            if (canonicalSnapshot != null && canonicalSnapshot.Count > 0)
            {
                await PersistLiveMessagesAsync(normalizedCanonical, canonicalSnapshot);
            }

            await _messageStore.DeleteChatMessagesAsync(normalizedTransient);
            await PersistChatIdentityStateAsync(reason);
        }

        internal string GetCanonicalChatJid(string jid) => GetCanonicalJid(jid);

        internal void RegisterAliasFromAppState(string lidJid, string pnJid, string source) => RegisterAliasMapping(lidJid, pnJid, source);

        /// <summary>A name that has been resolved to an address, ready to be applied.</summary>
        private sealed class ResolvedNameEntry
        {
            public string Canonical;
            public string Normalized;
            public string Name;

            /// <summary>
            /// A group's subject, which is the group's name outright, as opposed to a contact's
            /// name, which is one input to a display name that is composed elsewhere.
            /// </summary>
            public bool IsSubject;
        }

        /// <summary>
        /// Records a LID/phone pair and schedules the work that follows from it.
        /// </summary>
        /// <remarks>
        /// The follow-up is scheduled rather than run because it is the same work no matter how
        /// many pairs arrive: rewriting the alias file, collapsing duplicate rows, asking for the
        /// avatars a resolved pair unblocks. One message registering one pair and a history chunk
        /// registering a thousand both end in a single pass now. It used to be a pass each, which
        /// on a first sync meant thousands of dispatches to the UI thread and thousands of writes
        /// of the whole alias map - during the one minute the app has the least to spare.
        /// </remarks>
        private void RegisterAliasMapping(string lidJid, string pnJid, string source)
        {
            if (TryRecordAliasMapping(lidJid, pnJid, source))
            {
                ScheduleAliasFollowUp(source);
            }
        }

        /// <summary>
        /// Same for a whole set at once, for the sources that deal in tables rather than in single
        /// pairs - a history chunk, a group listing.
        /// </summary>
        internal void RegisterAliasMappings(IEnumerable<KeyValuePair<string, string>> lidToPn, string source)
        {
            if (lidToPn == null)
            {
                return;
            }

            int changed = 0;
            foreach (var pair in lidToPn)
            {
                if (TryRecordAliasMapping(pair.Key, pair.Value, source))
                {
                    changed++;
                }
            }

            if (changed > 0)
            {
                Debug.WriteLine($"[WhatsAppService] Recorded {changed} new alias pair(s) from {source}");
                ScheduleAliasFollowUp(source);
            }
        }

        /// <summary>
        /// The bookkeeping half: validates the pair, files it both ways, and reports whether it
        /// told us anything we did not already know. No UI, no disk, no scans.
        /// </summary>
        private bool TryRecordAliasMapping(string lidJid, string pnJid, string source)
        {
            string lid = NormalizeJid(lidJid);
            string pn = NormalizeJid(pnJid);
            if (string.IsNullOrEmpty(lid) || string.IsNullOrEmpty(pn)) return false;
            bool lidAccepted = lid.EndsWith("@lid", StringComparison.OrdinalIgnoreCase) || IsLidLikeJid(lid);
            bool pnAccepted = pn.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) && !IsLidLikeJid(pn);
            if (!lidAccepted || !pnAccepted) return false;

            // Guard against identity poisoning: never map a foreign LID to our own phone JID.
            // Dotted @s.whatsapp.net LID aliases for our own account are allowed and collapse to self chat.
            string guardLidKey = lid;
            if (IsLidLikeJid(lid) && lid.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase))
            {
                string lidUser = lid.Split('@')[0];
                int dotIndex = lidUser.IndexOf('.');
                if (dotIndex > 0)
                {
                    guardLidKey = $"{lidUser.Substring(0, dotIndex)}@lid";
                }
            }

            bool isKnownSelfAlias =
                IsSelfLinkedJid(pn) &&
                JidAlias.TryGetValue(pn, out var reverseAlias) &&
                string.Equals(NormalizeJid(reverseAlias), guardLidKey, StringComparison.OrdinalIgnoreCase);

            if (!IsSelfLinkedJid(lid) && IsSelfLinkedJid(pn) && !isKnownSelfAlias)
            {
                Debug.WriteLine($"[WhatsAppService] Skipping suspicious alias from {source}: {lid} -> {pn}");
                return false;
            }

            bool changed = !JidAlias.TryGetValue(lid, out var existingPn) || NormalizeJid(existingPn) != pn;
            JidAlias[lid] = pn;
            JidAlias[pn] = lid;
            RegisterSocketAlias(lid, pn, source);

            if (!changed)
            {
                // Live traffic re-states the same pair on every message. Recognising that costs a
                // dictionary lookup and saves everything below.
                return false;
            }

            // Uma consulta anterior pode ter usado somente o LID ou somente o PN e
            // gravado um falso "no-picture". Ao descobrir o par correto, permita
            // uma nova tentativa imediatamente para as linhas sem avatar.
            if (_contactService != null)
            {
                _contactService.ClearAvatarAttempted(lid);
                _contactService.ClearAvatarAttempted(pn);
                _contactService.ClearAvatarAttempted(GetCanonicalJid(pn));
            }

            lock (_aliasFollowUpGate)
            {
                _pendingAliasAvatarJids.Add(pn);
            }

            return true;
        }

        /// <summary>
        /// Coalesces the follow-up so a burst of pairs produces one pass instead of one each.
        /// </summary>
        private void ScheduleAliasFollowUp(string source)
        {
            CancellationToken token;
            lock (_aliasFollowUpGate)
            {
                _pendingAliasFollowUpSource = source;

                if (_aliasFollowUpCts != null)
                {
                    _aliasFollowUpCts.Cancel();
                    _aliasFollowUpCts.Dispose();
                }

                _aliasFollowUpCts = new CancellationTokenSource();
                token = _aliasFollowUpCts.Token;
            }

            Task.Delay(AliasFollowUpDebounce, token).ContinueWith(
                t =>
                {
                    if (t.IsCanceled)
                    {
                        return;
                    }

                    _ = RunAliasFollowUpAsync();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>
        /// Everything that a batch of new aliases implies, done once: the alias file is rewritten,
        /// rows that turned out to be the same conversation are merged, and the rows whose avatar
        /// lookup failed under a half-known identity get another chance.
        /// </summary>
        private async Task RunAliasFollowUpAsync()
        {
            if (Interlocked.Exchange(ref _aliasFollowUpRunning, 1) == 1)
            {
                // A pass is already writing the file and walking the list. Whatever arrived in the
                // meantime is still pending and will be picked up by the next timer.
                ScheduleAliasFollowUp(_pendingAliasFollowUpSource);
                return;
            }

            string source;
            List<string> avatarTargets;
            lock (_aliasFollowUpGate)
            {
                source = _pendingAliasFollowUpSource ?? "alias";
                avatarTargets = _pendingAliasAvatarJids.ToList();
                _pendingAliasAvatarJids.Clear();
            }

            try
            {
                await PersistJidAliasesAsync("alias:" + source);

                if (avatarTargets.Count > 0)
                {
                    await RunOnUiThreadAsync(() =>
                    {
                        foreach (var pn in avatarTargets)
                        {
                            foreach (var chat in GetChatRowsForCanonicalJid(pn)
                                         .Where(c => string.IsNullOrWhiteSpace(c.AvatarUrl)))
                            {
                                RequestAvatarRefresh(chat, force: true);
                            }
                        }
                    });
                }

                // Deduplication is global and idempotent: it groups every row by canonical JID,
                // which is what the pairs just changed. Running it once at the end covers every
                // pair in the burst, including the per-pair merge this used to do separately.
                await DeduplicateChatsAsync("alias:" + source);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Alias follow-up failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _aliasFollowUpRunning, 0);
            }
        }

        /// <summary>Delegates to <see cref="IContactService"/> (owns cooldown/dedup policy); this class only supplies the client primitives.</summary>
        public Task RefreshContactNamesAsync(bool includeGroups = false, bool force = false)
        {
            return _contactService?.RefreshContactNamesAsync(includeGroups, force) ?? Task.CompletedTask;
        }
    
        public async Task ResolveContactsAsync(string[] jids, bool allowBatchFallback = true)
        {
            if (jids == null || jids.Length == 0) return;
            if (_socket == null || !_socket.IsHandshakeComplete)
            {
                Debug.WriteLine("[WhatsAppService] ResolveContactsAsync skipped (handshake not complete)");
                return;
            }

            var bridge = _socket as SocketBridge;
            var session = bridge != null ? bridge.Session : null;
            if (session != null && session.Connection.IsConnected)
            {
                await ResolveContactsViaSocketAsync(jids).ConfigureAwait(false);
                return;
            }

            string[] fallbackJids = null;
            bool lockTaken = false;
            try
            {
                await _usyncLock.WaitAsync().ConfigureAwait(false);
                lockTaken = true;

                // Socket may drop while waiting for the usync lock during sync.
                if (_socket == null || !_socket.IsHandshakeComplete)
                {
                    Debug.WriteLine("[WhatsAppService] ResolveContactsAsync skipped after lock (socket not ready)");
                    return;
                }

                Debug.WriteLine($"[WhatsAppService] ResolveContactsAsync: querying {jids.Length} contacts...");
                // Keep the background direct-contact refresh on the narrow phone-based query.
                // The broader JID-based metadata probe can expose richer metadata, but in the
                // current companion session it times out even one-at-a-time and is not viable
                // as an automatic background refresh.
                var queryProtocols = new List<BinaryNode>
                {
                    new BinaryNode("contact", null)
                };

                // Build user nodes - for the background refresh, use phone-based lookup to keep
                // the query fast and reliable. Higher-fidelity name sources come from history
                // pushnames, notify attributes, and explicit profile-style probes.
                var userNodes = new List<BinaryNode>();
                foreach (var jid in jids)
                {
                    if (string.IsNullOrWhiteSpace(jid))
                    {
                        continue;
                    }

                    if (NormalizeJid(jid) == NormalizeJid(_authState?.Me?.Id))
                    {
                        Debug.WriteLine($"[WhatsAppService] ResolveContactsAsync: skipping self JID {jid}");
                        continue;
                    }

                    if (jid.EndsWith("@newsletter", StringComparison.OrdinalIgnoreCase) ||
                        jid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase) ||
                        jid.EndsWith("@broadcast", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.WriteLine($"[WhatsAppService] ResolveContactsAsync: skipping non-direct JID {jid}");
                        continue;
                    }

                    string phone = null;
                    if (jid.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) ||
                        jid.EndsWith("@lid", StringComparison.OrdinalIgnoreCase))
                    {
                        string canonical = GetCanonicalJid(jid);
                        if (string.IsNullOrWhiteSpace(canonical))
                        {
                            canonical = jid;
                        }

                        int atIndex = canonical.IndexOf('@');
                        phone = atIndex >= 0 ? canonical.Substring(0, atIndex) : canonical;
                        int deviceIndex = phone.IndexOf(':');
                        if (deviceIndex >= 0)
                        {
                            phone = phone.Substring(0, deviceIndex);
                        }
                    }
                    else
                    {
                        phone = jid;
                    }

                    phone = phone?.Replace("+", "").Replace(" ", "").Replace("-", "");
                    if (string.IsNullOrWhiteSpace(phone))
                    {
                        Debug.WriteLine($"[WhatsAppService] ResolveContactsAsync: unable to derive phone lookup key for {jid}");
                        continue;
                    }

                    if (!phone.StartsWith("+", StringComparison.Ordinal))
                    {
                        phone = "+" + phone;
                    }

                    var children = new List<BinaryNode>
                    {
                        new BinaryNode("contact", null, phone)
                    };
                    userNodes.Add(new BinaryNode("user", null, children));
                }

                if (userNodes.Count == 0)
                {
                    Debug.WriteLine("[WhatsAppService] ResolveContactsAsync: no supported direct-contact JIDs remained after filtering.");
                    return;
                }

                var socket = _socket;
                if (socket == null || !socket.IsHandshakeComplete)
                {
                    Debug.WriteLine("[WhatsAppService] ResolveContactsAsync aborted (socket lost before usync)");
                    return;
                }

                int timeoutMs = userNodes.Count > 1 ? 15000 : 8000;
                var response = await socket.QueryUsyncAsync(userNodes, "interactive", "query", queryProtocols, timeoutMs);
                if (response == null) return;

                Debug.WriteLine($"[WhatsAppService] usync response: {response.Tag}");
                var usyncNode = response.GetChild("usync");
                var listNode = usyncNode?.GetChild("list");
                if (listNode?.Children == null)
                {
                    Debug.WriteLine($"[WhatsAppService] usync response missing list/children node: {response}");
                    if (usyncNode != null)
                    {
                        var errorNode = usyncNode.GetChild("error");
                        if (errorNode != null) Debug.WriteLine($"[WhatsAppService] usync server error: {errorNode}");
                    }

                    if (allowBatchFallback && userNodes.Count > 1)
                    {
                        Debug.WriteLine($"[WhatsAppService] ResolveContactsAsync batch rejected; retrying individually for {userNodes.Count} JIDs.");
                        fallbackJids = jids
                            .Where(j => !string.IsNullOrWhiteSpace(j))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                    }
                    return;
                }

                bool cacheUpdated = false;
                foreach (var userNode in listNode.Children)
                {
                    if (userNode == null) continue;

                    string userJid = userNode.Attrs != null && userNode.Attrs.TryGetValue("jid", out var j) ? j : null;
                    if (string.IsNullOrEmpty(userJid)) continue;

                    string normalizedUser = NormalizeJid(userJid);

                    // Debug log all children tags for deeper inspection
                    if (userNode.Children != null && userNode.Children.Count > 0)
                    {
                        var childTags = string.Join(", ", userNode.Children.Where(c => c != null).Select(c => c.Tag));
                        Debug.WriteLine($"[WhatsAppService] user node {userJid} children: [{childTags}]");
                    }
                    else
                    {
                        Debug.WriteLine($"[WhatsAppService] user node {userJid} children: []");
                    }

                    // 1. Process LID/PN mapping
                    var lidNode = userNode.GetChild("lid");
                    if (lidNode != null)
                    {
                        string targetJid = lidNode.Attrs != null && lidNode.Attrs.TryGetValue("val", out var v) ? v : null;
                        if (!string.IsNullOrEmpty(targetJid))
                        {
                            if (!targetJid.Contains("@"))
                            {
                                targetJid += userJid.EndsWith("@lid") ? "@s.whatsapp.net" : "@lid";
                            }

                            string normalizedTarget = NormalizeJid(targetJid);
                            JidAlias[normalizedUser] = normalizedTarget;
                            JidAlias[normalizedTarget] = normalizedUser;
                            RegisterSocketAlias(normalizedUser, normalizedTarget, "contact-usync");
                            cacheUpdated = true;

                            // Identity Healing: Check if this LID belongs to US
                            string meLid = _authState?.Me?.Lid;
                            if (!string.IsNullOrEmpty(meLid) && normalizedUser == NormalizeJid(meLid))
                            {
                                string meId = _authState.Me.Id;
                                if (normalizedTarget != meId)
                                {
                                    Log($"[WhatsAppService] IDENTITY HEALING (USync): Me.Lid ({meLid}) belongs to PN {normalizedTarget}, but current Me.Id is {meId}. Fixing...");
                                    _authState.Me.Id = normalizedTarget;
                                    _ = PersistAuthStateAsync(null, "usync-identity-heal");
                                }
                            }
                            else if (normalizedUser == _authState?.Me?.Id && !string.IsNullOrEmpty(meLid) && normalizedTarget != NormalizeJid(meLid))
                            {
                                // If the PN in Me.Id points to a LID that isn't ours, it's corrupt
                                Log($"[WhatsAppService] IDENTITY CORRUPTION DETECTED (USync): Me.Id ({normalizedUser}) is mapped to foreign LID {normalizedTarget}. PURGING...");
                                _authState.Me.Id = meLid;
                                JidAlias.Remove(normalizedUser);
                                _ = PersistAuthStateAsync(null, "usync-identity-purge");
                            }
                        }
                    }

                    // 2. Process Contact Name
                    var contactNode = userNode.GetChild("contact");
                    if (contactNode != null)
                    {
                        string pushName = contactNode.Attrs != null && contactNode.Attrs.TryGetValue("notify", out var n) ? n : null;
                        if (string.IsNullOrEmpty(pushName))
                        {
                            pushName = contactNode.Attrs.TryGetValue("name", out var nm) ? nm : null;
                        }
                        if (string.IsNullOrEmpty(pushName))
                        {
                            pushName = contactNode.GetContentString();
                            if (!string.IsNullOrEmpty(pushName)) Debug.WriteLine($"[WhatsAppService] Found name in text content for {userJid}: {pushName}");
                        }

                        // Process picture (Avatar) ID if the server included it inline.
                        var pictureNode = userNode.GetChild("picture");
                        if (pictureNode != null)
                        {
                            var pictureId = pictureNode.Attrs.TryGetValue("id", out var pid) ? pid : null;
                            if (!string.IsNullOrEmpty(pictureId))
                            {
                                Debug.WriteLine($"[WhatsAppService] usync avatar ID found for {userJid}: {pictureId}");
                                
                                // Fire and forget avatar URL fetch
                                _ = Task.Run(async () =>
                                {
                                    var url = await GetProfilePictureAsync(userJid);
                                    if (!string.IsNullOrEmpty(url))
                                    {
                                        await RunOnUiThreadAsync(() =>
                                        {
                                            var chat = Chats.FirstOrDefault(c => NormalizeJid(c.JID) == normalizedUser);
                                            if (chat != null)
                                            {
                                                chat.AvatarUrl = url;
                                                chat.AvatarFetchedAtUtc = DateTime.UtcNow;
                                                chat.AvatarFetchFailedAtUtc = null;
                                                chat.AvatarFetchFailureReason = null;
                                                Debug.WriteLine($"[WhatsAppService] Updated AvatarUrl for {userJid}");
                                            }
                                        });
                                    }
                                });
                            }
                        }

                        // Process LID mapping for canonicalization
                        var mappedLidNode = userNode.GetChild("lid");
                        if (mappedLidNode != null)
                        {
                            var targetLid = mappedLidNode.Attrs.TryGetValue("jid", out var lj) ? lj : null;
                            if (!string.IsNullOrEmpty(targetLid))
                            {
                                string normalizedLid = NormalizeJid(targetLid);
                                if (!JidAlias.ContainsKey(normalizedLid))
                                {
                                    JidAlias[normalizedLid] = normalizedUser;
                                    JidAlias[normalizedUser] = normalizedLid;
                                    RegisterSocketAlias(normalizedLid, normalizedUser, "contact-usync-mapped-lid");
                                    Debug.WriteLine($"[WhatsAppService] usync mapping found: {normalizedLid} -> {normalizedUser}");
                                    
                                    // Proactively merge chats if both exist
                                    _ = CheckAndMergeDuplicateChatsAsync(normalizedLid, normalizedUser);
                                }
                            }
                        }

                        pushName = SanitizeContactLabel(pushName, normalizedUser);
                        if (!string.IsNullOrEmpty(pushName))
                        {
                            ContactNames[normalizedUser] = pushName;
                            cacheUpdated = true;
                            Debug.WriteLine($"[WhatsAppService] usync name resolved: {userJid} -> {pushName}");

                            // Do not rely on inline usync picture nodes for direct contacts.
                            // If the chat still has no avatar, fetch it through the dedicated
                            // profile-picture IQ path once we know the JID is valid.
                            var chatNeedingAvatar = Chats.FirstOrDefault(c => NormalizeJid(c.JID) == normalizedUser && string.IsNullOrEmpty(c.AvatarUrl));
                            if (chatNeedingAvatar != null && !normalizedUser.EndsWith("@g.us"))
                            {
                                _ = Task.Run(async () =>
                                {
                                    var url = await GetProfilePictureAsync(userJid);
                                    if (!string.IsNullOrEmpty(url))
                                    {
                                        await RunOnUiThreadAsync(() =>
                                        {
                                            var chat = Chats.FirstOrDefault(c => NormalizeJid(c.JID) == normalizedUser);
                                            if (chat != null && string.IsNullOrEmpty(chat.AvatarUrl))
                                            {
                                                chat.AvatarUrl = url;
                                                chat.AvatarFetchedAtUtc = DateTime.UtcNow;
                                                chat.AvatarFetchFailedAtUtc = null;
                                                chat.AvatarFetchFailureReason = null;
                                                Debug.WriteLine($"[WhatsAppService] Updated AvatarUrl for {userJid} via profile-picture IQ");
                                            }
                                        });
                                    }
                                });
                            }
                        }
                        else
                        {
                            // Log attributes if name not found
                            var attrList = contactNode.Attrs != null
                                ? string.Join(", ", contactNode.Attrs.Select(kv => $"{kv.Key}={kv.Value}"))
                                : string.Empty;
                            int contentLen = (contactNode.Content is byte[] b) ? b.Length : (contactNode.Content is string s ? s.Length : 0);
                            Debug.WriteLine($"[WhatsAppService] usync contact node for {userJid} exists but has no name. Attrs: [{attrList}], ContentLen: {contentLen}");
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"[WhatsAppService] usync response for {userJid} is MISSING the 'contact' node.");
                    }
                }

                if (cacheUpdated)
                {
                    await ApplyResolvedNamesToChatsAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] ResolveContactsAsync failed: {ex}");
                try
                {
                    RuntimeDiagnosticsService.Instance.RecordException(
                        "contacts",
                        "resolve-contacts-failed",
                        ex,
                        "count=" + jids.Length + "; batchFallback=" + allowBatchFallback);
                }
                catch
                {
                }

                if (allowBatchFallback && jids.Length > 1)
                {
                    fallbackJids = jids
                        .Where(j => !string.IsNullOrWhiteSpace(j))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
            }
            finally
            {
                if (lockTaken)
                {
                    try
                    {
                        _usyncLock.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    catch (SemaphoreFullException)
                    {
                    }
                }
            }

            if (fallbackJids == null || fallbackJids.Length == 0)
            {
                return;
            }

            foreach (var originalJid in fallbackJids)
            {
                try
                {
                    await ResolveContactsAsync(new[] { originalJid }, allowBatchFallback: false);
                }
                catch (Exception exOne)
                {
                    Debug.WriteLine($"[WhatsAppService] ResolveContactsAsync single fallback failed for {originalJid}: {exOne.Message}");
                }
            }
        }

        public async Task<string> SearchContactAsync(string phoneNumber)
        {
            if (string.IsNullOrEmpty(phoneNumber)) return null;
            
            // Normalize phone number (remove +, spaces, etc)
            string cleaned = phoneNumber.Replace("+", "").Replace(" ", "").Replace("-", "");
            if (string.IsNullOrEmpty(cleaned)) return null;

            Debug.WriteLine($"[WhatsAppService] SearchContactAsync: Searching for {cleaned}...");
            
            // Trigger resolution (ResolveContactsAsync handles phone nodes if no @ is present)
            await ResolveContactsAsync(new string[] { cleaned });

            // Check if we found a mapping or a name for this
            // USync adds the resolved JID as an alias or key in ContactNames
            // Let's find any JID that contains this phone number
            string foundJid = null;
            
            // Check JidAlias first (USync often returns LID <-> JID)
            foreach (var alias in JidAlias)
            {
                if (alias.Key.StartsWith(cleaned)) { foundJid = alias.Key; break; }
                if (alias.Value.StartsWith(cleaned)) { foundJid = alias.Value; break; }
            }

            if (foundJid == null)
            {
                foreach (var name in ContactNames)
                {
                    if (name.Key.StartsWith(cleaned)) { foundJid = name.Key; break; }
                }
            }

            if (foundJid != null)
            {
                Debug.WriteLine($"[WhatsAppService] SearchContactAsync: Found {foundJid} for {cleaned}");
                return foundJid;
            }

            Debug.WriteLine($"[WhatsAppService] SearchContactAsync: No contact found for {cleaned}");
            return null;
        }
    }
}
