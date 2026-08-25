// =============================================================================
// ChatDeletionPrompt
//
// Ask, then delete. Three places offer to delete a conversation - the list row,
// the chat overflow and the 1:1 info bar - and all three owe the user the same
// question and the same wording.
//
// The confirmation is not decoration. Deleting a chat removes the messages from
// this device and tells the account to drop it everywhere; there is no undo and
// nothing left to restore from, unlike a pin.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class ChatDeletionPrompt
    {
        /// <summary>
        /// Confirms with the user and deletes on yes. Returns true only when the conversation was
        /// actually deleted, so a caller that has to navigate away can tell the difference between
        /// a cancel and a failure.
        /// </summary>
        public static async Task<bool> ConfirmAndDeleteAsync(
            ChatItem chat,
            IChatService chats,
            IDialogService dialogs,
            IStringResources strings)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID) || chats == null)
            {
                return false;
            }

            if (dialogs != null)
            {
                bool confirmed = await dialogs.ShowConfirmAsync(
                    title: Get(strings, "Chat_DeleteChatTitle", "Delete chat?"),
                    content: string.Format(
                        Get(
                            strings,
                            "Chat_DeleteChatBody",
                            "\"{0}\" and its messages will be deleted on this phone and on your other devices. This cannot be undone."),
                        DescribeChat(chat)),
                    primaryButtonText: Get(strings, "Chat_DeleteChatConfirm", "Delete"),
                    closeButtonText: Get(strings, "Common_Cancel", "Cancel"));

                if (!confirmed)
                {
                    return false;
                }
            }

            try
            {
                await chats.DeleteChatAsync(chat);

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatDeletionPrompt] Delete failed: " + ex.GetBaseException().Message);
                if (dialogs != null)
                {
                    try
                    {
                        await dialogs.ShowConfirmAsync(
                            title: Get(strings, "Chat_DeleteChatFailedTitle", "Chat not deleted"),
                            content: Get(
                                strings,
                                "Chat_DeleteChatFailedBody",
                                "WhatsApp did not accept the change, so the conversation was left as it is. Try again when you are connected."),
                            primaryButtonText: Get(strings, "Common_OK", "OK"),
                            closeButtonText: string.Empty);
                    }
                    catch
                    {
                        // The failure is already reported in the log; a dialog that cannot be shown
                        // must not turn a handled error into a crash.
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// What to call the conversation in the question. A chat with no name yet is better
        /// described as "this chat" than as a bare JID.
        /// </summary>
        private static string DescribeChat(ChatItem chat)
        {
            return string.IsNullOrWhiteSpace(chat.Name) ? chat.JID : chat.Name;
        }

        private static string Get(IStringResources strings, string key, string fallback)
        {
            return strings == null ? fallback : strings.Get(key, fallback);
        }
    }
}
