// =============================================================================
// ChatDetailViewModel.Reactions
//
// Optimistic from-me reaction on a bubble; rolls back when TryReactAsync fails.
// Same emoji as the current from-me chip removes it (empty wire emoji).
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Helpers;
using Unison.Core.Mappers;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    public partial class ChatDetailViewModel
    {
        private readonly ReactionMapper _reactionMapper = new ReactionMapper();

        /// <summary>
        /// Ensures the emoji catalog is warm for the reaction full-picker flyout.
        /// Does not require the composer sticker tray to be open.
        /// </summary>
        public Task EnsureEmojiCatalogForReactionPickerAsync()
        {
            return EnsureEmojiCategoriesAsync(force: false, requireStickerBox: false);
        }

        /// <summary>
        /// From-me emoji on <paramref name="message"/>, if any (for quick-bar selection mark).
        /// </summary>
        public string GetMyReactionEmoji(ChatMessage message)
        {
            if (message == null)
            {
                return null;
            }

            string selfJid = _jids.Self?.Id
                ?? _jids.Self?.Phone
                ?? "self";
            MessageReaction mine = OutgoingReactionOptimistic.FindFromMe(message, selfJid);
            return mine?.Emoji;
        }

        /// <summary>
        /// Applies <paramref name="emoji"/> locally, then asks the message façade.
        /// Clicking the emoji already marked as mine removes it immediately.
        /// On failure, undoes the local change.
        /// </summary>
        public async Task ReactToMessageAsync(ChatMessage message, string emoji)
        {
            if (message == null || string.IsNullOrWhiteSpace(emoji) || string.IsNullOrWhiteSpace(message.Id))
            {
                return;
            }

            string chatJid = ActiveChat?.JID
                ?? message.RemoteJid
                ?? string.Empty;
            string selfJid = _jids.Self?.Id
                ?? _jids.Self?.Phone
                ?? "self";
            string trimmed = emoji.Trim();

            if (OutgoingReactionOptimistic.IsFromMeEmoji(message, trimmed, selfJid))
            {
                await RemoveMyReactionAsync(message, chatJid, selfJid).ConfigureAwait(false);
                return;
            }

            // At most one from-me reaction: swapping replaces the previous chip in place.
            PendingReaction previous = OutgoingReactionOptimistic.SnapshotFromMe(
                message,
                chatJid,
                selfJid);
            PendingReaction pending = OutgoingReactionOptimistic.BuildFromMe(
                message.Id,
                chatJid,
                trimmed,
                selfJid);

            if (!OutgoingReactionOptimistic.Apply(_reactionMapper, message, pending))
            {
                return;
            }

            bool kept = false;
            try
            {
                kept = await _messageService.TryReactAsync(chatJid, message, trimmed)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChatDetailViewModel] TryReactAsync failed: " + ex.Message);
                kept = false;
            }

            if (!kept)
            {
                if (previous != null)
                {
                    OutgoingReactionOptimistic.Apply(_reactionMapper, message, previous);
                }
                else
                {
                    OutgoingReactionOptimistic.Undo(_reactionMapper, message, pending);
                }
            }
        }

        private async Task RemoveMyReactionAsync(
            ChatMessage message,
            string chatJid,
            string selfJid)
        {
            PendingReaction snapshot = OutgoingReactionOptimistic.SnapshotFromMe(
                message,
                chatJid,
                selfJid);
            if (snapshot == null)
            {
                return;
            }

            PendingReaction removal = OutgoingReactionOptimistic.BuildRemoval(snapshot);
            if (!OutgoingReactionOptimistic.Apply(_reactionMapper, message, removal))
            {
                return;
            }

            bool kept = false;
            try
            {
                // Empty text = WhatsApp reaction removal.
                kept = await _messageService.TryReactAsync(chatJid, message, string.Empty)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChatDetailViewModel] TryReactAsync (remove) failed: " + ex.Message);
                kept = false;
            }

            if (!kept)
            {
                OutgoingReactionOptimistic.Apply(_reactionMapper, message, snapshot);
            }
        }
    }
}
