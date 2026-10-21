using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Data.Xml.Dom;
using Windows.UI.Core;
using Windows.UI.Notifications;
using Windows.UI.StartScreen;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Uwp.Helpers;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// WinRT SecondaryTile adapter for <see cref="IShortcutService"/>.
    /// Chat tiles (<c>c_</c>) open the conversation; contact tiles (<c>p_</c>) open Profile.
    /// </summary>
    public sealed class ShortcutService : IShortcutService
    {
        private static readonly Uri DefaultLogoUri =
            new Uri("ms-appx:///Assets/Square150x150Logo.png");

        private static readonly Uri ChatBalloonUri =
            new Uri("ms-appx:///Assets/Tiles/chat-balloon.png");

        private static readonly Uri ProfileGlyphUri =
            new Uri("ms-appx:///Assets/Tiles/profile-glyph.png");

        private const string TileIdPrefix = "c_";
        private const string ContactTileIdPrefix = "p_";
        private const int PreviewMaxChars = 120;
        private const int MottoMaxChars = 80;

        /// <summary>
        /// Always the last rotating queue frame on chat / profile / community tiles.
        /// </summary>
        private const string UnisonBrandName = "Unison";

        /// <summary>
        /// Distinct tags so <see cref="TileUpdater.EnableNotificationQueue"/> keeps every frame.
        /// Untagged updates replace each other and the small tile never rotates.
        /// Community: hub logo + Avisos + channel + Unison (≤5). Profile: photo + motto + unread + Unison.
        /// </summary>
        private const string QueueTagPhoto = "photo";
        private const string QueueTagGlyph = "glyph";
        private const string QueueTagMotto = "motto";
        private const string QueueTagUnread = "unread";
        private const string QueueTagHub = "hub";
        private const string QueueTagAvisos = "avisos";
        private const string QueueTagChannel = "channel";
        private const string QueueTagUnison = "unison";

        private static readonly object CommunityTileSync = new object();

        /// <summary>Hub tile id → last pushed community frames (for live channel rotation).</summary>
        private static readonly Dictionary<string, CommunityTileState> CommunityStateByHubTileId =
            new Dictionary<string, CommunityTileState>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Member chat JID → hub secondary tile id.</summary>
        private static readonly Dictionary<string, string> MemberJidToHubTileId =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public async Task<bool> PinChatAsync(ChatItem chat)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return false;
            }

            string tileId = BuildTileId(chat.JID);
            string displayName = ResolveDisplayName(chat);
            string arguments = BuildChatLaunchArgument(chat.JID);
            Uri logo = ResolveLogoUri(chat.GetAvatarUrl(preferHigh: false));

            try
            {
                if (SecondaryTile.Exists(tileId))
                {
                    var existing = new SecondaryTile(tileId)
                    {
                        DisplayName = displayName,
                        Arguments = arguments
                    };
                    ApplyChatTileChrome(existing, logo);
                    await existing.UpdateAsync();
                    PushPinnedPresentation(chat);
                    return true;
                }

                var tile = new SecondaryTile(
                    tileId,
                    displayName,
                    arguments,
                    logo,
                    TileSize.Default)
                {
                    DisplayName = displayName
                };
                ApplyChatTileChrome(tile, logo);

                bool created = await tile.RequestCreateAsync();
                if (created)
                {
                    PushPinnedPresentation(chat);
                }

                return created;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShortcutService] PinChatAsync failed: " + ex.Message);
                return false;
            }
        }

        public async Task<bool> PinContactAsync(ChatItem chat)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return false;
            }

            string tileId = BuildContactTileId(chat.JID);
            string displayName = ResolveDisplayName(chat);
            string arguments = BuildProfileLaunchArgument(chat.JID);
            Uri logo = ResolveLogoUri(chat.GetAvatarUrl(preferHigh: false));

            try
            {
                if (SecondaryTile.Exists(tileId))
                {
                    var existing = new SecondaryTile(tileId)
                    {
                        DisplayName = displayName,
                        Arguments = arguments
                    };
                    ApplyContactTileChrome(existing, logo);
                    await existing.UpdateAsync();
                    PushPinnedContactPresentation(chat);
                    return true;
                }

                var tile = new SecondaryTile(
                    tileId,
                    displayName,
                    arguments,
                    logo,
                    TileSize.Default)
                {
                    DisplayName = displayName
                };
                ApplyContactTileChrome(tile, logo);

                bool created = await tile.RequestCreateAsync();
                if (created)
                {
                    PushPinnedContactPresentation(chat);
                }

                return created;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShortcutService] PinContactAsync failed: " + ex.Message);
                return false;
            }
        }

        public async Task<bool> UnpinChatAsync(string chatJid)
        {
            if (string.IsNullOrWhiteSpace(chatJid))
            {
                return false;
            }

            string tileId = BuildTileId(chatJid);
            try
            {
                if (!SecondaryTile.Exists(tileId))
                {
                    return false;
                }

                try
                {
                    TileUpdateManager.CreateTileUpdaterForSecondaryTile(tileId).Clear();
                    BadgeUpdateManager.CreateBadgeUpdaterForSecondaryTile(tileId).Clear();
                }
                catch
                {
                }

                var tile = new SecondaryTile(tileId);
                return await tile.RequestDeleteAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShortcutService] UnpinChatAsync failed: " + ex.Message);
                return false;
            }
        }

        public async Task UnpinAllChatsAsync()
        {
            var dispatcher = CoreApplication.MainView?.CoreWindow?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasThreadAccess)
            {
                var done = new TaskCompletionSource<bool>();
                await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    UnpinAllChatsCoreAsync().ContinueWith(task =>
                    {
                        if (task.Exception != null)
                        {
                            done.TrySetException(task.Exception);
                        }
                        else
                        {
                            done.TrySetResult(true);
                        }
                    });
                });
                await done.Task;
                return;
            }

            await UnpinAllChatsCoreAsync();
        }

        private async Task UnpinAllChatsCoreAsync()
        {
            IReadOnlyList<SecondaryTile> tiles;
            try
            {
                tiles = await SecondaryTile.FindAllAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShortcutService] UnpinAll FindAll failed: " + ex.Message);
                return;
            }

            if (tiles == null)
            {
                return;
            }

            foreach (SecondaryTile tile in tiles)
            {
                if (tile == null ||
                    string.IsNullOrWhiteSpace(tile.TileId) ||
                    (!tile.TileId.StartsWith(TileIdPrefix, StringComparison.OrdinalIgnoreCase) &&
                     !tile.TileId.StartsWith(ContactTileIdPrefix, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                try
                {
                    TileUpdateManager.CreateTileUpdaterForSecondaryTile(tile.TileId).Clear();
                    BadgeUpdateManager.CreateBadgeUpdaterForSecondaryTile(tile.TileId).Clear();
                    await new SecondaryTile(tile.TileId).RequestDeleteAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ShortcutService] UnpinAll " + tile.TileId + " failed: " + ex.Message);
                }
            }
        }

        public async Task<bool> UnpinContactAsync(string chatJid)
        {
            if (string.IsNullOrWhiteSpace(chatJid))
            {
                return false;
            }

            string tileId = BuildContactTileId(chatJid);
            try
            {
                if (!SecondaryTile.Exists(tileId))
                {
                    return false;
                }

                try
                {
                    TileUpdateManager.CreateTileUpdaterForSecondaryTile(tileId).Clear();
                    BadgeUpdateManager.CreateBadgeUpdaterForSecondaryTile(tileId).Clear();
                }
                catch
                {
                }

                return await new SecondaryTile(tileId).RequestDeleteAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShortcutService] UnpinContactAsync failed: " + ex.Message);
                return false;
            }
        }

        public Task<bool> IsChatPinnedAsync(string chatJid)
        {
            if (string.IsNullOrWhiteSpace(chatJid))
            {
                return Task.FromResult(false);
            }

            try
            {
                return Task.FromResult(SecondaryTile.Exists(BuildTileId(chatJid)));
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        public Task<bool> IsContactPinnedAsync(string chatJid)
        {
            if (string.IsNullOrWhiteSpace(chatJid))
            {
                return Task.FromResult(false);
            }

            try
            {
                return Task.FromResult(SecondaryTile.Exists(BuildContactTileId(chatJid)));
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        public void UpdateChatUnread(string chatJid, int unreadCount)
        {
            if (string.IsNullOrWhiteSpace(chatJid))
            {
                return;
            }

            try
            {
                string chatTileId = BuildTileId(chatJid);
                if (SecondaryTile.Exists(chatTileId))
                {
                    ApplyBadge(chatTileId, unreadCount);
                }

                string contactTileId = BuildContactTileId(chatJid);
                if (SecondaryTile.Exists(contactTileId))
                {
                    string displayName = "User";
                    Uri logo = DefaultLogoUri;
                    try
                    {
                        var existing = new SecondaryTile(contactTileId);
                        if (!string.IsNullOrWhiteSpace(existing.DisplayName))
                        {
                            displayName = existing.DisplayName.Trim();
                        }

                        if (existing.VisualElements?.Square150x150Logo != null)
                        {
                            logo = existing.VisualElements.Square150x150Logo;
                        }
                    }
                    catch
                    {
                    }

                    PushContactLiveTiles(contactTileId, displayName, logo, motto: null, unreadCount);
                    ApplyBadge(contactTileId, unreadCount);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShortcutService] UpdateChatUnread failed: " + ex.Message);
            }
        }

        public void UpdatePinnedChatTile(
            string chatJid,
            int unreadCount,
            string title,
            string preview,
            string avatarUrl)
        {
            if (string.IsNullOrWhiteSpace(chatJid))
            {
                return;
            }

            string safeTitle = string.IsNullOrWhiteSpace(title) ? "Chat" : title.Trim();
            Uri logo = ResolveLogoUri(avatarUrl);

            try
            {
                string chatTileId = BuildTileId(chatJid);
                if (SecondaryTile.Exists(chatTileId) && !IsKnownCommunityHubTile(chatTileId))
                {
                    // Same layout as the primary Unison Start tile, scoped to this chat only.
                    PushLiveTile(chatTileId, safeTitle, preview, logo, isGroup: true);
                    ApplyBadge(chatTileId, unreadCount);
                }

                // Linked subgroup (or Avisos) may rotate a pinned community hub even when the
                // subgroup itself is not on Start.
                TryPushCommunityLiveUpdate(chatJid, safeTitle, preview, logo, unreadCount);

                string contactTileId = BuildContactTileId(chatJid);
                if (SecondaryTile.Exists(contactTileId))
                {
                    PushContactLiveTiles(contactTileId, safeTitle, logo, motto: null, unreadCount);
                    ApplyBadge(contactTileId, unreadCount);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShortcutService] UpdatePinnedChatTile failed: " + ex.Message);
            }
        }

        public async Task RefreshPinnedUnreadAsync(IEnumerable<ChatItem> chats)
        {
            IReadOnlyList<SecondaryTile> tiles;
            try
            {
                tiles = await SecondaryTile.FindAllAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShortcutService] FindAllAsync failed: " + ex.Message);
                return;
            }

            if (tiles == null || tiles.Count == 0)
            {
                return;
            }

            var byChatTileId = new Dictionary<string, ChatItem>(StringComparer.OrdinalIgnoreCase);
            var byContactTileId = new Dictionary<string, ChatItem>(StringComparer.OrdinalIgnoreCase);
            if (chats != null)
            {
                foreach (var chat in chats)
                {
                    if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
                    {
                        continue;
                    }

                    byChatTileId[BuildTileId(chat.JID)] = chat;
                    byContactTileId[BuildContactTileId(chat.JID)] = chat;
                }
            }

            foreach (var tile in tiles)
            {
                if (tile == null || string.IsNullOrWhiteSpace(tile.TileId))
                {
                    continue;
                }

                if (tile.TileId.StartsWith(TileIdPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (byChatTileId.TryGetValue(tile.TileId, out var chat))
                    {
                        PushPinnedPresentation(chat);
                    }
                    else
                    {
                        TryClearBadge(tile.TileId);
                    }

                    continue;
                }

                if (tile.TileId.StartsWith(ContactTileIdPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (byContactTileId.TryGetValue(tile.TileId, out var contactChat))
                    {
                        PushPinnedContactPresentation(contactChat);
                    }
                    else
                    {
                        TryClearBadge(tile.TileId);
                    }
                }
            }
        }

        private static void TryClearBadge(string tileId)
        {
            try
            {
                BadgeUpdateManager.CreateBadgeUpdaterForSecondaryTile(tileId).Clear();
            }
            catch
            {
            }
        }

        /// <summary>Stable SecondaryTile id for chat Start tiles (≤64 chars).</summary>
        public static string BuildTileId(string chatJid) =>
            BuildPrefixedTileId(TileIdPrefix, chatJid);

        /// <summary>Stable SecondaryTile id for contact Profile Start tiles.</summary>
        public static string BuildContactTileId(string chatJid) =>
            BuildPrefixedTileId(ContactTileIdPrefix, chatJid);

        private static string BuildPrefixedTileId(string prefix, string chatJid)
        {
            string norm = (chatJid ?? string.Empty).Trim().ToLowerInvariant();
            using (var sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(norm));
                var sb = new StringBuilder(prefix, prefix.Length + 32);
                for (int i = 0; i < 16 && i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }

                return sb.ToString();
            }
        }

        private void PushPinnedPresentation(ChatItem chat)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return;
            }

            string tileId = BuildTileId(chat.JID);
            if (!SecondaryTile.Exists(tileId))
            {
                return;
            }

            if (IsCommunityHub(chat))
            {
                PushCommunityLiveTiles(chat);
                return;
            }

            string preview = FormatAuthorPreview(chat.LastMessageAuthor, chat.LastMessage);
            PushLiveTile(
                tileId,
                ResolveChatTitle(chat),
                preview,
                ResolveLogoUri(chat.GetAvatarUrl(preferHigh: false)),
                chat.IsGroup || chat.IsCommunityAnnounce);
            ApplyBadge(tileId, chat.UnreadCount);
        }

        private void PushPinnedContactPresentation(ChatItem chat)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return;
            }

            string tileId = BuildContactTileId(chat.JID);
            if (!SecondaryTile.Exists(tileId))
            {
                return;
            }

            PushContactLiveTiles(
                tileId,
                ResolveDisplayName(chat),
                ResolveLogoUri(chat.GetAvatarUrl(preferHigh: false)),
                ResolveContactMotto(chat),
                chat.UnreadCount);
            ApplyBadge(tileId, chat.UnreadCount);
        }

        private static void ApplyBadge(string tileId, int unreadCount)
        {
            var updater = BadgeUpdateManager.CreateBadgeUpdaterForSecondaryTile(tileId);
            if (unreadCount <= 0)
            {
                updater.Clear();
                return;
            }

            int badgeValue = unreadCount;
            if (SystemInfoProvider.DetectIsMobile() && unreadCount > 99)
            {
                badgeValue = 100;
            }

            var badgeXml = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);
            var badgeElement = badgeXml.SelectSingleNode("/badge") as XmlElement;
            if (badgeElement == null)
            {
                return;
            }

            badgeElement.SetAttribute("value", badgeValue.ToString());
            updater.Update(new BadgeNotification(badgeXml));
        }

        /// <summary>
        /// Chat / group Start tile — same caption+body layout as <see cref="LiveTilesService"/>,
        /// filtered to this chat. Queue: photo, balloon, then Unison logo (always last).
        /// </summary>
        private static void PushLiveTile(
            string tileId,
            string title,
            string preview,
            Uri imageUri,
            bool isGroup)
        {
            _ = isGroup;
            string safeTitle = EscapeXml(Truncate(title, 40));
            string safePreview = EscapeXml(Truncate(preview, PreviewMaxChars));
            string imageSrc = EscapeXml(imageUri?.AbsoluteUri ?? DefaultLogoUri.AbsoluteUri);
            string balloonSrc = EscapeXml(ChatBalloonUri.AbsoluteUri);

            // Primary Start tile style (LiveTilesService): caption = chat, body = preview.
            string mediumBody =
                "<text hint-style=\"caption\">" + safeTitle + "</text>" +
                (string.IsNullOrEmpty(safePreview)
                    ? string.Empty
                    : "<text hint-style=\"body\" hint-wrap=\"true\">" + safePreview + "</text>");

            string wideBody =
                "<text hint-style=\"caption\">" + safeTitle + "</text>" +
                (string.IsNullOrEmpty(safePreview)
                    ? string.Empty
                    : "<text hint-style=\"body\" hint-wrap=\"true\">" + safePreview + "</text>");

            string smallBalloonBody =
                "<image placement=\"background\" src=\"" + balloonSrc + "\"/>";

            var updater = TileUpdateManager.CreateTileUpdaterForSecondaryTile(tileId);
            try
            {
                updater.EnableNotificationQueue(true);
                updater.Clear();
            }
            catch
            {
            }

            updater.Update(BuildQueuedTileNotification(
                smallBody: "<image placement=\"background\" hint-crop=\"circle\" src=\"" + imageSrc + "\"/>",
                mediumBody: mediumBody,
                wideBody: wideBody,
                branding: "none",
                displayName: null,
                queueTag: QueueTagPhoto,
                textStacking: null));

            updater.Update(BuildQueuedTileNotification(
                smallBody: smallBalloonBody,
                mediumBody: mediumBody,
                wideBody: wideBody,
                branding: "none",
                displayName: null,
                queueTag: QueueTagGlyph,
                textStacking: null));

            // Always last in the loop: Unison logo frame.
            updater.Update(BuildUnisonBrandNotification());
        }

        /// <summary>
        /// Community hub Start tile queue (max 4):
        /// (1) community logo, (2) Avisos when present, (3) another linked channel,
        /// (4) Unison logo frame always. Content frames put the community name on the plate.
        /// </summary>
        private static void PushCommunityLiveTiles(ChatItem hub)
        {
            if (hub == null || string.IsNullOrWhiteSpace(hub.JID))
            {
                return;
            }

            string tileId = BuildTileId(hub.JID);
            if (!SecondaryTile.Exists(tileId))
            {
                return;
            }

            CommunityTileState state = BuildCommunityTileState(hub, tileId);
            RememberCommunityState(state);
            PushCommunityFrames(state);
            ApplyBadge(tileId, state.BadgeUnread);
        }

        private static CommunityTileState BuildCommunityTileState(ChatItem hub, string tileId)
        {
            string communityName = ResolveCommunityPlateName(hub);
            var ordered = CommunityHubPresentation.OrderMembers(hub.GetCommunityGroups())
                          ?? new List<ChatItem>();

            // Prefer hub picture; if missing, borrow Avisos / first member avatar.
            CommunityHubPresentation.ApplyMemberAvatarFallback(hub);

            ChatItem avisos = null;
            ChatItem channel = null;
            for (int i = 0; i < ordered.Count; i++)
            {
                ChatItem member = ordered[i];
                if (member == null)
                {
                    continue;
                }

                if (member.IsCommunityAnnounce && avisos == null)
                {
                    avisos = member;
                    continue;
                }

                if (!member.IsCommunityAnnounce && channel == null)
                {
                    channel = member;
                }
            }

            // No Avisos: still show one linked group so the hub tile is not empty.
            if (avisos == null && channel == null && ordered.Count > 0)
            {
                channel = ordered[0];
            }

            var state = new CommunityTileState
            {
                HubTileId = tileId,
                HubJid = hub.JID,
                CommunityName = communityName,
                HubImageUri = ResolveLogoUri(hub.GetAvatarUrl(preferHigh: false)),
                BadgeUnread = SumCommunityUnread(hub, ordered),
                MemberJids = new List<string>()
            };

            for (int i = 0; i < ordered.Count; i++)
            {
                ChatItem member = ordered[i];
                if (member != null && !string.IsNullOrWhiteSpace(member.JID))
                {
                    state.MemberJids.Add(member.JID);
                }
            }

            if (avisos != null)
            {
                state.HasAvisos = true;
                state.AvisosJid = avisos.JID;
                state.AvisosTitle = ResolveChatTitle(avisos);
                state.AvisosPreview = FormatAuthorPreview(avisos.LastMessageAuthor, avisos.LastMessage);
                state.AvisosImageUri = ResolveLogoUri(avisos.GetAvatarUrl(preferHigh: false));
            }

            if (channel != null)
            {
                state.HasChannel = true;
                state.ChannelJid = channel.JID;
                state.ChannelTitle = ResolveChatTitle(channel);
                state.ChannelPreview = FormatAuthorPreview(channel.LastMessageAuthor, channel.LastMessage);
                state.ChannelImageUri = ResolveLogoUri(channel.GetAvatarUrl(preferHigh: false));
            }

            return state;
        }

        private static bool TryPushCommunityLiveUpdate(
            string memberJid,
            string title,
            string preview,
            Uri logo,
            int unreadCount)
        {
            CommunityTileState state;
            lock (CommunityTileSync)
            {
                if (!MemberJidToHubTileId.TryGetValue(memberJid, out string hubTileId) ||
                    string.IsNullOrWhiteSpace(hubTileId) ||
                    !CommunityStateByHubTileId.TryGetValue(hubTileId, out state) ||
                    state == null)
                {
                    return false;
                }

                // Hub JID is registered for lookup cleanup only — not a channel frame source.
                if (string.Equals(state.HubJid, memberJid, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (!SecondaryTile.Exists(hubTileId))
                {
                    return false;
                }

                state = state.Clone();
            }

            bool isAvisos = state.HasAvisos &&
                string.Equals(state.AvisosJid, memberJid, StringComparison.OrdinalIgnoreCase);

            if (isAvisos)
            {
                state.AvisosTitle = title;
                state.AvisosPreview = preview ?? string.Empty;
                state.AvisosImageUri = logo ?? DefaultLogoUri;
            }
            else
            {
                // Second slot always follows the channel that just messaged.
                state.HasChannel = true;
                state.ChannelJid = memberJid;
                state.ChannelTitle = title;
                state.ChannelPreview = preview ?? string.Empty;
                state.ChannelImageUri = logo ?? DefaultLogoUri;
            }

            if (unreadCount > state.BadgeUnread)
            {
                state.BadgeUnread = unreadCount;
            }

            RememberCommunityState(state);
            PushCommunityFrames(state);
            ApplyBadge(state.HubTileId, state.BadgeUnread);
            return true;
        }

        private static void PushCommunityFrames(CommunityTileState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.HubTileId))
            {
                return;
            }

            string communityPlate = EscapeXml(Truncate(state.CommunityName ?? UnisonBrandName, 40));
            string balloonSrc = EscapeXml(ChatBalloonUri.AbsoluteUri);

            var updater = TileUpdateManager.CreateTileUpdaterForSecondaryTile(state.HubTileId);
            try
            {
                updater.EnableNotificationQueue(true);
                updater.Clear();
            }
            catch
            {
            }

            // First in the loop: community logo / avatar.
            updater.Update(BuildIdentityPhotoNotification(
                state.HubImageUri ?? DefaultLogoUri,
                communityPlate,
                QueueTagHub));

            if (state.HasAvisos)
            {
                updater.Update(BuildCommunityFrameNotification(
                    state.AvisosTitle,
                    state.AvisosPreview,
                    state.AvisosImageUri,
                    communityPlate,
                    QueueTagAvisos));
            }

            if (state.HasChannel)
            {
                updater.Update(BuildCommunityFrameNotification(
                    state.ChannelTitle,
                    state.ChannelPreview,
                    state.ChannelImageUri,
                    communityPlate,
                    QueueTagChannel));
            }

            // Hub with no linked groups yet — keep a text plate until members load.
            if (!state.HasAvisos && !state.HasChannel)
            {
                string emptyPreview = EscapeXml(
                    LocalizedStrings.Get("Tile_ContactNoNewMessages", "No new messages"));
                string mediumBody =
                    "<text hint-style=\"caption\">" + communityPlate + "</text>" +
                    "<text hint-style=\"body\" hint-wrap=\"true\">" + emptyPreview + "</text>";
                updater.Update(BuildQueuedTileNotification(
                    smallBody: "<image placement=\"background\" src=\"" + balloonSrc + "\"/>",
                    mediumBody: mediumBody,
                    wideBody: mediumBody,
                    branding: "name",
                    displayName: communityPlate,
                    queueTag: QueueTagChannel,
                    textStacking: null));
            }

            // Always last in the loop: Unison logo frame.
            updater.Update(BuildUnisonBrandNotification());
        }

        /// <summary>
        /// Full-bleed identity photo frame (community logo or profile picture) with name on the plate.
        /// </summary>
        private static TileNotification BuildIdentityPhotoNotification(
            Uri imageUri,
            string plateName,
            string queueTag)
        {
            string imageSrc = EscapeXml((imageUri ?? DefaultLogoUri).AbsoluteUri);
            string safeName = EscapeXml(Truncate(
                string.IsNullOrWhiteSpace(plateName) ? UnisonBrandName : plateName,
                40));
            string photo =
                "<image placement=\"background\" hint-crop=\"circle\" src=\"" + imageSrc + "\"/>";

            return BuildQueuedTileNotification(
                smallBody: photo,
                mediumBody: photo,
                wideBody: photo,
                branding: "name",
                displayName: safeName,
                queueTag: queueTag,
                textStacking: "center");
        }

        private static TileNotification BuildCommunityFrameNotification(
            string groupTitle,
            string preview,
            Uri imageUri,
            string communityBrand,
            string queueTag)
        {
            _ = imageUri;
            string safeTitle = EscapeXml(Truncate(groupTitle, 40));
            string safePreview = EscapeXml(Truncate(preview, PreviewMaxChars));
            string balloonSrc = EscapeXml(ChatBalloonUri.AbsoluteUri);

            string mediumBody =
                "<text hint-style=\"caption\">" + safeTitle + "</text>" +
                (string.IsNullOrEmpty(safePreview)
                    ? string.Empty
                    : "<text hint-style=\"body\" hint-wrap=\"true\">" + safePreview + "</text>");

            // Small: speech balloon. Medium: group on top; community name on the plate (no Unison logo —
            // that is its own rotating frame).
            return BuildQueuedTileNotification(
                smallBody: "<image placement=\"background\" src=\"" + balloonSrc + "\"/>",
                mediumBody: mediumBody,
                wideBody: mediumBody,
                branding: "name",
                displayName: communityBrand,
                queueTag: queueTag,
                textStacking: null);
        }

        /// <summary>
        /// Dedicated queue frame: Unison logo + title. Always appended last so chat/profile/community
        /// content frames rotate with the brand tile in a loop.
        /// </summary>
        private static TileNotification BuildUnisonBrandNotification()
        {
            string logoSrc = EscapeXml(DefaultLogoUri.AbsoluteUri);
            string brand = EscapeXml(UnisonBrandName);
            string logoImage = "<image placement=\"background\" src=\"" + logoSrc + "\"/>";
            string mediumBody =
                logoImage +
                "<text hint-style=\"base\" hint-align=\"center\">" + brand + "</text>";
            string wideBody =
                logoImage +
                "<text hint-style=\"subtitle\" hint-align=\"center\">" + brand + "</text>";

            return BuildQueuedTileNotification(
                smallBody: logoImage,
                mediumBody: mediumBody,
                wideBody: wideBody,
                branding: "none",
                displayName: null,
                queueTag: QueueTagUnison,
                textStacking: "center");
        }

        private static void RememberCommunityState(CommunityTileState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.HubTileId))
            {
                return;
            }

            lock (CommunityTileSync)
            {
                // Drop stale member → hub links for this hub before re-registering.
                var staleMembers = new List<string>();
                foreach (var pair in MemberJidToHubTileId)
                {
                    if (string.Equals(pair.Value, state.HubTileId, StringComparison.OrdinalIgnoreCase))
                    {
                        staleMembers.Add(pair.Key);
                    }
                }

                for (int i = 0; i < staleMembers.Count; i++)
                {
                    MemberJidToHubTileId.Remove(staleMembers[i]);
                }

                if (!string.IsNullOrWhiteSpace(state.HubJid))
                {
                    MemberJidToHubTileId[state.HubJid] = state.HubTileId;
                }

                if (state.MemberJids != null)
                {
                    for (int i = 0; i < state.MemberJids.Count; i++)
                    {
                        string memberJid = state.MemberJids[i];
                        if (!string.IsNullOrWhiteSpace(memberJid))
                        {
                            MemberJidToHubTileId[memberJid] = state.HubTileId;
                        }
                    }
                }

                CommunityStateByHubTileId[state.HubTileId] = state.Clone();
            }
        }

        private static bool IsCommunityHub(ChatItem chat)
        {
            return chat != null && (chat.IsCommunity || chat.IsCommunityParent);
        }

        private static bool IsKnownCommunityHubTile(string tileId)
        {
            if (string.IsNullOrWhiteSpace(tileId))
            {
                return false;
            }

            lock (CommunityTileSync)
            {
                return CommunityStateByHubTileId.ContainsKey(tileId);
            }
        }

        private static string ResolveCommunityPlateName(ChatItem hub)
        {
            if (hub == null)
            {
                return UnisonBrandName;
            }

            if (CommunityHubPresentation.IsUsableHubLabel(hub.Name, hub.JID))
            {
                return Truncate(hub.Name.Trim(), 40);
            }

            string fallback = LocalizedStrings.Get("Chat_CommunityFallbackName", "Community");
            return Truncate(fallback, 40);
        }

        private static int SumCommunityUnread(ChatItem hub, IReadOnlyList<ChatItem> members)
        {
            int sum = 0;
            if (members != null)
            {
                for (int i = 0; i < members.Count; i++)
                {
                    ChatItem m = members[i];
                    if (m != null && m.UnreadCount > 0)
                    {
                        sum += m.UnreadCount;
                    }
                }
            }

            if (sum > 0)
            {
                return sum;
            }

            return hub != null ? Math.Max(0, hub.UnreadCount) : 0;
        }

        private static string FormatAuthorPreview(string author, string preview)
        {
            if (string.IsNullOrWhiteSpace(preview))
            {
                return string.Empty;
            }

            string body = preview.Trim();
            if (!string.IsNullOrWhiteSpace(author))
            {
                return author.Trim() + ": " + body;
            }

            return body;
        }

        private static string ResolveChatTitle(ChatItem chat)
        {
            if (chat == null)
            {
                return "Chat";
            }

            if (chat.IsCommunityAnnounce)
            {
                return LocalizedStrings.Get("Chat_CommunityAnnouncements", "Announcements");
            }

            return ResolveDisplayName(chat);
        }

        /// <summary>Cached community hub live-tile frames (logo + Avisos + one channel + Unison).</summary>
        private sealed class CommunityTileState
        {
            public string HubTileId;
            public string HubJid;
            public string CommunityName;
            public Uri HubImageUri;
            public int BadgeUnread;
            public List<string> MemberJids;

            public bool HasAvisos;
            public string AvisosJid;
            public string AvisosTitle;
            public string AvisosPreview;
            public Uri AvisosImageUri;

            public bool HasChannel;
            public string ChannelJid;
            public string ChannelTitle;
            public string ChannelPreview;
            public Uri ChannelImageUri;

            public CommunityTileState Clone()
            {
                return new CommunityTileState
                {
                    HubTileId = HubTileId,
                    HubJid = HubJid,
                    CommunityName = CommunityName,
                    HubImageUri = HubImageUri,
                    BadgeUnread = BadgeUnread,
                    MemberJids = MemberJids == null ? null : new List<string>(MemberJids),
                    HasAvisos = HasAvisos,
                    AvisosJid = AvisosJid,
                    AvisosTitle = AvisosTitle,
                    AvisosPreview = AvisosPreview,
                    AvisosImageUri = AvisosImageUri,
                    HasChannel = HasChannel,
                    ChannelJid = ChannelJid,
                    ChannelTitle = ChannelTitle,
                    ChannelPreview = ChannelPreview,
                    ChannelImageUri = ChannelImageUri
                };
            }
        }

        private static string BuildChatLaunchArgument(string chatJid) =>
            "chat=" + Uri.EscapeDataString(chatJid ?? string.Empty);

        private static string BuildProfileLaunchArgument(string chatJid) =>
            "profile=" + Uri.EscapeDataString(chatJid ?? string.Empty);

        /// <summary>
        /// Contact Profile Start tile queue (4 frames):
        /// 1) profile photo;
        /// 2) centered about/recado (or “Nenhum Recado…”);
        /// 3) centered unread line;
        /// 4) Unison logo frame (always last in the loop).
        /// </summary>
        private static void PushContactLiveTiles(
            string tileId,
            string displayName,
            Uri imageUri,
            string motto,
            int unreadCount)
        {
            string name = string.IsNullOrWhiteSpace(displayName) ? "User" : displayName.Trim();
            string safeName = EscapeXml(Truncate(name, 40));
            string glyphSrc = EscapeXml(ProfileGlyphUri.AbsoluteUri);
            Uri photoUri = imageUri ?? DefaultLogoUri;

            string mottoLine = string.IsNullOrWhiteSpace(motto)
                ? LocalizedStrings.Get("Tile_ContactNoMotto", "No status message")
                : Truncate(motto.Trim(), MottoMaxChars);
            string safeMotto = EscapeXml(mottoLine);

            string unreadLine = unreadCount > 0
                ? EscapeXml(Truncate(
                    LocalizedStrings.Format("Tile_ContactHasNewMessages", unreadCount.ToString()),
                    60))
                : EscapeXml(LocalizedStrings.Get("Tile_ContactNoNewMessages", "No new messages"));

            string mediumMotto =
                "<text hint-style=\"body\" hint-align=\"center\" hint-wrap=\"true\" hint-maxLines=\"4\">" +
                safeMotto + "</text>";
            string wideMotto =
                "<text hint-style=\"subtitle\" hint-align=\"center\" hint-wrap=\"true\" hint-maxLines=\"4\">" +
                safeMotto + "</text>";

            string mediumUnread =
                "<text hint-style=\"body\" hint-align=\"center\" hint-wrap=\"true\">" + unreadLine + "</text>";
            string wideUnread =
                "<text hint-style=\"subtitle\" hint-align=\"center\" hint-wrap=\"true\">" + unreadLine + "</text>";

            string smallGlyphBody =
                "<image placement=\"background\" src=\"" + glyphSrc + "\"/>";

            var updater = TileUpdateManager.CreateTileUpdaterForSecondaryTile(tileId);
            try
            {
                updater.EnableNotificationQueue(true);
                updater.Clear();
            }
            catch
            {
            }

            // 1) Profile photo first.
            updater.Update(BuildIdentityPhotoNotification(photoUri, name, QueueTagPhoto));

            // 2) Motto / recado.
            updater.Update(BuildQueuedTileNotification(
                smallBody: smallGlyphBody,
                mediumBody: mediumMotto,
                wideBody: wideMotto,
                branding: "name",
                displayName: safeName,
                queueTag: QueueTagMotto,
                textStacking: "center"));

            // 3) Unread line.
            updater.Update(BuildQueuedTileNotification(
                smallBody: smallGlyphBody,
                mediumBody: mediumUnread,
                wideBody: wideUnread,
                branding: "name",
                displayName: safeName,
                queueTag: QueueTagUnread,
                textStacking: "center"));

            // 4) Unison brand.
            updater.Update(BuildUnisonBrandNotification());
        }

        private static TileNotification BuildQueuedTileNotification(
            string smallBody,
            string mediumBody,
            string wideBody,
            string branding,
            string displayName = null,
            string queueTag = null,
            string textStacking = null)
        {
            string displayAttr = string.IsNullOrEmpty(displayName)
                ? string.Empty
                : " displayName=\"" + displayName + "\"";

            string stackAttr = string.IsNullOrEmpty(textStacking)
                ? string.Empty
                : " hint-textStacking=\"" + textStacking + "\"";

            string smallStackAttr = string.IsNullOrEmpty(textStacking)
                ? string.Empty
                : " hint-textStacking=\"" + textStacking + "\"";

            // Glyph-only small frames always center vertically.
            if (smallBody != null &&
                smallBody.IndexOf("<image", StringComparison.OrdinalIgnoreCase) < 0 &&
                string.IsNullOrEmpty(smallStackAttr))
            {
                smallStackAttr = " hint-textStacking=\"center\"";
            }

            string xml =
                "<tile>" +
                "<visual branding=\"" + branding + "\">" +
                "<binding template=\"TileSmall\"" + smallStackAttr + ">" + smallBody + "</binding>" +
                "<binding template=\"TileMedium\"" + displayAttr + stackAttr + ">" + mediumBody + "</binding>" +
                "<binding template=\"TileWide\"" + displayAttr + stackAttr + ">" + wideBody + "</binding>" +
                "<binding template=\"TileLarge\"" + displayAttr + stackAttr + ">" + wideBody + "</binding>" +
                "</visual>" +
                "</tile>";

            var document = new XmlDocument();
            document.LoadXml(xml);
            var notification = new TileNotification(document)
            {
                ExpirationTime = DateTimeOffset.UtcNow.AddDays(3)
            };

            if (!string.IsNullOrWhiteSpace(queueTag))
            {
                notification.Tag = queueTag.Length <= 16 ? queueTag : queueTag.Substring(0, 16);
            }

            return notification;
        }

        private static void ApplyChatTileChrome(SecondaryTile tile, Uri logo)
        {
            tile.VisualElements.Square150x150Logo = logo;
            tile.VisualElements.Square71x71Logo = logo;
            // Live queue owns chrome; keep static plate nameless to avoid double bands.
            tile.VisualElements.ShowNameOnSquare150x150Logo = false;
            tile.VisualElements.ForegroundText = ForegroundText.Light;
        }

        private static void ApplyContactTileChrome(SecondaryTile tile, Uri logo)
        {
            tile.VisualElements.Square150x150Logo = logo;
            tile.VisualElements.Square71x71Logo = logo;
            // Live queue owns chrome; keep static plate nameless to avoid double bands.
            tile.VisualElements.ShowNameOnSquare150x150Logo = false;
            tile.VisualElements.ForegroundText = ForegroundText.Light;
        }

        /// <summary>
        /// Contact about / "recado". Empty until profile status is synced onto the chat model
        /// (tile then shows <c>Tile_ContactNoMotto</c>).
        /// </summary>
        private static string ResolveContactMotto(ChatItem chat)
        {
            _ = chat;
            return string.Empty;
        }

        private static string ResolveDisplayName(ChatItem chat)
        {
            string name = chat?.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = chat?.JID ?? "Chat";
                int at = name.IndexOf('@');
                if (at > 0)
                {
                    name = name.Substring(0, at);
                }
            }

            name = name.Trim();
            if (name.Length > 40)
            {
                name = name.Substring(0, 40);
            }

            return name;
        }

        private static Uri ResolveLogoUri(string avatarUrl)
        {
            if (string.IsNullOrWhiteSpace(avatarUrl))
            {
                return DefaultLogoUri;
            }

            string url = avatarUrl.Trim();
            if (url.StartsWith("ms-appdata:///", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("ms-appx:///", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return new Uri(url);
                }
                catch
                {
                    return DefaultLogoUri;
                }
            }

            return DefaultLogoUri;
        }

        private static string Truncate(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            string trimmed = value.Trim();
            if (trimmed.Length <= maxChars)
            {
                return trimmed;
            }

            return trimmed.Substring(0, maxChars - 1) + "…";
        }

        private static string EscapeXml(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }
    }
}
