using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Unison.Baileys.Protocol;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Socket.Groups;
using Unison.Socket.Models;
using Unison.Uwp.Client;
using Unison.Uwp.Services.Socket;

namespace Unison.Uwp.Services.Diagnostics
{
    /// <summary>
    /// Temporary chat-open probe: dumps RAM + SQLite preview + live w:g2 community flags.
    /// <see cref="ChatDetailViewModel"/> already refreshes send permissions / community link;
    /// this waits briefly and logs post-apply RAM plus a rich wire dump.
    /// </summary>
    public static class ChatOpenMetadataProbe
    {
        public static void WriteAsync(ChatItem chat)
        {
            if (chat == null || App.Services == null)
            {
                return;
            }

            IRuntimeDiagnostics diagnostics = App.Services.GetService<IRuntimeDiagnostics>();
            if (diagnostics == null)
            {
                return;
            }

            try
            {
                diagnostics.Write(
                    "chat-open",
                    "meta-ram",
                    FormatRam(chat));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatOpenMetadataProbe] meta-ram: " + ex.Message);
            }

            _ = WriteStoreAndWireAsync(chat, diagnostics);
        }

        private static async Task WriteStoreAndWireAsync(
            ChatItem chat,
            IRuntimeDiagnostics diagnostics)
        {
            try
            {
                IHistoryChatPreviewStore previewStore =
                    App.Services.GetService<IHistoryChatPreviewStore>();
                if (previewStore != null)
                {
                    HistoryChatPreview row = await previewStore
                        .TryGetAsync(chat.JID)
                        .ConfigureAwait(false);
                    diagnostics.Write(
                        "chat-open",
                        "meta-sql",
                        FormatSql(chat.JID, row));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatOpenMetadataProbe] meta-sql: " + ex.Message);
                diagnostics.Write(
                    "chat-open",
                    "meta-sql",
                    "chat=" + DiagnosticJidMask.Apply(chat.JID) + "; error=" + ex.Message);
            }

            bool isGroup = chat.IsGroup ||
                (!string.IsNullOrWhiteSpace(chat.JID) &&
                 chat.JID.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase));
            if (!isGroup)
            {
                return;
            }

            try
            {
                IWhatsAppSessionProvider sessions =
                    App.Services.GetService<IWhatsAppSessionProvider>();
                IWhatsAppSocket socket = sessions?.Socket;
                if (socket == null || !socket.IsHandshakeComplete)
                {
                    diagnostics.Write(
                        "chat-open",
                        "meta-wire",
                        "chat=" + DiagnosticJidMask.Apply(chat.JID) +
                        "; skipped=no-socket");
                    return;
                }

                BinaryNode response = await socket
                    .QueryGroupMetadataAsync(chat.JID)
                    .ConfigureAwait(false);
                diagnostics.Write(
                    "chat-open",
                    "meta-wire",
                    FormatWire(chat.JID, response));

                // ViewModel's RefreshGroupSendPermissionsAsync usually wins the race; give it a
                // moment then dump RAM so we can see whether LinkedParentJid stuck.
                await Task.Delay(1200).ConfigureAwait(false);
                diagnostics.Write(
                    "chat-open",
                    "meta-ram-after",
                    FormatRam(chat));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatOpenMetadataProbe] meta-wire: " + ex.Message);
                diagnostics.Write(
                    "chat-open",
                    "meta-wire",
                    "chat=" + DiagnosticJidMask.Apply(chat.JID) + "; error=" + ex.Message);
            }
        }

        private static string FormatRam(ChatItem chat)
        {
            return "chat=" + DiagnosticJidMask.Apply(chat.JID) +
                   "; nameLen=" + (chat.Name?.Length ?? 0) +
                   "; name=" + Sanitize(chat.Name) +
                   "; kind=" + chat.Kind +
                   "; isGroup=" + chat.IsGroup +
                   "; personal=" + chat.IsPersonal +
                   "; announceOnly=" + chat.IsAnnounceOnly +
                   "; isCommunityAnnounce=" + chat.IsCommunityAnnounce +
                   "; isCommunityParent=" + chat.IsCommunityParent +
                   "; linkedParent=" + DiagnosticJidMask.Apply(chat.LinkedParentJid) +
                   "; myRole=" + chat.MyGroupRole +
                   "; locked=" + chat.IsGroupLockedForMessages +
                   "; members=" + chat.GroupMemberCount +
                   "; unread=" + chat.UnreadCount +
                   "; pinned=" + chat.IsChatPinned +
                   "; muted=" + chat.IsMutedLocally +
                   "; hasAvatar=" + !string.IsNullOrWhiteSpace(chat.AvatarUrl) +
                   "; hasAvatarHigh=" + !string.IsNullOrWhiteSpace(chat.AvatarHighUrl) +
                   "; tipKind=" + chat.LastPreview.Kind +
                   "; tipLen=" + (chat.LastMessage?.Length ?? 0);
        }

        private static string FormatSql(string jid, HistoryChatPreview row)
        {
            if (row == null)
            {
                return "chat=" + DiagnosticJidMask.Apply(jid) + "; row=missing";
            }

            return "chat=" + DiagnosticJidMask.Apply(jid) +
                   "; rowJid=" + DiagnosticJidMask.Apply(row.Jid) +
                   "; lid=" + DiagnosticJidMask.Apply(row.LidJid) +
                   "; pn=" + DiagnosticJidMask.Apply(row.PnJid) +
                   "; name=" + Sanitize(row.Name) +
                   "; isGroup=" + row.IsGroup +
                   "; isCommunityAnnounce=" + row.IsCommunityAnnounce +
                   "; isCommunityParent=" + row.IsCommunityParent +
                   "; linkedParent=" + DiagnosticJidMask.Apply(row.LinkedParentJid) +
                   "; status=" + row.Status +
                   "; unread=" + row.UnreadCount +
                   "; tipKind=" + row.LastMessageKind +
                   "; tipId=" + (row.LastMessageId ?? "-") +
                   "; deleted=" + (row.Status == ChatStatus.Deleted) +
                   "; syncType=" + (row.SyncType ?? "-");
        }

        private static string FormatWire(string jid, BinaryNode response)
        {
            if (response == null)
            {
                return "chat=" + DiagnosticJidMask.Apply(jid) + "; response=null";
            }

            BinaryNode group = response.Tag == "group" ? response : response.GetChild("group");
            GroupMetadata parsed = null;
            try
            {
                parsed = GroupMetadataParser.Parse(response);
            }
            catch (Exception ex)
            {
                return "chat=" + DiagnosticJidMask.Apply(jid) +
                       "; parseError=" + ex.Message +
                       "; hasGroupNode=" + (group != null);
            }

            BinaryNode defaultSub = group?.GetChild("default_sub_group");
            BinaryNode linkedParent = group?.GetChild("linked_parent");
            BinaryNode parent = group?.GetChild("parent");
            BinaryNode defaultSubCommunity = group?.GetChild("default_sub_community");

            string defaultSubId = defaultSub == null
                ? null
                : (defaultSub.GetAttribute("jid") ?? defaultSub.GetAttribute("id"));
            string linkedParentJid = linkedParent == null
                ? null
                : (linkedParent.GetAttribute("jid") ?? linkedParent.GetAttribute("id"));
            string parentJid = parent?.GetAttribute("jid") ?? parent?.GetAttribute("id");
            string defaultSubCommunityJid = defaultSubCommunity == null
                ? null
                : (defaultSubCommunity.GetAttribute("jid") ?? defaultSubCommunity.GetAttribute("id"));

            return "chat=" + DiagnosticJidMask.Apply(jid) +
                   "; subject=" + Sanitize(parsed?.Subject) +
                   "; announceOnly=" + (parsed != null && parsed.Announce) +
                   "; isCommunity=" + (parsed != null && parsed.IsCommunity) +
                   "; isCommunityAnnounce=" + (parsed != null && parsed.IsCommunityAnnounce) +
                   "; linkedParent=" + DiagnosticJidMask.Apply(parsed?.LinkedParent ?? linkedParentJid) +
                   "; hasLinkedParentChild=" + (linkedParent != null) +
                   "; linkedParentAttrs=" + FormatAttrs(linkedParent) +
                   "; defaultSubGroup=" + DiagnosticJidMask.Apply(defaultSubId) +
                   "; parent=" + DiagnosticJidMask.Apply(parentJid) +
                   "; defaultSubCommunity=" + DiagnosticJidMask.Apply(defaultSubCommunityJid) +
                   "; size=" + (parsed?.Size ?? 0) +
                   "; hasAnnouncementChild=" + (group?.GetChild("announcement") != null) +
                   "; hasParentChild=" + (parent != null) +
                   "; hasDefaultSubGroupChild=" + (defaultSub != null);
        }

        private static string FormatAttrs(BinaryNode node)
        {
            if (node?.Attrs == null || node.Attrs.Count == 0)
            {
                return "-";
            }

            var parts = new System.Collections.Generic.List<string>();
            foreach (var pair in node.Attrs)
            {
                string key = pair.Key ?? "?";
                string value = pair.Value ?? string.Empty;
                if (string.Equals(key, "jid", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "id", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "parent", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "linked_parent", StringComparison.OrdinalIgnoreCase))
                {
                    value = DiagnosticJidMask.Apply(value) ?? "-";
                }
                else if (value.Length > 24)
                {
                    value = value.Substring(0, 24) + "…";
                }

                parts.Add(key + "=" + value);
            }

            return parts.Count == 0 ? "-" : string.Join(",", parts);
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "-";
            }

            string trimmed = value.Trim();
            if (trimmed.Length > 80)
            {
                trimmed = trimmed.Substring(0, 80) + "…";
            }

            return trimmed
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace(';', ',');
        }
    }
}
