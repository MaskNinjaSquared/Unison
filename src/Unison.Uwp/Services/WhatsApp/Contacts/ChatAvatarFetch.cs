using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.Uwp.Client;
using Unison.Uwp.Services.Socket;
using Unison.Uwp.Services.WhatsApp.Groups;

namespace Unison.Uwp.Services.WhatsApp.Contacts
{
    /// <summary>
    /// Fetches a chat picture and writes it on the row, including a group's parent or sibling
    /// when the group itself has none.
    /// </summary>
    internal sealed class ChatAvatarFetch
    {
        private const string GroupAvatarFallbackMissReason = "group-avatar-fallback-miss";

        private readonly IDispatcher _ui;
        private readonly IChatCatalogPersistence _catalog;
        private readonly IWhatsAppSessionProvider _sessions;
        private readonly IGroupService _groups;
        private readonly IJidResolver _jids;
        private readonly IChatStateStore _chats;
        private readonly IAvatarReport _report;
        private readonly AvatarFetcher _avatars;
        private readonly IAvatarCache _cache;
        private readonly GroupMetadataReader _reader;

        public ChatAvatarFetch(
            IWhatsAppSessionProvider sessions,
            IGroupService groups,
            IJidResolver jids,
            IChatStateStore chats,
            IAvatarReport report,
            AvatarFetcher avatars,
            IAvatarCache cache,
            IDispatcher ui,
            IChatCatalogPersistence catalog)
        {
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _groups = groups ?? throw new ArgumentNullException(nameof(groups));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _report = report ?? throw new ArgumentNullException(nameof(report));
            _avatars = avatars ?? throw new ArgumentNullException(nameof(avatars));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _reader = new GroupMetadataReader(jids);
        }

        public async Task FetchAndApplyAsync(ChatItem chat, CancellationToken token, bool fetchHighQuality)
        {
            if (chat == null)
            {
                return;
            }

            string alias;
            _jids.TryGetAlias(chat.JID, out alias);
            var result = await _avatars.FetchPreviewAsync(
                ChatPinStoreKeys.For(chat.JID, _jids.GetCanonicalJid(chat.JID), alias),
                token);
            await ApplyAsync(chat, result, token);
            if (fetchHighQuality)
            {
                _ = _groups.EnsureHighQualityGroupAvatarAsync(chat);
            }
        }

        public async Task HydrateCachedAsync(string reason)
        {
            List<AvatarHydrateCandidate> candidates = null;
            await _ui.RunAsync(() =>
            {
                candidates = new List<AvatarHydrateCandidate>();
                foreach (ChatItem chat in _chats.Chats)
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
                foreach (AvatarHydrateCandidate candidate in candidates)
                {
                    string localUri;
                    DateTime fetchedAtUtc;
                    if (candidate.NeedsPreview &&
                        _cache.TryGet(candidate.Jid, AvatarVariant.Preview, out localUri, out fetchedAtUtc))
                    {
                        previewHits[candidate.Jid] = Tuple.Create(localUri, fetchedAtUtc);
                    }

                    string highUri;
                    DateTime highFetchedAtUtc;
                    if (candidate.NeedsHigh &&
                        _cache.TryGet(candidate.Jid, AvatarVariant.HighResolution, out highUri, out highFetchedAtUtc))
                    {
                        highHits[candidate.Jid] = highUri;
                    }
                }
            }).ConfigureAwait(false);

            if (previewHits.Count == 0 && highHits.Count == 0)
            {
                return;
            }

            int hydrated = 0;
            await _ui.RunAsync(() =>
            {
                foreach (ChatItem chat in _chats.Chats)
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
                    _catalog.Schedule();
                }
            });
        }

        private async Task ApplyAsync(ChatItem chat, ProfilePictureResult result, CancellationToken token)
        {
            if (chat == null || result == null)
            {
                return;
            }

            DateTime nowUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(result.Url))
            {
                string localUri = null;
                try
                {
                    localUri = await _avatars.CachePreviewAsync(chat.JID, result.Url, token);
                }
                catch (Exception ex)
                {
                    await _ui.RunAsync(() =>
                        ChatAvatarOutcome.RecordFailure(chat, "download:" + ex.Message, nowUtc));
                    Debug.WriteLine($"[WhatsAppService] Avatar download/cache failed for {chat.JID}: target={result.TargetJid}, reason={ex.Message}");
                    return;
                }

                if (string.IsNullOrWhiteSpace(localUri))
                {
                    await _ui.RunAsync(() =>
                        ChatAvatarOutcome.RecordFailure(chat, "download:empty", nowUtc));
                    return;
                }

                await _ui.RunAsync(() =>
                {
                    ChatAvatarOutcome.RecordCached(chat, localUri, nowUtc);
                    _groups.ApplyGroupMemberAvatarOutcome(
                        chat.JID,
                        new GroupMemberAvatarFetchResult { LocalUri = localUri });
                });
                _report.ReportCached(chat.JID, localUri);
                return;
            }

            if (result.IsNotFound)
            {
                if (await TryGroupFallbackAsync(chat, result, token))
                {
                    return;
                }

                string failureReason = result.FailureReason ?? "no-picture";
                if (chat.IsGroup && ShouldTry(result))
                {
                    failureReason = failureReason + ":" + GroupAvatarFallbackMissReason;
                }

                await _ui.RunAsync(() =>
                    ChatAvatarOutcome.RecordAbsent(chat, failureReason, nowUtc));
                Debug.WriteLine($"[WhatsAppService] Avatar confirmed absent for {chat.JID}: target={result.TargetJid}, reason={failureReason}");
                return;
            }

            if (await TryGroupFallbackAsync(chat, result, token))
            {
                return;
            }

            await _ui.RunAsync(() =>
                ChatAvatarOutcome.RecordFailure(
                    chat,
                    result.FailureReason ?? (result.IsTimeout ? "timeout" : "transient"),
                    nowUtc));
            Debug.WriteLine($"[WhatsAppService] Avatar refresh failed without clearing existing image for {chat.JID}: target={result.TargetJid}, lookup={result.TokenLookupJid}, reason={chat.AvatarFetchFailureReason}");
        }

        private async Task<bool> TryGroupFallbackAsync(ChatItem chat, ProfilePictureResult originalResult, CancellationToken token)
        {
            var socket = _sessions.Socket;
            if (chat == null || !chat.IsGroup || socket == null || !ShouldTry(originalResult))
            {
                return false;
            }

            token.ThrowIfCancellationRequested();

            List<string> fallbackJids;
            try
            {
                var metadata = await socket.QueryGroupMetadataAsync(chat.JID);
                fallbackJids = GroupAvatarFallbackJids.From(_reader.FindGroupNode(metadata, chat.JID), chat.JID);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Group avatar fallback metadata query failed for {chat.JID}: {ex.Message}");
                return await TrySiblingAsync(chat, token);
            }

            if (fallbackJids.Count == 0)
            {
                Debug.WriteLine($"[WhatsAppService] Group avatar fallback has no parent/community candidate for {chat.JID}");
                return await TrySiblingAsync(chat, token);
            }

            foreach (string fallbackJid in fallbackJids)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    Debug.WriteLine($"[WhatsAppService] Group avatar fallback trying {chat.JID} -> {fallbackJid} after {originalResult?.FailureReason}");
                    string fallbackUrl = await _avatars.FetchUrlAsync(fallbackJid);
                    if (string.IsNullOrWhiteSpace(fallbackUrl))
                    {
                        continue;
                    }

                    string localUri = await _avatars.CachePreviewAsync(chat.JID, fallbackUrl, token);
                    if (string.IsNullOrWhiteSpace(localUri))
                    {
                        continue;
                    }

                    DateTime nowUtc = DateTime.UtcNow;
                    await _ui.RunAsync(() =>
                    {
                        chat.AvatarUrl = localUri;
                        chat.AvatarFetchedAtUtc = nowUtc;
                        chat.AvatarFetchFailedAtUtc = null;
                        chat.AvatarFetchFailureReason = null;
                    });
                    _report.ReportCached(chat.JID, localUri);
                    Debug.WriteLine($"[WhatsAppService] Group avatar fallback cached {chat.JID} from {fallbackJid}");
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Group avatar fallback failed for {chat.JID} via {fallbackJid}: {ex.Message}");
                }
            }

            return await TrySiblingAsync(chat, token);
        }

        private async Task<bool> TrySiblingAsync(ChatItem chat, CancellationToken token)
        {
            ChatItem source = SiblingGroupAvatar.Find(chat, _chats.Chats);
            if (source == null)
            {
                return false;
            }

            token.ThrowIfCancellationRequested();
            string sourceJid = source.JID;
            string sourceAvatar = source.AvatarUrl;
            string sourceAvatarHigh = source.AvatarHighUrl;
            DateTime nowUtc = DateTime.UtcNow;

            await _ui.RunAsync(() =>
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
            _report.ReportCached(chat.JID, sourceAvatar);
            Debug.WriteLine($"[WhatsAppService] Group avatar sibling fallback copied {chat.JID} from same-subject group {sourceJid}");
            return true;
        }

        private static bool ShouldTry(ProfilePictureResult result)
        {
            if (result == null)
            {
                return false;
            }

            return GroupAvatarFallbackDecision.ShouldTry(result.Url, result.IsNotFound, result.FailureReason);
        }

        private sealed class AvatarHydrateCandidate
        {
            public string Jid;
            public bool NeedsPreview;
            public bool NeedsHigh;
        }
    }
}
