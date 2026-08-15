// =============================================================================
// HistorySyncProcessor
//
// Turns a decoded HistorySync blob into the chunk the app consumes.
//
// The valuable part is not the flattening, it is the harvesting: every
// conversation in a history chunk names an account in both address spaces, and a
// LID chat that omits its phone number often still reveals it inside a message
// receipt. A single initial sync therefore fills most of the LID mapping store in
// one go - which is exactly what the current code leaves on the table.
//
// Ports: rc14 processHistoryMessage in src/Utils/history.ts
// =============================================================================
using System.Collections.Generic;
using Unison.Socket.Abstractions;
using Unison.Socket.Signal;
using Unison.Socket.WABinary;

namespace Unison.Socket.Sync
{
    public static class HistorySyncProcessor
    {
        public static MessagingHistorySet Process(global::Proto.HistorySync sync, ISocketLog log = null)
        {
            var result = new MessagingHistorySet();
            if (sync == null)
            {
                return result;
            }

            log = log ?? NullSocketLog.Instance;
            result.SyncType = sync.SyncType;
            result.Progress = sync.HasProgress ? (int?)sync.Progress : null;

            foreach (var participants in sync.PastParticipants)
            {
                result.PastParticipants.Add(participants);
            }

            // Present for every sync type, including the ones that carry no conversations.
            foreach (var mapping in sync.PhoneNumberToLidMappings)
            {
                if (!string.IsNullOrEmpty(mapping.LidJid) && !string.IsNullOrEmpty(mapping.PnJid))
                {
                    result.LidMappings.Add(new LidMapping(mapping.LidJid, mapping.PnJid));
                }
            }

            switch (sync.SyncType)
            {
                case global::Proto.HistorySync.Types.HistorySyncType.InitialBootstrap:
                case global::Proto.HistorySync.Types.HistorySyncType.Recent:
                case global::Proto.HistorySync.Types.HistorySyncType.Full:
                case global::Proto.HistorySync.Types.HistorySyncType.OnDemand:
                    foreach (var conversation in sync.Conversations)
                    {
                        ReadConversation(conversation, result);
                    }

                    break;

                case global::Proto.HistorySync.Types.HistorySyncType.PushName:
                    foreach (var pushName in sync.Pushnames)
                    {
                        if (!string.IsNullOrEmpty(pushName.Id))
                        {
                            result.Contacts.Add(new HistoryContact(pushName.Id, pushName.Pushname_));
                        }
                    }

                    break;
            }

            log.Debug(
                "[History] Processed a " + sync.SyncType + " chunk: " + result.Chats.Count + " chat(s), " +
                result.Messages.Count + " message(s), " + result.LidMappings.Count + " mapping(s)");

            return result;
        }

        private static void ReadConversation(global::Proto.Conversation conversation, MessagingHistorySet result)
        {
            var chatId = conversation.Id;
            if (string.IsNullOrEmpty(chatId))
            {
                return;
            }

            var name = FirstNonEmpty(conversation.DisplayName, conversation.Name, conversation.Username);
            if (!string.IsNullOrEmpty(name))
            {
                result.Contacts.Add(new HistoryContact(chatId, name));
            }

            HarvestMapping(conversation, chatId, result);

            foreach (var item in conversation.Messages)
            {
                if (item.Message != null)
                {
                    result.Messages.Add(item.Message);
                }
            }

            result.Chats.Add(conversation);
        }

        /// <summary>
        /// Records the chat's other identity. When a LID chat does not carry its phone number,
        /// the receipts inside its messages usually do.
        /// </summary>
        private static void HarvestMapping(
            global::Proto.Conversation conversation,
            string chatId,
            MessagingHistorySet result)
        {
            var isLid = JidUtils.IsAnyLid(chatId);
            var isPn = JidUtils.IsAnyPn(chatId);

            if (isLid && !string.IsNullOrEmpty(conversation.PnJid))
            {
                result.LidMappings.Add(new LidMapping(chatId, conversation.PnJid));
                return;
            }

            if (isPn && !string.IsNullOrEmpty(conversation.LidJid))
            {
                result.LidMappings.Add(new LidMapping(conversation.LidJid, chatId));
                return;
            }

            if (isLid)
            {
                var pn = ExtractPnFromReceipts(conversation);
                if (!string.IsNullOrEmpty(pn))
                {
                    result.LidMappings.Add(new LidMapping(chatId, pn));
                }
            }
        }

        private static string ExtractPnFromReceipts(global::Proto.Conversation conversation)
        {
            foreach (var item in conversation.Messages)
            {
                if (item.Message == null)
                {
                    continue;
                }

                foreach (var receipt in item.Message.UserReceipt)
                {
                    if (JidUtils.IsAnyPn(receipt.UserJid))
                    {
                        return receipt.UserJid;
                    }
                }
            }

            return null;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }

            return null;
        }
    }
}
