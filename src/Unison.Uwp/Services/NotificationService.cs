using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Unison.Background;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Uwp.Helpers;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// Native Windows 10 toast and badge integration.
    /// Respects LocalSettingsConstants.NotificationsEnabled.
    /// Live Tile updates go through <see cref="ILiveTilesService"/>.
    /// </summary>
    public sealed class NotificationService : INotificationService
    {
        private static readonly Lazy<NotificationService> LazyInstance =
            new Lazy<NotificationService>(() => new NotificationService());

        public static NotificationService Instance => LazyInstance.Value;

        private readonly object _sync = new object();
        private bool _initialized;
        private ILiveTilesService _liveTiles;
        private IShortcutService _shortcuts;

        private NotificationService()
        {
        }

        public void AttachLiveTiles(ILiveTilesService liveTiles)
        {
            _liveTiles = liveTiles ?? LiveTilesService.Instance;
        }

        public void AttachShortcuts(IShortcutService shortcuts)
        {
            _shortcuts = shortcuts;
        }

        private ILiveTilesService LiveTiles =>
            _liveTiles ?? LiveTilesService.Instance;

        private IShortcutService Shortcuts => _shortcuts;

        private static bool IsNotificationsSettingEnabled()
        {
            try
            {
                return LocalSettingsAccess.Current.Get<bool>(LocalSettingsConstants.NotificationsEnabled);
            }
            catch
            {
                return true;
            }
        }

        public void Initialize()
        {
            lock (_sync)
            {
                if (_initialized)
                {
                    return;
                }

                LiveTiles.Initialize();
                _initialized = true;
            }
        }

        public void NotifyIncomingMessage(
            string chatJid,
            string chatName,
            string senderName,
            string preview,
            bool isGroup,
            bool isMuted,
            bool suppressToast,
            int totalUnread,
            string avatarUrl = null,
            int chatUnread = -1)
        {
            Initialize();

            BackgroundNotificationContent content =
                BackgroundPreviewResolver.ResolveRealMessage(
                    chatJid,
                    chatName,
                    senderName,
                    preview,
                    isGroup,
                    avatarUrl);

            bool notificationsEnabled = IsNotificationsSettingEnabled();
            bool toastShown = false;
            string toastError = string.Empty;
            if (notificationsEnabled && !isMuted && !suppressToast)
            {
                toastShown = BackgroundToastPresenter.ShowRealMessage(
                    content,
                    out toastError);
            }

            RuntimeDiagnosticsService.Instance.Write(
                "notifications",
                "real-message-toast",
                "shown=" + toastShown +
                "; enabled=" + notificationsEnabled +
                "; muted=" + isMuted +
                "; suppressed=" + suppressToast +
                "; group=" + isGroup +
                "; hasAvatar=" + (!string.IsNullOrWhiteSpace(avatarUrl)) +
                "; previewLength=" + content.Preview.Length +
                "; error=" + toastError);

            LiveTiles.UpdateFromMessage(
                content.Title,
                content.Preview,
                chatJid,
                totalUnread);

            UpdateBadge(notificationsEnabled ? totalUnread : 0);

            if (chatUnread >= 0 && Shortcuts != null)
            {
                Shortcuts.UpdatePinnedChatTile(
                    chatJid,
                    chatUnread,
                    content.Title,
                    content.Preview,
                    avatarUrl);
            }
        }

        public void ShowToast(string title, string body)
        {
            Initialize();
            if (!IsNotificationsSettingEnabled())
            {
                return;
            }

            try
            {
                string safeTitle = EscapeXml(title ?? string.Empty);
                string safeBody = EscapeXml(body ?? string.Empty);
                string xml =
                    "<toast><visual><binding template=\"ToastGeneric\">" +
                    "<text>" + safeTitle + "</text>" +
                    "<text>" + safeBody + "</text>" +
                    "</binding></visual></toast>";

                var doc = new XmlDocument();
                doc.LoadXml(xml);
                ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(doc));
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.Write(
                    "notifications",
                    "show-toast-failed",
                    ex.Message);
            }
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

        public void RefreshUnreadState(IEnumerable<ChatItem> chats)
        {
            int totalUnread = 0;
            if (chats != null)
            {
                totalUnread = chats
                    .Where(c => c != null)
                    .Sum(c => Math.Max(0, c.UnreadCount));
            }

            UpdateBadge(IsNotificationsSettingEnabled() ? totalUnread : 0);

            if (Shortcuts != null)
            {
                _ = Shortcuts.RefreshPinnedUnreadAsync(chats);
            }
        }

        public void UpdateBadge(int totalUnread)
        {
            try
            {
                var updater = BadgeUpdateManager.CreateBadgeUpdaterForApplication();
                if (totalUnread <= 0 || !IsNotificationsSettingEnabled())
                {
                    updater.Clear();
                    return;
                }

                int badgeValue = Math.Min(99, totalUnread);
                var badgeXml = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);
                var badgeElement = badgeXml.SelectSingleNode("/badge") as XmlElement;
                if (badgeElement == null)
                {
                    return;
                }

                badgeElement.SetAttribute("value", badgeValue.ToString());
                updater.Update(new BadgeNotification(badgeXml));
            }
            catch
            {
            }
        }

        public void ClearAll()
        {
            try { BadgeUpdateManager.CreateBadgeUpdaterForApplication().Clear(); } catch { }
            LiveTiles.Clear();
            try { ToastNotificationManager.History.Clear(); } catch { }
        }

        public void OnNotificationsConfigChanged()
        {
            if (!IsNotificationsSettingEnabled())
            {
                try { BadgeUpdateManager.CreateBadgeUpdaterForApplication().Clear(); } catch { }
                try { ToastNotificationManager.History.Clear(); } catch { }
            }
        }
    }
}
