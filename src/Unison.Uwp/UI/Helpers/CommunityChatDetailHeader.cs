using System;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Helpers
{
    /// <summary>
    /// Toggles chat-detail title chrome between normal and community-linked layouts.
    /// </summary>
    internal static class CommunityChatDetailHeader
    {
        public static bool TryShow(
            ChatItem chat,
            IStringResources strings,
            FrameworkElement titleStatusPanel,
            FrameworkElement communityTitlePanel,
            TextBlock communityTitleText,
            TextBlock communityGroupSubtitleText)
        {
            if (chat == null ||
                string.IsNullOrWhiteSpace(chat.LinkedParentJid) ||
                titleStatusPanel == null ||
                communityTitlePanel == null ||
                communityTitleText == null ||
                communityGroupSubtitleText == null)
            {
                Hide(titleStatusPanel, communityTitlePanel);
                return false;
            }

            IChatStateStore chats = null;
            IJidResolver jids = null;
            IContactService contacts = null;
            try
            {
                chats = App.Services?.GetService<IChatStateStore>();
                jids = App.Services?.GetService<IJidResolver>();
                contacts = App.Services?.GetService<IContactService>();
            }
            catch
            {
            }

            string parentKey = jids != null
                ? jids.GetCanonicalJid(chat.LinkedParentJid)
                : JidHelper.Normalize(chat.LinkedParentJid);

            string communityTitle;
            string groupSubtitle;
            if (!CommunityHubPresentation.TryGetDetailHeader(
                    chat,
                    jid => FindParent(chats, jids, parentKey, jid),
                    strings,
                    out communityTitle,
                    out groupSubtitle,
                    jid => contacts?.ResolveDisplayName(jid, "community-header")))
            {
                Hide(titleStatusPanel, communityTitlePanel);
                return false;
            }

            communityTitleText.Text = communityTitle ?? string.Empty;
            communityGroupSubtitleText.Text = groupSubtitle ?? string.Empty;
            titleStatusPanel.Visibility = Visibility.Collapsed;
            communityTitlePanel.Visibility = Visibility.Visible;
            return true;
        }

        public static void Hide(
            FrameworkElement titleStatusPanel,
            FrameworkElement communityTitlePanel)
        {
            if (titleStatusPanel != null)
            {
                titleStatusPanel.Visibility = Visibility.Visible;
            }

            if (communityTitlePanel != null)
            {
                communityTitlePanel.Visibility = Visibility.Collapsed;
            }
        }

        private static ChatItem FindParent(
            IChatStateStore chats,
            IJidResolver jids,
            string parentKey,
            string requestedJid)
        {
            if (chats?.Chats == null)
            {
                return null;
            }

            string key = parentKey;
            if (string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(requestedJid))
            {
                key = jids != null
                    ? jids.GetCanonicalJid(requestedJid)
                    : JidHelper.Normalize(requestedJid);
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            foreach (ChatItem c in chats.Chats)
            {
                if (c == null || string.IsNullOrWhiteSpace(c.JID))
                {
                    continue;
                }

                string cKey = jids != null ? jids.GetCanonicalJid(c.JID) : JidHelper.Normalize(c.JID);
                if (string.Equals(cKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    return c;
                }
            }

            return null;
        }
    }
}
