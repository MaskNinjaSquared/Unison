// =============================================================================
// CreateGroupUseCase
//
// Creates a group and returns it as the server describes it.
//
// The participant list is a request, not a guarantee. People who block group
// invitations, or who are not reachable, simply do not appear in the metadata
// that comes back - so the answer is worth reading rather than assuming.
//
// The key attribute is a message id and is what makes the call safe to repeat:
// a retry with the same key joins the group that was already created instead of
// making a second one.
//
// Ports: rc14 groupCreate in src/Socket/groups.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Groups;
using Unison.Socket.Messages;
using Unison.Socket.Models;
using Unison.Socket.Session;

namespace Unison.Socket.UseCases.Groups
{
    public sealed class CreateGroupUseCase
    {
        private readonly ConnectionHandler _connection;

        public CreateGroupUseCase(ConnectionHandler connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            _connection = connection;
        }

        public async Task<GroupMetadata> ExecuteAsync(
            string subject,
            IEnumerable<string> participants,
            TimeSpan? timeout = null)
        {
            var create = BuildCreateNode(subject, participants, MessageContent.GenerateMessageId(null));

            var response = await GroupQuery
                .ExecuteAsync(_connection, GroupQuery.GroupServer, "set", new List<BinaryNode> { create }, timeout)
                .ConfigureAwait(false);

            var created = GroupMetadataParser.Parse(response);
            if (created == null)
            {
                throw new InvalidOperationException("The server answered the group create without a group");
            }

            return created;
        }

        /// <summary>The create node exactly as rc14 sends it; no participants is a group of one (test seam).</summary>
        public static BinaryNode BuildCreateNode(string subject, IEnumerable<string> participants, string key)
        {
            if (string.IsNullOrEmpty(subject))
            {
                throw new ArgumentException("A subject is required", nameof(subject));
            }

            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("A key is required", nameof(key));
            }

            return new BinaryNode(
                "create",
                new Dictionary<string, string>
                {
                    { "subject", subject },
                    { "key", key }
                },
                GroupQuery.Participants(participants));
        }
    }
}
