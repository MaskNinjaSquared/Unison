using System;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Windows.UI.StartScreen;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// Copies the app tile's badge and live-tile payload onto the pinned Start mark,
    /// so that tile gets the same notifications as the primary one.
    /// </summary>
    internal static class StartTileMirror
    {
        public const string TileId = "Unison.StartMark";

        public static void UpdateTile(XmlDocument document, DateTimeOffset? expiration)
        {
            try
            {
                if (document == null || !SecondaryTile.Exists(TileId))
                {
                    return;
                }

                var notification = new TileNotification(document)
                {
                    ExpirationTime = expiration
                };
                TileUpdateManager.CreateTileUpdaterForSecondaryTile(TileId).Update(notification);
            }
            catch
            {
            }
        }

        public static void ClearTile()
        {
            try
            {
                if (!SecondaryTile.Exists(TileId))
                {
                    return;
                }

                TileUpdateManager.CreateTileUpdaterForSecondaryTile(TileId).Clear();
            }
            catch
            {
            }
        }

        public static void UpdateBadge(int totalUnread, bool notificationsEnabled)
        {
            try
            {
                if (!SecondaryTile.Exists(TileId))
                {
                    return;
                }

                var updater = BadgeUpdateManager.CreateBadgeUpdaterForSecondaryTile(TileId);
                if (totalUnread <= 0 || !notificationsEnabled)
                {
                    updater.Clear();
                    return;
                }

                int badgeValue = totalUnread;
                if (SystemInfoProvider.DetectIsMobile() && totalUnread > 99)
                {
                    badgeValue = 100;
                }

                XmlDocument badgeXml = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);
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

        public static void ClearBadge()
        {
            try
            {
                if (!SecondaryTile.Exists(TileId))
                {
                    return;
                }

                BadgeUpdateManager.CreateBadgeUpdaterForSecondaryTile(TileId).Clear();
            }
            catch
            {
            }
        }
    }
}
