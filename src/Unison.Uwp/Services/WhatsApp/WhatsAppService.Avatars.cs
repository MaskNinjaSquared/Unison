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
            var result = await FetchBestProfilePictureResultAsync(chat, lookupCandidates, token);
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

            if (!string.IsNullOrWhiteSpace(chat.AvatarHighUrl))
            {
                return;
            }

            string cached;
            DateTime fetchedAtUtc;
            if (TryGetCachedAvatarUri(chat.JID, out cached, out fetchedAtUtc, "_high"))
            {
                await RunOnUiThreadAsync(() => chat.AvatarHighUrl = cached);
                return;
            }

            var socket = _socket;
            if (socket == null || !socket.IsHandshakeComplete)
            {
                return;
            }

            foreach (var candidate in GetAvatarLookupCandidates(chat) ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                ProfilePictureResult result;
                await _usyncLock.WaitAsync();
                try
                {
                    result = await socket.GetProfilePictureUrlResultAsync(candidate, "image");
                }
                finally
                {
                    _usyncLock.Release();
                }

                if (string.IsNullOrWhiteSpace(result?.Url))
                {
                    continue;
                }

                try
                {
                    string localUri = await DownloadAndCacheAvatarAsync(
                        chat.JID,
                        result.Url,
                        CancellationToken.None,
                        "_high");
                    if (string.IsNullOrWhiteSpace(localUri))
                    {
                        continue;
                    }

                    await RunOnUiThreadAsync(() => chat.AvatarHighUrl = localUri);
                    Debug.WriteLine($"[WhatsAppService] Cached high-res group avatar for {chat.JID}");
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] High-res group avatar failed for {chat.JID}: {ex.Message}");
                }
            }
        }

        public async Task<string> GetProfilePictureUrlAsync(string jid, string type = "preview")
        {
            if (string.IsNullOrWhiteSpace(jid) || _socket == null || !_socket.IsHandshakeComplete)
            {
                return null;
            }

            try
            {
                var result = await _socket.GetProfilePictureUrlResultAsync(jid, type).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(result?.Url) ? null : result.Url;
            }
            catch (Exception ex)
            {
                Log($"[WhatsAppService] GetProfilePictureUrlAsync failed for {jid}: {ex.Message}");
                return null;
            }
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
                        if (TryGetCachedAvatarUri(candidate.Jid, out localUri, out fetchedAtUtc))
                        {
                            previewHits[candidate.Jid] = Tuple.Create(localUri, fetchedAtUtc);
                        }
                    }

                    if (candidate.NeedsHigh)
                    {
                        string highUri;
                        DateTime highFetchedAtUtc;
                        if (TryGetCachedAvatarUri(candidate.Jid, out highUri, out highFetchedAtUtc, "_high"))
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

        private static string BuildSafeAvatarFileName(string jid, string suffix = null)
        {
            string source = string.IsNullOrWhiteSpace(jid) ? Guid.NewGuid().ToString("N") : jid;
            var chars = source
                .Select(c => char.IsLetterOrDigit(c) ? c : '_')
                .ToArray();
            string safe = new string(chars).Trim('_');
            if (string.IsNullOrWhiteSpace(safe))
            {
                safe = Guid.NewGuid().ToString("N");
            }
            if (safe.Length > 96)
            {
                safe = safe.Substring(0, 96);
            }

            if (!string.IsNullOrWhiteSpace(suffix))
            {
                return safe + suffix + ".jpg";
            }

            return safe + ".jpg";
        }

        /// <summary>
        /// Downloads a remote avatar into LocalFolder/MediaCache/Avatars (JID-named file).
        /// Used by chat avatar batch and <see cref="ProfileFacade"/>.
        /// </summary>
        public Task<string> CacheRemoteAvatarAsync(string jid, string remoteUrl, CancellationToken token)
        {
            return DownloadAndCacheAvatarAsync(jid, remoteUrl, token);
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
                return false;
            }

            if (fallbackJids.Count == 0)
            {
                Debug.WriteLine($"[WhatsAppService] Group avatar fallback has no parent/community candidate for {chat.JID}");
                return false;
            }

            foreach (var fallbackJid in fallbackJids)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    Debug.WriteLine($"[WhatsAppService] Group avatar fallback trying {chat.JID} -> {fallbackJid} after {originalResult?.FailureReason}");
                    var fallbackResult = await _socket.GetProfilePictureUrlResultAsync(fallbackJid, "preview");
                    Debug.WriteLine($"[WhatsAppService] Group avatar fallback result for {chat.JID}: source={fallbackJid}, hasUrl={!string.IsNullOrWhiteSpace(fallbackResult?.Url)}, notFound={fallbackResult?.IsNotFound}, timeout={fallbackResult?.IsTimeout}, reason={fallbackResult?.FailureReason}");

                    if (string.IsNullOrWhiteSpace(fallbackResult?.Url))
                    {
                        continue;
                    }

                    string localUri = await DownloadAndCacheAvatarAsync(chat.JID, fallbackResult.Url, token);
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

                    if (_contactService != null)
                    {
                        await _contactService.NotifyAvatarCachedAsync(chat.JID, localUri);
                    }

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
            DateTime nowUtc = DateTime.UtcNow;

            await RunOnUiThreadAsync(() =>
                {
                    chat.AvatarUrl = sourceAvatar;
                    chat.AvatarFetchedAtUtc = nowUtc;
                    chat.AvatarFetchFailedAtUtc = null;
                    chat.AvatarFetchFailureReason = null;
                });

            if (_contactService != null && !string.IsNullOrWhiteSpace(sourceAvatar))
            {
                await _contactService.NotifyAvatarCachedAsync(chat.JID, sourceAvatar);
            }

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
            var group = FindGroupNode(response, groupJid);
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
                    string jid = NormalizeGroupJidCandidate(raw);
                    if (!string.IsNullOrWhiteSpace(jid))
                    {
                        candidates.Add(jid);
                    }
                }
            }
        }

        private string NormalizeGroupJidCandidate(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string value = raw.Trim();
            if (value.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeJid(value);
            }

            if (value.IndexOf('@') < 0 && value.All(char.IsDigit))
            {
                return NormalizeJid(value + "@g.us");
            }

            return null;
        }

        public void MarkAvatarImageLoadFailed(ChatItem chat, string reason)
        {
            if (chat == null)
            {
                return;
            }

            string failedUrl = chat.AvatarUrl;
            if (!string.IsNullOrWhiteSpace(failedUrl) &&
                failedUrl.StartsWith("ms-appdata:///local/MediaCache/Avatars/", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    int slashIndex = failedUrl.LastIndexOf('/');
                    string fileName = slashIndex >= 0 && slashIndex < failedUrl.Length - 1
                        ? failedUrl.Substring(slashIndex + 1)
                        : BuildSafeAvatarFileName(chat.JID);
                    string filePath = System.IO.Path.Combine(
                        ApplicationData.Current.LocalFolder.Path,
                        "MediaCache",
                        "Avatars",
                        fileName);
                    if (System.IO.File.Exists(filePath))
                    {
                        System.IO.File.Delete(filePath);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Failed to remove broken avatar cache for {chat.JID}: {ex.Message}");
                }
            }

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

        public async Task<string> GetProfilePictureAsync(string jid)
        {
            if (string.IsNullOrEmpty(jid) || _socket == null) return null;
            var result = await _socket.GetProfilePictureUrlResultAsync(jid, "image");
            if (string.IsNullOrWhiteSpace(result?.Url))
            {
                Debug.WriteLine($"[WhatsAppService] GetProfilePictureAsync returned no URL for {jid}: target={result?.TargetJid}, lookup={result?.TokenLookupJid}, reason={result?.FailureReason}");
                return null;
            }

            try
            {
                return await DownloadAndCacheAvatarAsync(jid, result.Url, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] GetProfilePictureAsync cache failed for {jid}: {ex.Message}");
                return result.Url;
            }
        }
    }
}
