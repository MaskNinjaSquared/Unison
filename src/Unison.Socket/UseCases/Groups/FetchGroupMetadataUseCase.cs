// =============================================================================
// FetchGroupMetadataUseCase
//
// Asks the server for one group's full metadata.
//
// The reply is also the best source of LID/PN pairs there is - a group of forty
// people discloses forty mappings in one round trip - so the result carries them
// alongside the metadata for the facade to feed into the mapping store.
//
// Ports: rc14 groupMetadata in src/Socket/groups.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Groups;
using Unison.Socket.Models;
using Unison.Socket.Session;
using Unison.Socket.Signal;

namespace Unison.Socket.UseCases.Groups
{
    /// <summary>
    /// Outcome of a metadata query. Being refused is expected - a group we left answers 403 -
    /// so it is reported rather than thrown.
    /// </summary>
    public sealed class GroupMetadataResult
    {
        public GroupMetadata Metadata { get; set; }

        /// <summary>LID/PN pairs disclosed by the participant list.</summary>
        public IReadOnlyList<LidMapping> Mappings { get; set; }

        /// <summary>Server error code when the query was refused.</summary>
        public string FailureReason { get; set; }

        public bool HasMetadata
        {
            get { return Metadata != null; }
        }
    }

    public sealed class FetchGroupMetadataUseCase
    {
        private readonly ConnectionHandler _connection;

        public FetchGroupMetadataUseCase(ConnectionHandler connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            _connection = connection;
        }

        public async Task<GroupMetadataResult> ExecuteAsync(string groupJid, TimeSpan? timeout = null)
        {
            if (string.IsNullOrEmpty(groupJid))
            {
                throw new ArgumentException("groupJid is required", nameof(groupJid));
            }

            var iq = new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", groupJid },
                    { "type", "get" },
                    { "xmlns", "w:g2" }
                },
                new List<BinaryNode>
                {
                    new BinaryNode("query", new Dictionary<string, string> { { "request", "interactive" } })
                });

            BinaryNode response;
            try
            {
                // A group we left answers 403, which the caller reads off the result.
                response = await _connection.QueryAllowingErrorAsync(iq, timeout).ConfigureAwait(false);
            }
            catch (WaConnectionException ex)
            {
                return new GroupMetadataResult
                {
                    Mappings = new List<LidMapping>(),
                    FailureReason = ((int)ex.Reason).ToString()
                };
            }

            var metadata = GroupMetadataParser.Parse(response);
            if (metadata == null)
            {
                var error = response != null ? response.GetChild("error") : null;
                return new GroupMetadataResult
                {
                    Mappings = new List<LidMapping>(),
                    FailureReason = error != null ? error.GetAttribute("code") : "no-group-node"
                };
            }

            return new GroupMetadataResult
            {
                Metadata = metadata,
                Mappings = GroupMetadataParser.ExtractLidMappings(metadata)
            };
        }
    }
}
