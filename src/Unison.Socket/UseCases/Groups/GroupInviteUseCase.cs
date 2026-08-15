// =============================================================================
// GroupInviteUseCase
//
// The invite link: reading it, replacing it, looking one up, and using it.
//
// Revoking is the same query as reading with a different type, and it answers
// with the new code - the old link stops working the moment it returns, which
// is the point of it.
//
// Looking a code up before joining is worth doing: it returns the group's
// metadata without joining, so the user can be shown what they are about to
// walk into.
//
// Ports: rc14 groupInviteCode, groupRevokeInvite, groupGetInviteInfo and
// groupAcceptInvite in src/Socket/groups.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Groups;
using Unison.Socket.Models;
using Unison.Socket.Session;

namespace Unison.Socket.UseCases.Groups
{
    public sealed class GroupInviteUseCase
    {
        private readonly ConnectionHandler _connection;

        public GroupInviteUseCase(ConnectionHandler connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            _connection = connection;
        }

        /// <summary>Returns the current code. The full link is this appended to chat.whatsapp.com.</summary>
        public Task<string> FetchCodeAsync(string groupJid, TimeSpan? timeout = null)
        {
            return QueryCodeAsync(groupJid, "get", timeout);
        }

        /// <summary>Invalidates the current link and returns the one that replaces it.</summary>
        public Task<string> RevokeAsync(string groupJid, TimeSpan? timeout = null)
        {
            return QueryCodeAsync(groupJid, "set", timeout);
        }

        /// <summary>Reads a group's details from a code, without joining.</summary>
        public async Task<GroupMetadata> FetchInfoAsync(string code, TimeSpan? timeout = null)
        {
            var response = await SendInviteAsync(code, "get", timeout).ConfigureAwait(false);
            return GroupMetadataParser.Parse(response);
        }

        /// <summary>Joins the group and returns its JID.</summary>
        public async Task<string> AcceptAsync(string code, TimeSpan? timeout = null)
        {
            var response = await SendInviteAsync(code, "set", timeout).ConfigureAwait(false);

            var group = response != null ? response.GetChild("group") : null;
            return group != null ? group.GetAttribute("jid") : null;
        }

        private async Task<string> QueryCodeAsync(string groupJid, string type, TimeSpan? timeout)
        {
            var response = await GroupQuery
                .ExecuteAsync(
                    _connection,
                    groupJid,
                    type,
                    new List<BinaryNode> { new BinaryNode("invite") },
                    timeout)
                .ConfigureAwait(false);

            var invite = response != null ? response.GetChild("invite") : null;
            return invite != null ? invite.GetAttribute("code") : null;
        }

        private Task<BinaryNode> SendInviteAsync(string code, string type, TimeSpan? timeout)
        {
            if (string.IsNullOrEmpty(code))
            {
                throw new ArgumentException("An invite code is required", nameof(code));
            }

            var invite = new BinaryNode("invite", new Dictionary<string, string> { { "code", code } });

            return GroupQuery.ExecuteAsync(
                _connection,
                GroupQuery.GroupServer,
                type,
                new List<BinaryNode> { invite },
                timeout);
        }
    }
}
