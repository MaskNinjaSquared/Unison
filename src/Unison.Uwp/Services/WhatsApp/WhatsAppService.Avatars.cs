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

        /// <summary>Fetches the best available profile picture for a chat (incl. group-avatar fallback) and applies it.</summary>
        public async Task FetchAndApplyAvatarAsync(ChatItem chat, CancellationToken token, bool fetchHighQuality = true)
        {
            if (chat == null)
            {
                return;
            }

            var lookupCandidates = GetAvatarLookupCandidates(chat);
            var result = await _avatarFetcher.FetchPreviewAsync(lookupCandidates, token);
            await ApplyAvatarResultAsync(chat, result, token);
            if (fetchHighQuality)
            {
                _ = EnsureHighQualityGroupAvatarAsync(chat);
            }
        }

        public Task EnsureHighQualityGroupAvatarAsync(ChatItem chat)
        {
            return EnsureHighQualityGroupAvatarCoreAsync(chat);
        }

        private async Task EnsureHighQualityGroupAvatarCoreAsync(ChatItem chat)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return;
            }

            // Both interfaces declare this a no-op for 1:1 chats and the guard was missing,
            // so every visible direct chat spent a second CDN round trip on a file nothing
            // reads: the cache hydration that restores AvatarHighUrl between sessions is
            // itself group-only, so a contact's high file was fetched and then forgotten.
            if (!chat.IsGroup)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(chat.AvatarHighUrl))
            {
                return;
            }

            string localUri = await _avatarFetcher.FetchHighResolutionAsync(
                chat.JID,
                GetAvatarLookupCandidates(chat),
                CancellationToken.None);
            if (string.IsNullOrWhiteSpace(localUri))
            {
                return;
            }

            await RunOnUiThreadAsync(() => chat.AvatarHighUrl = localUri);
        }

        public Task<string> GetProfilePictureUrlAsync(string jid, string type = "preview")
        {
            return _avatarFetcher.FetchUrlAsync(jid, type);
        }

        private async Task HydrateCachedAvatarUrisAsync(string reason)
        {
            // Snapshot what needs a disk check on the UI thread, probe files off-UI, then apply
            // property updates in one UI pass. File Exists / GetLastWriteTime must not run while
            // holding the chat list dispatcher.
            List<AvatarHydrateCandidate> candidates = null;
            await RunOnUiThreadAsync(() =>
            {
                candidates = new List<AvatarHydrateCandidate>();
                foreach (var chat in Chats)
                {
                    if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
                    {
                        continue;
                    }

                    bool needsPreview = string.IsNullOrWhiteSpace(chat.AvatarUrl) ||
                                        chat.AvatarUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                        chat.AvatarUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

                    bool needsHigh = chat.IsGroup &&
                                     (string.IsNullOrWhiteSpace(chat.AvatarHighUrl) ||
                                      chat.AvatarHighUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                      chat.AvatarHighUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

                    if (!needsPreview && !needsHigh)
                    {
                        continue;
                    }

                    candidates.Add(new AvatarHydrateCandidate
                    {
                        Jid = chat.JID,
                        NeedsPreview = needsPreview,
                        NeedsHigh = needsHigh
                    });
                }
            });

            if (candidates == null || candidates.Count == 0)
            {
                return;
            }

            var previewHits = new Dictionary<string, Tuple<string, DateTime>>(StringComparer.OrdinalIgnoreCase);
            var highHits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            await Task.Run(() =>
            {
                foreach (var candidate in candidates)
                {
                    if (candidate.NeedsPreview)
                    {
                        string localUri;
                        DateTime fetchedAtUtc;
                        if (_avatarCache.TryGet(candidate.Jid, AvatarVariant.Preview, out localUri, out fetchedAtUtc))
                        {
                            previewHits[candidate.Jid] = Tuple.Create(localUri, fetchedAtUtc);
                        }
                    }

                    if (candidate.NeedsHigh)
                    {
                        string highUri;
                        DateTime highFetchedAtUtc;
                        if (_avatarCache.TryGet(candidate.Jid, AvatarVariant.HighResolution, out highUri, out highFetchedAtUtc))
                        {
                            highHits[candidate.Jid] = highUri;
                        }
                    }
                }
            }).ConfigureAwait(false);

            if (previewHits.Count == 0 && highHits.Count == 0)
            {
                return;
            }

            int hydrated = 0;
            await RunOnUiThreadAsync(() =>
            {
                foreach (var chat in Chats)
                {
                    if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
                    {
                        continue;
                    }

                    Tuple<string, DateTime> preview;
                    if (previewHits.TryGetValue(chat.JID, out preview))
                    {
                        chat.AvatarUrl = preview.Item1;
                        chat.AvatarFetchedAtUtc = preview.Item2;
                        chat.AvatarFetchFailedAtUtc = null;
                        chat.AvatarFetchFailureReason = null;
                        hydrated++;
                    }

                    string highUri;
                    if (highHits.TryGetValue(chat.JID, out highUri))
                    {
                        chat.AvatarHighUrl = highUri;
                    }
                }

                if (hydrated > 0)
                {
                    Debug.WriteLine($"[WhatsAppService] Hydrated {hydrated} avatar URLs from local cache ({reason})");
                    SchedulePersist();
                }
            });
        }

        private sealed class AvatarHydrateCandidate
        {
            public string Jid;
            public bool NeedsPreview;
            public bool NeedsHigh;
        }

        private async Task<bool> TryApplyGroupAvatarFallbackAsync(ChatItem chat, ProfilePictureResult originalResult, CancellationToken token)
        {
            if (chat == null || !chat.IsGroup || _socket == null || !ShouldTryGroupAvatarFallback(originalResult))
            {
                return false;
            }

            token.ThrowIfCancellationRequested();

            List<string> fallbackJids;
            try
            {
                var metadata = await _socket.QueryGroupMetadataAsync(chat.JID);
                fallbackJids = ExtractGroupAvatarFallbackJids(metadata, chat.JID);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Group avatar fallback metadata query failed for {chat.JID}: {ex.Message}");
                return await TryApplySiblingGroupAvatarFallbackAsync(chat, token);
            }

            // The sibling attempt used to sit only after this loop, so it was unreachable for
            // any group without a community above it -- which is most of them, and exactly
            // the shape the sibling case is about.
            if (fallbackJids.Count == 0)
            {
                Debug.WriteLine($"[WhatsAppService] Group avatar fallback has no parent/community candidate for {chat.JID}");
                return await TryApplySiblingGroupAvatarFallbackAsync(chat, token);
            }

            foreach (var fallbackJid in fallbackJids)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    Debug.WriteLine($"[WhatsAppService] Group avatar fallback trying {chat.JID} -> {fallbackJid} after {originalResult?.FailureReason}");
                    string fallbackUrl = await _avatarFetcher.FetchUrlAsync(fallbackJid);
                    if (string.IsNullOrWhiteSpace(fallbackUrl))
                    {
                        continue;
                    }

                    string localUri = await _avatarFetcher.CachePreviewAsync(chat.JID, fallbackUrl, token);
                    if (string.IsNullOrWhiteSpace(localUri))
                    {
                        continue;
                    }

                    DateTime nowUtc = DateTime.UtcNow;
                    await RunOnUiThreadAsync(() =>
                        {
                            chat.AvatarUrl = localUri;
                            chat.AvatarFetchedAtUtc = nowUtc;
                            chat.AvatarFetchFailedAtUtc = null;
                            chat.AvatarFetchFailureReason = null;
                        });

                    ReportAvatarCached(chat.JID, localUri);

                    Debug.WriteLine($"[WhatsAppService] Group avatar fallback cached {chat.JID} from {fallbackJid}");
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Group avatar fallback failed for {chat.JID} via {fallbackJid}: {ex.Message}");
                }
            }

            return await TryApplySiblingGroupAvatarFallbackAsync(chat, token);
        }

        private async Task<bool> TryApplySiblingGroupAvatarFallbackAsync(ChatItem chat, CancellationToken token)
        {
            var source = FindSiblingGroupAvatarSource(chat);
            if (source == null)
            {
                return false;
            }

            token.ThrowIfCancellationRequested();
            string sourceJid = source.JID;
            string sourceAvatar = source.AvatarUrl;
            // Same group, so the sibling's high-resolution file is this row's too. Leaving it
            // behind sent the row straight back out for a second fetch of a file we hold.
            string sourceAvatarHigh = source.AvatarHighUrl;
            DateTime nowUtc = DateTime.UtcNow;

            await RunOnUiThreadAsync(() =>
                {
                    chat.AvatarUrl = sourceAvatar;
                    if (!string.IsNullOrWhiteSpace(sourceAvatarHigh))
                    {
                        chat.AvatarHighUrl = sourceAvatarHigh;
                    }

                    chat.AvatarFetchedAtUtc = nowUtc;
                    chat.AvatarFetchFailedAtUtc = null;
                    chat.AvatarFetchFailureReason = null;
                });

            ReportAvatarCached(chat.JID, sourceAvatar);

            Debug.WriteLine($"[WhatsAppService] Group avatar sibling fallback copied {chat.JID} from same-subject group {sourceJid}");
            return true;
        }

        private static bool ShouldTryGroupAvatarFallback(ProfilePictureResult result)
        {
            if (result == null || !string.IsNullOrWhiteSpace(result.Url))
            {
                return false;
            }

            return result.IsNotFound ||
                   string.Equals(result.FailureReason, "server-error:401", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(result.FailureReason, "server-error:404", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(result.FailureReason, "server-error:406", StringComparison.OrdinalIgnoreCase);
        }

        private List<string> ExtractGroupAvatarFallbackJids(BinaryNode response, string groupJid)
        {
            var candidates = new List<string>();
            var group = _groupMetadata.FindGroupNode(response, groupJid);
            if (group == null)
            {
                return candidates;
            }

            AddGroupAvatarCandidate(candidates, group.GetChild("linked_parent"));
            AddGroupAvatarCandidate(candidates, group.GetChild("parent"));
            AddGroupAvatarCandidate(candidates, group.GetChild("default_sub_group"));
            AddGroupAvatarCandidate(candidates, group.GetChild("default_sub_community"));

            return candidates
                .Where(j => !string.IsNullOrWhiteSpace(j) &&
                            !string.Equals(NormalizeJid(j), NormalizeJid(groupJid), StringComparison.OrdinalIgnoreCase))
                .Select(NormalizeJid)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void AddGroupAvatarCandidate(List<string> candidates, BinaryNode node)
        {
            if (node?.Attrs == null)
            {
                return;
            }

            foreach (var key in new[] { "jid", "id", "parent", "linked_parent" })
            {
                if (node.Attrs.TryGetValue(key, out var raw))
                {
                    string jid = _groupMetadata.NormalizeGroupJid(raw);
                    if (!string.IsNullOrWhiteSpace(jid))
                    {
                        candidates.Add(jid);
                    }
                }
            }
        }

        public void MarkAvatarImageLoadFailed(ChatItem chat, string reason)
        {
            if (chat == null)
            {
                return;
            }

            _avatarCache.DeleteIfCached(chat.AvatarUrl);

            // Nao mantenha uma URI local quebrada nem aplique o backoff de 30 minutos:
            // isso fazia a foto desaparecer durante toda a sessao. A linha visivel pede
            // uma nova consulta imediatamente, tentando tambem o JID alternativo PN/LID.
            chat.AvatarUrl = null;
            chat.AvatarFetchedAtUtc = null;
            chat.AvatarFetchFailedAtUtc = null;
            chat.AvatarFetchFailureReason = string.IsNullOrWhiteSpace(reason) ? "ui-image-failed" : reason;
            Debug.WriteLine($"[WhatsAppService] UI avatar image load failed for {chat.JID}: {chat.AvatarFetchFailureReason}");
            RequestAvatarRefresh(chat, force: true);
            SchedulePersist();
        }

        /// <summary>Delegates to <see cref="IContactService"/> (owns dedup/backoff policy); this class only supplies the fetch primitive.</summary>
        public void RequestAvatarRefresh(ChatItem chat, bool force = false)
        {
            _contactService?.RequestAvatarRefresh(chat, force);
        }

        /// <summary>Delegates to <see cref="IContactService"/> (owns batch/backoff policy); this class only supplies the fetch primitives.</summary>
        public Task RetrieveContactPicturesCoreAsync(CancellationToken token)
        {
            if (_socket == null)
            {
                return Task.CompletedTask;
            }

            return _contactService?.RetrieveContactPicturesAsync(token) ?? Task.CompletedTask;
        }

        private string FindAvatarOnChatRows(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            foreach (var row in GetChatRowsForCanonicalJid(jid))
            {
                if (row != null && !string.IsNullOrWhiteSpace(row.AvatarUrl))
                {
                    return row.AvatarUrl;
                }
            }

            return null;
        }

        public Task<string> GetProfilePictureAsync(string jid)
        {
            return _avatarFetcher.FetchAndCacheAsync(jid, CancellationToken.None);
        }
    }
}
