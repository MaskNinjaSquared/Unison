using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Uwp.Services.Diagnostics
{
    /// <summary>
    /// Chat-open probe: RAM + SQLite preview only.
    /// Do not QueryGroupMetadata here — that raced the real open refresh and, for
    /// 500+ member groups on Mobile, starved Yield / LoadMessages on the dispatcher.
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

            _ = WriteStoreAsync(chat, diagnostics);
        }

        private static async Task WriteStoreAsync(
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
