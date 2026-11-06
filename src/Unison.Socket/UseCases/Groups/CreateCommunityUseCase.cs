// =============================================================================
// CreateCommunityUseCase
//
// Creates a community: a parent group that holds linked subgroups, plus the
// general chat the server opens alongside it.
//
// The create answer only names the new parent, so the metadata is read in a
// second query, as rc14 does. If that read fails the community still exists;
// the caller gets the id and subject rather than an error for a create that
// went through.
//
// Ports: rc14 communityCreate in src/Socket/communities.ts
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
    public sealed class CreateCommunityUseCase
    {
        /// <summary>rc14 cuts a v1 message id to this length for the description id.</summary>
        private const int DescriptionIdLength = 12;

        private readonly ConnectionHandler _connection;
        private readonly FetchGroupMetadataUseCase _metadata;

        public CreateCommunityUseCase(ConnectionHandler connection, FetchGroupMetadataUseCase metadata)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        }

        public async Task<GroupMetadata> ExecuteAsync(
            string subject,
            string description,
            TimeSpan? timeout = null)
        {
            var create = BuildCreateNode(subject, description, NewDescriptionId());

            var response = await GroupQuery
                .ExecuteAsync(_connection, GroupQuery.GroupServer, "set", new List<BinaryNode> { create }, timeout)
                .ConfigureAwait(false);

            var communityJid = ReadCreatedJid(response);
            if (communityJid == null)
            {
                throw new InvalidOperationException("The server answered the community create without a group");
            }

            GroupMetadataResult fetched = null;
            try
            {
                fetched = await _metadata.ExecuteAsync(communityJid, timeout).ConfigureAwait(false);
            }
            catch (WaConnectionException)
            {
            }

            if (fetched != null && fetched.Metadata != null)
            {
                return fetched.Metadata;
            }

            return new GroupMetadata
            {
                Id = communityJid,
                Subject = subject,
                Description = description,
                IsCommunity = true
            };
        }

        /// <summary>The create node exactly as rc14 sends it (test seam).</summary>
        public static BinaryNode BuildCreateNode(string subject, string description, string descriptionId)
        {
            if (string.IsNullOrEmpty(subject))
            {
                throw new ArgumentException("A subject is required", nameof(subject));
            }

            if (string.IsNullOrEmpty(descriptionId))
            {
                throw new ArgumentException("A description id is required", nameof(descriptionId));
            }

            return new BinaryNode(
                "create",
                new Dictionary<string, string> { { "subject", subject } },
                new List<BinaryNode>
                {
                    new BinaryNode(
                        "description",
                        new Dictionary<string, string> { { "id", descriptionId } },
                        new List<BinaryNode>
                        {
                            new BinaryNode("body", null, Encoding.UTF8.GetBytes(description ?? string.Empty))
                        }),
                    new BinaryNode(
                        "parent",
                        new Dictionary<string, string> { { "default_membership_approval_mode", "request_required" } }),
                    new BinaryNode("allow_non_admin_sub_group_creation"),
                    new BinaryNode("create_general_chat")
                });
        }

        /// <summary>The parent's JID from the create answer, or null when it names none.</summary>
        public static string ReadCreatedJid(BinaryNode response)
        {
            var group = response != null ? response.GetChild("group") : null;
            var id = group != null ? group.GetAttribute("id") : null;
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            return id.IndexOf('@') >= 0 ? id : WA.JidEncode(id, WA.G_US);
        }

        private static string NewDescriptionId()
        {
            return MessageContent.GenerateMessageId(null).Substring(0, DescriptionIdLength);
        }
    }
}
