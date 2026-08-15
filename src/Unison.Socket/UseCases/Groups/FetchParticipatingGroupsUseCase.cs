// =============================================================================
// FetchParticipatingGroupsUseCase
//
// Lists every group the account belongs to, with full metadata for each.
//
// One query replaces the per-group fetches the app falls back to today, and the
// participant lists it returns are the cheapest bulk source of LID/PN pairs
// available, so the mappings come back with it.
//
// Ports: rc14 groupFetchAllParticipating in src/Socket/groups.ts
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
    public sealed class ParticipatingGroupsResult
    {
        public ParticipatingGroupsResult()
        {
            Groups = new List<GroupMetadata>();
            Mappings = new List<LidMapping>();
        }

        public IList<GroupMetadata> Groups { get; private set; }

        /// <summary>LID/PN pairs disclosed across every participant list in the reply.</summary>
        public IList<LidMapping> Mappings { get; private set; }

        public string FailureReason { get; set; }
    }

    public sealed class FetchParticipatingGroupsUseCase
    {
        private readonly ConnectionHandler _connection;

        public FetchParticipatingGroupsUseCase(ConnectionHandler connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            _connection = connection;
        }

        public async Task<ParticipatingGroupsResult> ExecuteAsync(TimeSpan? timeout = null)
        {
            var iq = new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", "@" + WA.G_US },
                    { "type", "get" },
                    { "xmlns", "w:g2" }
                },
                new List<BinaryNode>
                {
                    new BinaryNode(
                        "participating",
                        null,
                        new List<BinaryNode>
                        {
                            new BinaryNode("participants"),
                            new BinaryNode("description")
                        })
                });

            var result = new ParticipatingGroupsResult();

            BinaryNode response;
            try
            {
                response = await _connection.QueryAsync(iq, timeout).ConfigureAwait(false);
            }
            catch (WaConnectionException ex)
            {
                result.FailureReason = ((int)ex.Reason).ToString();
                return result;
            }

            var groups = response != null ? response.GetChild("groups") : null;
            if (groups == null)
            {
                result.FailureReason = "no-groups-node";
                return result;
            }

            foreach (var groupNode in groups.GetChildren("group"))
            {
                var metadata = GroupMetadataParser.Parse(groupNode);
                if (metadata == null)
                {
                    continue;
                }

                result.Groups.Add(metadata);
                foreach (var mapping in GroupMetadataParser.ExtractLidMappings(metadata))
                {
                    result.Mappings.Add(mapping);
                }
            }

            return result;
        }
    }
}
