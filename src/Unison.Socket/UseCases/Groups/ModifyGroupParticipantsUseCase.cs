// =============================================================================
// ModifyGroupParticipantsUseCase
//
// Adds, removes, promotes and demotes people, and answers join requests.
//
// The four membership actions are one query with a different tag, and all four
// report per participant: adding ten people can leave eight in the group and
// two refused, each with its own reason. That is why the result is a list and
// not a boolean - a caller that ignores it will tell the user things happened
// that did not.
//
// The commonest refusal is 403 on add: the person only accepts invitations, and
// the server hands back an invite link to send them instead.
//
// Ports: rc14 groupParticipantsUpdate and groupRequestParticipantsUpdate in
// src/Socket/groups.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Session;

namespace Unison.Socket.UseCases.Groups
{
    public enum GroupParticipantAction
    {
        Add,
        Remove,
        Promote,
        Demote
    }

    /// <summary>What the server did about one person.</summary>
    public sealed class GroupParticipantResult
    {
        public string Jid { get; set; }

        /// <summary>"200" when it worked; anything else is a refusal.</summary>
        public string Status { get; set; }

        /// <summary>
        /// Set when someone can only be invited rather than added. Sending them this link is the
        /// only way in.
        /// </summary>
        public string InviteCode { get; set; }

        public long InviteExpiration { get; set; }

        public bool Succeeded
        {
            get { return Status == "200"; }
        }
    }

    public sealed class ModifyGroupParticipantsUseCase
    {
        private readonly ConnectionHandler _connection;

        public ModifyGroupParticipantsUseCase(ConnectionHandler connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            _connection = connection;
        }

        public async Task<List<GroupParticipantResult>> ExecuteAsync(
            string groupJid,
            IEnumerable<string> participants,
            GroupParticipantAction action,
            TimeSpan? timeout = null)
        {
            var node = new BinaryNode(
                Tag(action),
                null,
                GroupQuery.Participants(participants));

            var response = await GroupQuery
                .ExecuteAsync(_connection, groupJid, "set", new List<BinaryNode> { node }, timeout)
                .ConfigureAwait(false);

            return ReadResults(response, Tag(action));
        }

        /// <summary>Approves or rejects people waiting to join a group that vets its members.</summary>
        public async Task<List<GroupParticipantResult>> UpdateJoinRequestsAsync(
            string groupJid,
            IEnumerable<string> participants,
            bool approve,
            TimeSpan? timeout = null)
        {
            var action = new BinaryNode(
                approve ? "approve" : "reject",
                null,
                GroupQuery.Participants(participants));

            var node = new BinaryNode(
                "membership_requests_action",
                null,
                new List<BinaryNode> { action });

            var response = await GroupQuery
                .ExecuteAsync(_connection, groupJid, "set", new List<BinaryNode> { node }, timeout)
                .ConfigureAwait(false);

            var container = response != null ? response.GetChild("membership_requests_action") : null;
            return ReadResults(container, approve ? "approve" : "reject");
        }

        /// <summary>Lists the people waiting for approval to join.</summary>
        public async Task<List<string>> FetchJoinRequestsAsync(string groupJid, TimeSpan? timeout = null)
        {
            var response = await GroupQuery
                .ExecuteAsync(
                    _connection,
                    groupJid,
                    "get",
                    new List<BinaryNode> { new BinaryNode("membership_approval_requests") },
                    timeout)
                .ConfigureAwait(false);

            var container = response != null ? response.GetChild("membership_approval_requests") : null;
            var waiting = new List<string>();

            if (container == null)
            {
                return waiting;
            }

            var requests = container.GetChildren("membership_approval_request");
            if (requests != null)
            {
                foreach (var request in requests)
                {
                    var jid = request.GetAttribute("jid");
                    if (!string.IsNullOrEmpty(jid))
                    {
                        waiting.Add(jid);
                    }
                }
            }

            return waiting;
        }

        private static List<GroupParticipantResult> ReadResults(BinaryNode response, string tag)
        {
            var results = new List<GroupParticipantResult>();

            var container = response != null ? response.GetChild(tag) : null;
            var participants = container != null ? container.GetChildren("participant") : null;

            if (participants == null)
            {
                return results;
            }

            foreach (var participant in participants)
            {
                var result = new GroupParticipantResult
                {
                    Jid = participant.GetAttribute("jid"),
                    Status = participant.GetAttribute("error") ?? "200"
                };

                var invite = participant.GetChild("add_request");
                if (invite != null)
                {
                    result.InviteCode = invite.GetAttribute("code");

                    long expiration;
                    if (long.TryParse(invite.GetAttribute("expiration"), out expiration))
                    {
                        result.InviteExpiration = expiration;
                    }
                }

                results.Add(result);
            }

            return results;
        }

        private static string Tag(GroupParticipantAction action)
        {
            switch (action)
            {
                case GroupParticipantAction.Add:
                    return "add";

                case GroupParticipantAction.Remove:
                    return "remove";

                case GroupParticipantAction.Promote:
                    return "promote";

                case GroupParticipantAction.Demote:
                    return "demote";

                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }
        }
    }
}
