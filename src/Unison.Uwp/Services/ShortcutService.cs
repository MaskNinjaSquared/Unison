using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Windows.UI.StartScreen;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// WinRT SecondaryTile adapter for <see cref="IShortcutService"/>.
    /// Live content uses adaptive peek image with circular crop when avatar is local.
    /// </summary>
    public sealed class ShortcutService : IShortcutService
    {
        private static readonly Uri DefaultLogoUri =
            new Uri("ms-appx:///Assets/Square150x150Logo.png");

        private const string TileIdPrefix = "c_";
        private const int PreviewMaxChars = 120;

        public async Task<bool> PinChatAsync(ChatItem chat)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return false;
            }

            string tileId = BuildTileId(chat.JID);
            string displayName = ResolveDisplayName(chat);
            string arguments = BuildLaunchArgument(chat.JID);
            Uri logo = ResolveLogoUri(chat.AvatarUrl);

            try
            {
                if (SecondaryTile.Exists(tileId))
                {
                    var existing = new SecondaryTile(tileId)
                    {
                        DisplayName = displayName,
                        Arguments = arguments
                    };
                    existing.VisualElements.Square150x150Logo = logo;
                    existing.VisualElements.ShowNameOnSquare150x150Logo = true;
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
                tile.VisualElements.ShowNameOnSquare150x150Logo = true;
                tile.VisualElements.ForegroundText = ForegroundText.Light;

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

        public void UpdateChatUnread(string chatJid, int unreadCount)
        {
            if (string.IsNullOrWhiteSpace(chatJid))
            {
                return;
            }

            string tileId = BuildTileId(chatJid);
            try
            {
                if (!SecondaryTile.Exists(tileId))
                {
                    return;
                }

                ApplyBadge(tileId, unreadCount);
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

            string tileId = BuildTileId(chatJid);
            try
            {
                if (!SecondaryTile.Exists(tileId))
                {
                    return;
                }

                ApplyBadge(tileId, unreadCount);
                PushLiveTile(
                    tileId,
                    string.IsNullOrWhiteSpace(title) ? "Chat" : title.Trim(),
                    preview,
                    ResolveLogoUri(avatarUrl),
                    unreadCount);
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

            var byTileId = new Dictionary<string, ChatItem>(StringComparer.OrdinalIgnoreCase);
            if (chats != null)
            {
                foreach (var chat in chats)
                {
                    if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
                    {
                        continue;
                    }

                    byTileId[BuildTileId(chat.JID)] = chat;
                }
            }

            foreach (var tile in tiles)
            {
                if (tile == null || string.IsNullOrWhiteSpace(tile.TileId))
                {
                    continue;
                }

                if (!tile.TileId.StartsWith(TileIdPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (byTileId.TryGetValue(tile.TileId, out var chat))
                {
                    PushPinnedPresentation(chat);
                }
                else
                {
                    try
                    {
                        BadgeUpdateManager.CreateBadgeUpdaterForSecondaryTile(tile.TileId).Clear();
                    }
                    catch
                    {
                    }
                }
            }
        }

        /// <summary>Stable SecondaryTile id (≤64 chars, alphanumeric + underscore).</summary>
        public static string BuildTileId(string chatJid)
        {
            string norm = (chatJid ?? string.Empty).Trim().ToLowerInvariant();
            using (var sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(norm));
                var sb = new StringBuilder(TileIdPrefix, TileIdPrefix.Length + 32);
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

            UpdatePinnedChatTile(
                chat.JID,
                chat.UnreadCount,
                ResolveDisplayName(chat),
                preview,
                chat.AvatarUrl);
        }

        private static void ApplyBadge(string tileId, int unreadCount)
        {
            var updater = BadgeUpdateManager.CreateBadgeUpdaterForSecondaryTile(tileId);
            if (unreadCount <= 0)
            {
                updater.Clear();
                return;
            }

            int badgeValue = Math.Min(99, unreadCount);
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
        /// Adaptive secondary-tile live content with circular peek avatar (Win10 1511+).
        /// </summary>
        private static void PushLiveTile(
            string tileId,
            string title,
            string preview,
            Uri imageUri,
            int unreadCount)
        {
            string safeTitle = EscapeXml(Truncate(title, 40));
            string safePreview = EscapeXml(Truncate(preview, PreviewMaxChars));
            string imageSrc = EscapeXml(imageUri?.AbsoluteUri ?? DefaultLogoUri.AbsoluteUri);
            bool hasPeekImage = imageUri != null;

            string unreadLine = unreadCount > 0
                ? EscapeXml(unreadCount == 1 ? "1 unread" : unreadCount + " unread")
                : string.Empty;

            // Peek + hint-crop=circle animates the avatar in from the top.
            string peek =
                hasPeekImage
                    ? "<image placement=\"peek\" hint-crop=\"circle\" src=\"" + imageSrc + "\"/>"
                    : string.Empty;

            string mediumBody = string.IsNullOrEmpty(safePreview)
                ? "<text hint-style=\"body\">" + safeTitle + "</text>"
                : "<text hint-style=\"caption\">" + safeTitle + "</text>" +
                  "<text hint-style=\"body\" hint-wrap=\"true\">" + safePreview + "</text>";

            if (!string.IsNullOrEmpty(unreadLine))
            {
                mediumBody += "<text hint-style=\"captionSubtle\">" + unreadLine + "</text>";
            }

            string wideBody =
                "<text hint-style=\"subtitle\">" + safeTitle + "</text>" +
                (string.IsNullOrEmpty(safePreview)
                    ? string.Empty
                    : "<text hint-style=\"body\" hint-wrap=\"true\">" + safePreview + "</text>") +
                (string.IsNullOrEmpty(unreadLine)
                    ? string.Empty
                    : "<text hint-style=\"captionSubtle\">" + unreadLine + "</text>");

            string largeBody = wideBody;

            string xml =
                "<tile>" +
                "<visual branding=\"name\">" +
                "<binding template=\"TileMedium\" displayName=\"" + safeTitle + "\">" +
                peek + mediumBody +
                "</binding>" +
                "<binding template=\"TileWide\" displayName=\"" + safeTitle + "\">" +
                peek + wideBody +
                "</binding>" +
                "<binding template=\"TileLarge\" displayName=\"" + safeTitle + "\">" +
                peek + largeBody +
                "</binding>" +
                "</visual>" +
                "</tile>";

            var document = new XmlDocument();
            document.LoadXml(xml);
            var notification = new TileNotification(document)
            {
                ExpirationTime = DateTimeOffset.UtcNow.AddDays(3)
            };

            var updater = TileUpdateManager.CreateTileUpdaterForSecondaryTile(tileId);
            try
            {
                updater.EnableNotificationQueue(true);
            }
            catch
            {
            }

            updater.Update(notification);
        }

        private static string BuildLaunchArgument(string chatJid)
        {
            return "notification=1&chat=" + Uri.EscapeDataString(chatJid ?? string.Empty);
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
