// =============================================================================
// UpdateGroupSettingsUseCase
//
// Everything about a group that is not its membership: subject, description,
// who may post, who may edit it, the disappearing timer, who may add people,
// and whether joining needs approval.
//
// The description is the odd one. It is versioned, so a change has to name the
// description it replaces, and sending one without that reference is rejected
// as a conflict. The previous id is read from the group's metadata first, which
// is why this operation needs a way to look the group up and the others do not.
//
// Leaving is here as well. It reads like membership but the server treats it as
// a setting on the group server rather than a change to the participant list.
//
// Ports: rc14 groupUpdateSubject, groupUpdateDescription, groupSettingUpdate,
// groupToggleEphemeral, groupMemberAddMode, groupJoinApprovalMode and
// groupLeave in src/Socket/groups.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Messages;
using Unison.Socket.Models;
using Unison.Socket.Session;

namespace Unison.Socket.UseCases.Groups
{
    public sealed class UpdateGroupSettingsUseCase
    {
        private readonly ConnectionHandler _connection;

        public UpdateGroupSettingsUseCase(ConnectionHandler connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            _connection = connection;
        }

        /// <summary>
        /// Resolves the group's current metadata, needed only to replace a description. Left unset,
        /// description changes are refused rather than sent without their reference.
        /// </summary>
        public Func<string, Task<GroupMetadata>> GetGroupMetadata { get; set; }

        public Task UpdateSubjectAsync(string groupJid, string subject, TimeSpan? timeout = null)
        {
            var node = new BinaryNode("subject", null, Encoding.UTF8.GetBytes(subject ?? string.Empty));
            return Send(groupJid, node, timeout);
        }

        /// <param name="description">Null or empty removes the description.</param>
        public async Task UpdateDescriptionAsync(string groupJid, string description, TimeSpan? timeout = null)
        {
            string previous = null;

            if (GetGroupMetadata != null)
            {
                var metadata = await GetGroupMetadata(groupJid).ConfigureAwait(false);
                previous = metadata != null ? metadata.DescriptionId : null;
            }

            var attributes = new Dictionary<string, string>();
            List<BinaryNode> children = null;

            if (string.IsNullOrEmpty(description))
            {
                attributes["delete"] = "true";
            }
            else
            {
                attributes["id"] = MessageContent.GenerateMessageId(null);
                children = new List<BinaryNode>
                {
                    new BinaryNode("body", null, Encoding.UTF8.GetBytes(description))
                };
            }

            if (!string.IsNullOrEmpty(previous))
            {
                attributes["prev"] = previous;
            }

            await Send(groupJid, new BinaryNode("description", attributes, children), timeout)
                .ConfigureAwait(false);
        }

        /// <param name="adminsOnly">True to let only admins post.</param>
        public Task UpdateAnnouncementAsync(string groupJid, bool adminsOnly, TimeSpan? timeout = null)
        {
            return Send(groupJid, new BinaryNode(adminsOnly ? "announcement" : "not_announcement"), timeout);
        }

        /// <param name="adminsOnly">True to let only admins change the subject, picture and description.</param>
        public Task UpdateLockedAsync(string groupJid, bool adminsOnly, TimeSpan? timeout = null)
        {
            return Send(groupJid, new BinaryNode(adminsOnly ? "locked" : "unlocked"), timeout);
        }

        /// <param name="seconds">How long messages last, or zero to turn disappearing off.</param>
        public Task UpdateEphemeralAsync(string groupJid, int seconds, TimeSpan? timeout = null)
        {
            var node = seconds > 0
                ? new BinaryNode(
                    "ephemeral",
                    new Dictionary<string, string> { { "expiration", seconds.ToString() } })
                : new BinaryNode("not_ephemeral");

            return Send(groupJid, node, timeout);
        }

        /// <param name="adminsOnly">True to let only admins add participants.</param>
        public Task UpdateMemberAddModeAsync(string groupJid, bool adminsOnly, TimeSpan? timeout = null)
        {
            var mode = adminsOnly ? "admin_add" : "all_member_add";
            var node = new BinaryNode("member_add_mode", null, Encoding.UTF8.GetBytes(mode));

            return Send(groupJid, node, timeout);
        }

        /// <param name="required">True to make people joining by link wait for approval.</param>
        public Task UpdateJoinApprovalAsync(string groupJid, bool required, TimeSpan? timeout = null)
        {
            var node = new BinaryNode(
                "membership_approval_mode",
                null,
                new List<BinaryNode>
                {
                    new BinaryNode(
                        "group_join",
                        new Dictionary<string, string> { { "state", required ? "on" : "off" } })
                });

            return Send(groupJid, node, timeout);
        }

        /// <summary>
        /// Leaves the group. Addressed to the group server rather than to the group, since after
        /// this we are no longer a member of it.
        /// </summary>
        public Task LeaveAsync(string groupJid, TimeSpan? timeout = null)
        {
            var leave = new BinaryNode(
                "leave",
                null,
                new List<BinaryNode>
                {
                    new BinaryNode("group", new Dictionary<string, string> { { "id", groupJid } })
                });

            return GroupQuery.ExecuteAsync(
                _connection,
                GroupQuery.GroupServer,
                "set",
                new List<BinaryNode> { leave },
                timeout);
        }

        private Task<BinaryNode> Send(string groupJid, BinaryNode node, TimeSpan? timeout)
        {
            return GroupQuery.ExecuteAsync(_connection, groupJid, "set", new List<BinaryNode> { node }, timeout);
        }
    }
}
