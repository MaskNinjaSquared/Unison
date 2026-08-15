// =============================================================================
// GroupNotificationParser
//
// Turns a w:gp2 notification into the two events the app cares about: a change
// to the group, or a change to who is in it.
//
// The tag list is kept in the same order as the reference switch so the two can
// be read side by side. Two cases are less obvious than they look. "leave" and
// "remove" collapse into the same action, as they do upstream: whether someone
// walked out or was thrown out only changes the wording of a system line, and
// system lines are not emitted here. And "modify" is not an edit at all - it is
// a participant changing addresses, which the app has to follow or it will keep
// writing to a JID nobody reads.
//
// Ports: rc14 handleGroupNotification in src/Socket/messages-recv.ts
// =============================================================================
using System.Collections.Generic;
using Unison.Baileys.Protocol;
using Unison.Socket.Groups;
using Unison.Socket.Models;
using Unison.Socket.WABinary;

namespace Unison.Socket.Notifications
{
    /// <summary>Everything one w:gp2 notification produced. Any field may be null.</summary>
    public sealed class GroupNotificationResult
    {
        /// <summary>Set when the notification changed the group itself.</summary>
        public GroupUpdate Update { get; set; }

        /// <summary>Set when the notification changed the membership.</summary>
        public GroupParticipantsUpdate Participants { get; set; }

        /// <summary>Set when the notification announced a group we were just added to.</summary>
        public GroupMetadata Created { get; set; }

        public bool IsEmpty
        {
            get { return Update == null && Participants == null && Created == null; }
        }
    }

    public static class GroupNotificationParser
    {
        public static GroupNotificationResult Parse(BinaryNode node)
        {
            var result = new GroupNotificationResult();
            if (node == null)
            {
                return result;
            }

            var children = node.GetAllChildren();
            if (children == null || children.Count == 0)
            {
                return result;
            }

            var child = children[0];
            var groupId = JidUtils.NormalizedUser(node.GetAttribute("from"));
            var author = node.GetAttribute("participant");

            switch (child.Tag)
            {
                case "create":
                    result.Created = GroupMetadataParser.Parse(child);
                    if (result.Created != null && string.IsNullOrEmpty(result.Created.Id))
                    {
                        result.Created.Id = groupId;
                    }

                    break;

                case "ephemeral":
                    result.Update = new GroupUpdate(groupId)
                    {
                        Author = author,
                        EphemeralDuration = ParseInt(child.GetAttribute("expiration"))
                    };
                    break;

                case "not_ephemeral":
                    result.Update = new GroupUpdate(groupId) { Author = author, EphemeralDuration = 0 };
                    break;

                case "modify":
                    result.Participants = BuildParticipants(
                        groupId,
                        author,
                        GroupParticipantAction.Modify,
                        child);
                    break;

                case "promote":
                    result.Participants = BuildParticipants(
                        groupId,
                        author,
                        GroupParticipantAction.Promote,
                        child);
                    break;

                case "demote":
                    result.Participants = BuildParticipants(
                        groupId,
                        author,
                        GroupParticipantAction.Demote,
                        child);
                    break;

                case "add":
                    result.Participants = BuildParticipants(
                        groupId,
                        author,
                        GroupParticipantAction.Add,
                        child);
                    break;

                case "leave":
                case "remove":
                    result.Participants = BuildParticipants(
                        groupId,
                        author,
                        GroupParticipantAction.Remove,
                        child);
                    break;

                case "subject":
                    result.Update = new GroupUpdate(groupId)
                    {
                        Author = author,
                        Subject = child.GetAttribute("subject"),
                        SubjectOwner = author,
                        SubjectTime = ParseLong(child.GetAttribute("s_t"))
                    };
                    break;

                case "description":
                    var body = child.GetChild("body");
                    result.Update = new GroupUpdate(groupId)
                    {
                        Author = author,
                        Description = body != null ? body.GetContentString() : string.Empty,
                        DescriptionId = child.GetAttribute("id")
                    };
                    break;

                case "announcement":
                    result.Update = new GroupUpdate(groupId) { Author = author, Announce = true };
                    break;

                case "not_announcement":
                    result.Update = new GroupUpdate(groupId) { Author = author, Announce = false };
                    break;

                case "locked":
                    result.Update = new GroupUpdate(groupId) { Author = author, Restrict = true };
                    break;

                case "unlocked":
                    result.Update = new GroupUpdate(groupId) { Author = author, Restrict = false };
                    break;

                case "member_add_mode":
                    // The value is the element's text, not an attribute: "all_member_add" or "admin_add".
                    result.Update = new GroupUpdate(groupId)
                    {
                        Author = author,
                        MemberAddMode = child.GetContentString() == "all_member_add"
                    };
                    break;

                case "membership_approval_mode":
                    var join = child.GetChild("group_join");
                    result.Update = new GroupUpdate(groupId)
                    {
                        Author = author,
                        JoinApprovalMode = join != null && join.GetAttribute("state") == "on"
                    };
                    break;
            }

            return result;
        }

        /// <summary>
        /// A removal that names only the sender is a departure. The distinction matters to the app,
        /// which words the two differently, and the wire does not make it for us.
        /// </summary>
        private static GroupParticipantsUpdate BuildParticipants(
            string groupId,
            string author,
            GroupParticipantAction action,
            BinaryNode child)
        {
            var update = new GroupParticipantsUpdate
            {
                Id = groupId,
                Author = author,
                Action = action
            };

            var participants = child.GetChildren("participant");
            if (participants != null)
            {
                foreach (var participant in participants)
                {
                    var jid = participant.GetAttribute("jid");
                    if (!string.IsNullOrEmpty(jid))
                    {
                        update.Participants.Add(jid);
                    }

                    var error = participant.GetAttribute("error");
                    if (!string.IsNullOrEmpty(error) && !string.IsNullOrEmpty(jid))
                    {
                        if (update.Reasons == null)
                        {
                            update.Reasons = new Dictionary<string, int>();
                        }

                        update.Reasons[jid] = ParseInt(error) ?? 0;
                    }
                }
            }

            return update;
        }

        private static int? ParseInt(string value)
        {
            int parsed;
            return int.TryParse(value, out parsed) ? (int?)parsed : null;
        }

        private static long? ParseLong(string value)
        {
            long parsed;
            return long.TryParse(value, out parsed) ? (long?)parsed : null;
        }
    }
}
