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

        /// <summary>Segoe MDL2 Message (E8BD) — adaptive text, not a PNG plate.</summary>
        private const string Mdl2MessageGlyphXml = "&#xE8BD;";

        /// <summary>Segoe MDL2 Contact2 (E77B) — adaptive text, not a PNG plate.</summary>
        private const string Mdl2ContactGlyphXml = "&#xE77B;";

        private const string TileIdPrefix = "c_";
        private const string ContactTileIdPrefix = "p_";
        private const int PreviewMaxChars = 120;

        /// <summary>
        /// Distinct tags so <see cref="TileUpdater.EnableNotificationQueue"/> keeps every frame.
        /// Untagged updates replace each other and the small tile never rotates.
        /// </summary>
        private const string QueueTagPhoto = "photo";
        private const string QueueTagGlyph = "glyph";
        private const string QueueTagUnread = "unread";

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
                if (SecondaryTile.Exists(chatTileId))
                {
                    PushLiveTile(chatTileId, safeTitle, preview, logo);
                    ApplyBadge(chatTileId, unreadCount);
                }

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

            string preview = chat.LastMessage;
            if (!string.IsNullOrWhiteSpace(chat.LastMessageAuthor) &&
                !string.IsNullOrWhiteSpace(preview))
            {
                preview = chat.LastMessageAuthor.Trim() + ": " + preview.Trim();
            }

            string tileId = BuildTileId(chat.JID);
            if (!SecondaryTile.Exists(tileId))
            {
                return;
            }

            PushLiveTile(
                tileId,
                ResolveDisplayName(chat),
                preview,
                ResolveLogoUri(chat.GetAvatarUrl(preferHigh: false)));
            // Badge after content so the queue Clear cannot leave a stale empty counter frame.
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
        /// Chat Start tile (Imgur-style): medium title + preview centered with MDL2 message glyph;
        /// TileSmall queue rotates photo ↔ MDL2 glyph. Badge carries the unread count.
        /// </summary>
        private static void PushLiveTile(
            string tileId,
            string title,
            string preview,
            Uri imageUri)
        {
            string safeTitle = EscapeXml(Truncate(title, 40));
            string safePreview = EscapeXml(Truncate(preview, PreviewMaxChars));
            string imageSrc = EscapeXml(imageUri?.AbsoluteUri ?? DefaultLogoUri.AbsoluteUri);

            // Medium / wide: vertically centered like Imgur text frames — glyph + name + preview.
            string mediumBody =
                "<text hint-style=\"title\" hint-align=\"center\">" + Mdl2MessageGlyphXml + "</text>" +
                "<text hint-style=\"base\" hint-align=\"center\" hint-maxLines=\"2\">" + safeTitle + "</text>" +
                (string.IsNullOrEmpty(safePreview)
                    ? string.Empty
                    : "<text hint-style=\"captionSubtle\" hint-align=\"center\" hint-wrap=\"true\" hint-maxLines=\"2\">" +
                      safePreview + "</text>");

            string wideBody =
                "<text hint-style=\"header\" hint-align=\"center\">" + Mdl2MessageGlyphXml + "</text>" +
                "<text hint-style=\"subtitle\" hint-align=\"center\" hint-maxLines=\"2\">" + safeTitle + "</text>" +
                (string.IsNullOrEmpty(safePreview)
                    ? string.Empty
                    : "<text hint-style=\"bodySubtle\" hint-align=\"center\" hint-wrap=\"true\">" +
                      safePreview + "</text>");

            string smallGlyphBody =
                "<text hint-style=\"title\" hint-align=\"center\">" + Mdl2MessageGlyphXml + "</text>";

            var updater = TileUpdateManager.CreateTileUpdaterForSecondaryTile(tileId);
            try
            {
                updater.EnableNotificationQueue(true);
                updater.Clear();
            }
            catch
            {
            }

            // Distinct Tag values — without them the second Update replaces the first and small never cycles.
            // No displayName strip — title already sits centered in the adaptive body (Imgur-style).
            updater.Update(BuildQueuedTileNotification(
                smallBody: "<image placement=\"background\" hint-crop=\"circle\" src=\"" + imageSrc + "\"/>",
                mediumBody: mediumBody,
                wideBody: wideBody,
                branding: "none",
                displayName: null,
                queueTag: QueueTagPhoto,
                textStacking: "center"));

            updater.Update(BuildQueuedTileNotification(
                smallBody: smallGlyphBody,
                mediumBody: mediumBody,
                wideBody: wideBody,
                branding: "none",
                displayName: null,
                queueTag: QueueTagGlyph,
                textStacking: "center"));
        }

        private static string BuildChatLaunchArgument(string chatJid) =>
            "chat=" + Uri.EscapeDataString(chatJid ?? string.Empty);

        private static string BuildProfileLaunchArgument(string chatJid) =>
            "profile=" + Uri.EscapeDataString(chatJid ?? string.Empty);

        /// <summary>
        /// Contact Profile Start tile queue (2 frames, Imgur-style):
        /// 1) round peek photo + MDL2 contact glyph beside the name at the bottom;
        /// 2) vertically centered “Nenhuma Nova Mensagem” / “N Novas Mensagens”.
        /// TileSmall rotates photo ↔ MDL2 contact glyph. Badge mirrors unread on both sizes.
        /// </summary>
        private static void PushContactLiveTiles(
            string tileId,
            string displayName,
            Uri imageUri,
            string motto,
            int unreadCount)
        {
            _ = motto;
            string name = string.IsNullOrWhiteSpace(displayName) ? "User" : displayName.Trim();
            string safeName = EscapeXml(Truncate(name, 40));
            string imageSrc = EscapeXml(imageUri?.AbsoluteUri ?? DefaultLogoUri.AbsoluteUri);

            string unreadLine = unreadCount > 0
                ? EscapeXml(Truncate(
                    LocalizedStrings.Format("Tile_ContactHasNewMessages", unreadCount.ToString()),
                    60))
                : EscapeXml(LocalizedStrings.Get("Tile_ContactNoNewMessages", "No new messages"));

            // Frame 1 — round photo peeks; name + MDL2 Contact2 sit under it.
            string mediumPhotoName =
                "<image placement=\"peek\" hint-crop=\"circle\" src=\"" + imageSrc + "\"/>" +
                "<group>" +
                "<subgroup hint-weight=\"1\">" +
                "<text hint-style=\"title\" hint-align=\"center\">" + Mdl2ContactGlyphXml + "</text>" +
                "</subgroup>" +
                "<subgroup hint-weight=\"3\">" +
                "<text hint-style=\"base\" hint-maxLines=\"2\" hint-align=\"left\">" + safeName + "</text>" +
                "</subgroup>" +
                "</group>";

            string widePhotoName =
                "<image placement=\"peek\" hint-crop=\"circle\" src=\"" + imageSrc + "\"/>" +
                "<group>" +
                "<subgroup hint-weight=\"1\">" +
                "<text hint-style=\"header\" hint-align=\"center\">" + Mdl2ContactGlyphXml + "</text>" +
                "</subgroup>" +
                "<subgroup hint-weight=\"3\">" +
                "<text hint-style=\"subtitle\" hint-maxLines=\"2\">" + safeName + "</text>" +
                "</subgroup>" +
                "</group>";

            // Frame 2 — unread status centered vertically (Imgur TextStacking.Center).
            string mediumUnread =
                "<text hint-style=\"body\" hint-align=\"center\" hint-wrap=\"true\">" + unreadLine + "</text>";
            string wideUnread =
                "<text hint-style=\"subtitle\" hint-align=\"center\" hint-wrap=\"true\">" + unreadLine + "</text>";

            string smallGlyphBody =
                "<text hint-style=\"title\" hint-align=\"center\">" + Mdl2ContactGlyphXml + "</text>";

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
                mediumBody: mediumPhotoName,
                wideBody: widePhotoName,
                branding: "none",
                displayName: null,
                queueTag: QueueTagPhoto,
                textStacking: null));

            updater.Update(BuildQueuedTileNotification(
                smallBody: smallGlyphBody,
                mediumBody: mediumUnread,
                wideBody: wideUnread,
                branding: "none",
                displayName: null,
                queueTag: QueueTagUnread,
                textStacking: "center"));
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
            // Live adaptive frames own the title (centered); avoid a second name band under the plate.
            tile.VisualElements.ShowNameOnSquare150x150Logo = false;
            tile.VisualElements.ForegroundText = ForegroundText.Light;
        }

        private static void ApplyContactTileChrome(SecondaryTile tile, Uri logo)
        {
            tile.VisualElements.Square150x150Logo = logo;
            tile.VisualElements.Square71x71Logo = logo;
            // Name + motto live on the adaptive queue, not under the static logo.
            tile.VisualElements.ShowNameOnSquare150x150Logo = false;
            tile.VisualElements.ForegroundText = ForegroundText.Light;
        }

        /// <summary>
        /// Contact about / "recado". Empty until profile status is synced onto the chat model.
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
