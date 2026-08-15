using System;
using System.Threading.Tasks;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Uwp.Helpers;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// WinRT Live Tile adapter for <see cref="ILiveTilesService"/>.
    /// Respects LocalSettingsConstants.LiveTilesEnabled.
    /// </summary>
    public sealed class LiveTilesService : ILiveTilesService
    {
        private static readonly Lazy<LiveTilesService> LazyInstance =
            new Lazy<LiveTilesService>(() => new LiveTilesService());

        public static LiveTilesService Instance => LazyInstance.Value;

        private readonly object _sync = new object();
        private bool _initialized;

        private LiveTilesService()
        {
        }

        private static bool IsLiveTilesSettingEnabled()
        {
            try
            {
                return LocalSettingsAccess.Current.Get<bool>(LocalSettingsConstants.LiveTilesEnabled);
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

                try
                {
                    var updater = TileUpdateManager.CreateTileUpdaterForApplication();
                    updater.EnableNotificationQueue(true);
                }
                catch
                {
                }

                _initialized = true;
            }
        }

        public void UpdateFromMessage(
            string title,
            string preview,
            string chatJid,
            int totalUnread)
        {
            if (!IsLiveTilesSettingEnabled())
            {
                return;
            }

            Initialize();

            try
            {
                string launch = BuildLaunchArgument(chatJid);
                string unreadText = totalUnread > 0
                    ? totalUnread + (totalUnread == 1 ? " unread message" : " unread messages")
                    : "New message";

                string xml =
                    "<tile>" +
                    "<visual branding=\"nameAndLogo\" arguments=\"" + EscapeXml(launch) + "\">" +
                    "<binding template=\"TileMedium\">" +
                    "<text hint-style=\"caption\">" + EscapeXml(title) + "</text>" +
                    "<text hint-style=\"body\" hint-wrap=\"true\">" + EscapeXml(preview) + "</text>" +
                    "</binding>" +
                    "<binding template=\"TileWide\">" +
                    "<text hint-style=\"caption\">" + EscapeXml(title) + "</text>" +
                    "<text hint-style=\"body\" hint-wrap=\"true\">" + EscapeXml(preview) + "</text>" +
                    "<text hint-style=\"captionSubtle\">" + EscapeXml(unreadText) + "</text>" +
                    "</binding>" +
                    "<binding template=\"TileLarge\">" +
                    "<text hint-style=\"subtitle\">" + EscapeXml(title) + "</text>" +
                    "<text hint-style=\"body\" hint-wrap=\"true\">" + EscapeXml(preview) + "</text>" +
                    "<text hint-style=\"captionSubtle\">" + EscapeXml(unreadText) + "</text>" +
                    "</binding>" +
                    "</visual>" +
                    "</tile>";

                var document = new XmlDocument();
                document.LoadXml(xml);
                var notification = new TileNotification(document)
                {
                    ExpirationTime = DateTimeOffset.UtcNow.AddDays(1)
                };

                TileUpdateManager.CreateTileUpdaterForApplication().Update(notification);
            }
            catch
            {
            }
        }

        public void Clear()
        {
            try
            {
                TileUpdateManager.CreateTileUpdaterForApplication().Clear();
            }
            catch
            {
            }
        }

        public Task OnLiveTilesConfigChangedAsync()
        {
            if (!IsLiveTilesSettingEnabled())
            {
                Clear();
            }

            return Task.CompletedTask;
        }

        private static string BuildLaunchArgument(string chatJid)
        {
            if (string.IsNullOrWhiteSpace(chatJid))
            {
                return "notification=1";
            }

            return "notification=1&chat=" + Uri.EscapeDataString(chatJid);
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
