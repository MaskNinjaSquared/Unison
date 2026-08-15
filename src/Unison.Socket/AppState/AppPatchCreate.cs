// =============================================================================
// AppPatchCreate / AppStatePatchFactory
//
// The user's side of app state: muting, archiving, pinning, marking read.
//
// Each action has a fixed home - a collection and an api version - that is not
// ours to choose. The server validates them, so a mute filed under the wrong
// collection or stamped with the wrong version is rejected outright. The values
// here come from the reference and should only change when it does.
//
// Several actions also carry a message range: the chat's recent history, which
// the phone uses to work out what "mark as read up to here" or "clear this chat"
// actually covers. Sending an action without it usually works and occasionally
// applies to the wrong messages, which is why the callers are given the option
// rather than having it quietly omitted.
//
// Ports: rc14 chatModificationToAppPatch in src/Utils/chat-utils.ts
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Socket.AppState
{
    /// <summary>One change, ready to be encoded into a patch.</summary>
    public sealed class AppPatchCreate
    {
        public AppPatchCreate()
        {
            Index = new List<string>();
        }

        /// <summary>Which collection it belongs to; see <see cref="WaPatchName"/>.</summary>
        public string Collection { get; set; }

        public IList<string> Index { get; private set; }

        public global::Proto.SyncActionValue SyncAction { get; set; }

        /// <summary>The action's schema version, which the server checks.</summary>
        public int ApiVersion { get; set; }

        public bool IsRemove { get; set; }
    }

    /// <summary>One message, as named in a range.</summary>
    public sealed class RangeMessage
    {
        public string Id { get; set; }

        public bool FromMe { get; set; }

        /// <summary>Required for a message someone else sent in a group; ignored elsewhere.</summary>
        public string Participant { get; set; }

        /// <summary>Unix seconds.</summary>
        public long Timestamp { get; set; }
    }

    public static class AppStatePatchFactory
    {
        /// <summary>Mutes until the given time, or unmutes when it is null.</summary>
        public static AppPatchCreate Mute(string jid, long? muteEndTimestampMs)
        {
            var action = NewAction();
            action.MuteAction = new global::Proto.SyncActionValue.Types.MuteAction
            {
                Muted = muteEndTimestampMs.HasValue,
                MuteEndTimestamp = muteEndTimestampMs ?? 0
            };

            return Build(WaPatchName.RegularHigh, 2, action, "mute", jid);
        }

        public static AppPatchCreate Archive(string jid, bool archived, IEnumerable<RangeMessage> lastMessages = null)
        {
            var action = NewAction();
            action.ArchiveChatAction = new global::Proto.SyncActionValue.Types.ArchiveChatAction
            {
                Archived = archived,
                MessageRange = BuildRange(jid, lastMessages)
            };

            return Build(WaPatchName.RegularLow, 3, action, "archive", jid);
        }

        public static AppPatchCreate MarkRead(string jid, bool read, IEnumerable<RangeMessage> lastMessages = null)
        {
            var action = NewAction();
            action.MarkChatAsReadAction = new global::Proto.SyncActionValue.Types.MarkChatAsReadAction
            {
                Read = read,
                MessageRange = BuildRange(jid, lastMessages)
            };

            return Build(WaPatchName.RegularLow, 3, action, "markChatAsRead", jid);
        }

        public static AppPatchCreate Pin(string jid, bool pinned)
        {
            var action = NewAction();
            action.PinAction = new global::Proto.SyncActionValue.Types.PinAction { Pinned = pinned };

            return Build(WaPatchName.RegularLow, 5, action, "pin_v1", jid);
        }

        public static AppPatchCreate Star(string jid, string messageId, bool fromMe, bool starred)
        {
            var action = NewAction();
            action.StarAction = new global::Proto.SyncActionValue.Types.StarAction { Starred = starred };

            return Build(WaPatchName.RegularLow, 2, action, "star", jid, messageId, fromMe ? "1" : "0", "0");
        }

        public static AppPatchCreate DeleteChat(string jid, IEnumerable<RangeMessage> lastMessages = null)
        {
            var action = NewAction();
            action.DeleteChatAction = new global::Proto.SyncActionValue.Types.DeleteChatAction
            {
                MessageRange = BuildRange(jid, lastMessages)
            };

            return Build(WaPatchName.RegularHigh, 6, action, "deleteChat", jid, "1");
        }

        public static AppPatchCreate ClearChat(string jid, IEnumerable<RangeMessage> lastMessages = null)
        {
            var action = NewAction();
            action.ClearChatAction = new global::Proto.SyncActionValue.Types.ClearChatAction
            {
                MessageRange = BuildRange(jid, lastMessages)
            };

            return Build(WaPatchName.RegularHigh, 6, action, "clearChat", jid, "1", "0");
        }

        /// <summary>
        /// Deletes a message for this account only. The other party keeps their copy, which is
        /// what separates this from a revoke.
        /// </summary>
        public static AppPatchCreate DeleteMessageForMe(
            string jid,
            string messageId,
            bool fromMe,
            long messageTimestamp,
            bool deleteMedia = false)
        {
            var action = NewAction();
            action.DeleteMessageForMeAction = new global::Proto.SyncActionValue.Types.DeleteMessageForMeAction
            {
                DeleteMedia = deleteMedia,
                MessageTimestamp = messageTimestamp
            };

            return Build(
                WaPatchName.RegularHigh,
                3,
                action,
                "deleteMessageForMe",
                jid,
                messageId,
                fromMe ? "1" : "0",
                "0");
        }

        private static global::Proto.SyncActionValue NewAction()
        {
            return new global::Proto.SyncActionValue
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }

        private static AppPatchCreate Build(
            string collection,
            int apiVersion,
            global::Proto.SyncActionValue action,
            params string[] index)
        {
            var patch = new AppPatchCreate
            {
                Collection = collection,
                ApiVersion = apiVersion,
                SyncAction = action
            };

            foreach (var part in index)
            {
                patch.Index.Add(part);
            }

            return patch;
        }

        private static global::Proto.SyncActionValue.Types.SyncActionMessageRange BuildRange(string jid, IEnumerable<RangeMessage> messages)
        {
            if (messages == null)
            {
                return null;
            }

            var range = new global::Proto.SyncActionValue.Types.SyncActionMessageRange();
            var newest = 0L;

            foreach (var message in messages)
            {
                if (message == null || string.IsNullOrEmpty(message.Id))
                {
                    continue;
                }

                var key = new global::Proto.MessageKey
                {
                    RemoteJid = jid,
                    Id = message.Id,
                    FromMe = message.FromMe
                };

                if (!string.IsNullOrEmpty(message.Participant))
                {
                    key.Participant = message.Participant;
                }

                range.Messages.Add(new global::Proto.SyncActionValue.Types.SyncActionMessage
                {
                    Key = key,
                    Timestamp = message.Timestamp
                });

                if (message.Timestamp > newest)
                {
                    newest = message.Timestamp;
                }
            }

            if (range.Messages.Count == 0)
            {
                return null;
            }

            range.LastMessageTimestamp = newest;
            return range;
        }
    }
}
