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
                await _ui.RunAsync(() =>
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
                ChatCatalogSnapshot catalog = null;
                Dictionary<string, string> contactSnapshot = null;
                Dictionary<string, string> phoneSnapshot = null;
                Dictionary<string, string> aliasSnapshot = null;

                await _ui.RunAsync(() =>
                    {
                        catalog = ChatCatalogSnapshot.Take(Chats);
                        contactSnapshot = ContactNames.Snapshot();
                        phoneSnapshot = PhoneContactNamesByJid.Snapshot();
                        aliasSnapshot = JidAlias.Snapshot();
                    });

                if (catalog == null || catalog.ChatJids.Count == 0)
                {
                    return;
                }

                await PersistChatCatalogAsync(catalog);
                await _messageStore.SaveContactNamesAsync(contactSnapshot ?? new Dictionary<string, string>(), catalog.ChatJids);
                await _messageStore.SavePhoneContactNamesAsync(phoneSnapshot ?? new Dictionary<string, string>(), catalog.ChatJids);
                await _messageStore.SaveJidAliasesAsync(aliasSnapshot ?? new Dictionary<string, string>(), catalog.ChatJids);
                Debug.WriteLine($"[WhatsAppService] Persisted chat identity state immediately ({reason})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to persist chat identity state immediately ({reason}): {ex.Message}");
            }
        }

        public string ResolveDisplayName(string jid, string context = null) =>
            _displayNames.Resolve(jid, context);

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

        private string GetBestWhatsAppName(params string[] jids) => _displayNames.FindBestPushName(jids);

        public string GetCanonicalJid(string jid) => JidAlias.GetCanonicalJid(jid);

        private string GetCanonicalSelfPnJid() => JidAlias.GetCanonicalSelfPnJid();

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

            await _ui.RunAsync(() =>
            {
                if (MessagesByChat.MergeInto(normalizedTransient, normalizedCanonical))
                {
                    ForgetMissingMessagesForChat(normalizedTransient);
                    merged = true;
                    canonicalSnapshot = MessagesByChat[normalizedCanonical].ToList();
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
                        TransientChatMerge.Apply(
                            canonicalChat,
                            transientChat,
                            normalizedCanonical,
                            normalizedTransient,
                            _selfMarkers);

                        Chats.Remove(transientChat);
                    }

                    merged = true;
                }

                if (ContactNames.TryGetValue(normalizedTransient, out var transientName))
                {
                    ContactNames.TryAdd(normalizedCanonical, transientName);
                    ContactNames.Remove(normalizedTransient);
                    merged = true;
                }

                if (PhoneContactNamesByJid.TryGetValue(normalizedTransient, out var transientPhoneName))
                {
                    PhoneContactNamesByJid.TryAdd(normalizedCanonical, transientPhoneName);
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
        /// Whether filing this pair would put a contact under our own identity.
        /// </summary>
        /// <remarks>
        /// The live path refuses such a pair in <see cref="TryRecordAliasMapping"/>, but persisted
        /// aliases are written straight into the table on startup, so a bad pair that ever reached
        /// disk came back on every launch and merged that contact's conversation into the self chat.
        ///
        /// This has to be asked *before* the entry is inserted. The guard inside
        /// <c>JidAliasTable.GetCanonicalJid</c> asks afterwards, and by then
        /// <see cref="IsSelfLinkedJid"/> reads the poisoned entry itself as evidence that the
        /// contact is us, which switches that guard off.
        /// </remarks>
        private bool IsSelfPoisoningAliasPair(string aliasKey, string aliasValue)
        {
            return AliasPairPolicy.IsUnsafeToRestore(aliasKey, aliasValue, JidAlias);
        }

        /// <summary>
        /// The bookkeeping half: validates the pair, files it both ways, and reports whether it
        /// told us anything we did not already know. No UI, no disk, no scans.
        /// </summary>
        private bool TryRecordAliasMapping(string lidJid, string pnJid, string source)
        {
            string lid = NormalizeJid(lidJid);
            string pn = NormalizeJid(pnJid);
            if (!AliasPairPolicy.IsWellFormedPair(lid, pn, JidAlias))
            {
                return false;
            }

            if (AliasPairPolicy.WouldPutAContactUnderOurIdentity(lid, pn, JidAlias))
            {
                Debug.WriteLine($"[WhatsAppService] Skipping suspicious alias from {source}: {lid} -> {pn}");
                return false;
            }

            bool changed = AliasPairFiling.FileBothWays(JidAlias, lid, pn);
            RegisterSocketAlias(lid, pn, source);

            if (!changed)
            {
                // Live traffic re-states the same pair on every message. Recognising that costs a
                // dictionary lookup and saves everything below.
                return false;
            }

            RaiseReport(
                () => OnJidAliasResolved?.Invoke(this, new JidAliasResolvedEventArgs(pn, lid)),
                nameof(OnJidAliasResolved));

            _aliasFollowUps.NoteAvatarTarget(pn);
            return true;
        }

        /// <summary>
        /// Coalesces the follow-up so a burst of pairs produces one pass instead of one each.
        /// </summary>
        private void ScheduleAliasFollowUp(string source)
        {
            _aliasFollowUps.Request(source);

            CancellationToken token;
            lock (_aliasFollowUpTimerGate)
            {
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
            if (!_aliasFollowUps.TryBegin(out var batch))
            {
                // A pass is already writing the file and walking the list. Whatever arrived in the
                // meantime is still pending and will be picked up by the next timer.
                ScheduleAliasFollowUp(_aliasFollowUps.PendingSource);
                return;
            }

            string source = batch.Source;
            try
            {
                await PersistJidAliasesAsync("alias:" + source);

                if (batch.AvatarTargets.Count > 0)
                {
                    await _ui.RunAsync(() =>
                    {
                        var rows = new List<ChatItem>();
                        foreach (var pn in batch.AvatarTargets)
                        {
                            rows.AddRange(GetChatRowsForCanonicalJid(pn)
                                .Where(c => string.IsNullOrWhiteSpace(c.AvatarUrl)));
                        }

                        if (rows.Count > 0)
                        {
                            RaiseReport(
                                () => OnAvatarRetryDue?.Invoke(this, new AvatarRetryDueEventArgs(rows)),
                                nameof(OnAvatarRetryDue));
                        }
                    });
                }

                // Deduplication is global and idempotent: it groups every row by canonical JID,
                // which is what the pairs just changed. Running it once at the end covers every
                // pair in the burst, including the per-pair merge this used to do separately.
                await DeduplicateChatsAsync("alias:" + source);

                // Group list strips often stuck on LID digits until a name was reachable under the
                // phone JID. ChatAuthorProjection listens for DisplayNamesChanged — alias alone
                // did not raise it, so opening the group (roster) was the only upgrade path.
                _chatState?.NotifyDisplayNamesChanged();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Alias follow-up failed: " + ex.Message);
                _aliasFollowUps.Restore(batch);
            }
            finally
            {
                _aliasFollowUps.End();
            }
        }

        /// <summary>Delegates to <see cref="IContactService"/> (owns cooldown/dedup policy); this class only supplies the client primitives.</summary>
        private Task RefreshContactNamesAsync(bool includeGroups = false, bool force = false)
        {
            return _contactService?.RefreshContactNamesAsync(includeGroups, force) ?? Task.CompletedTask;
        }
    }
}
