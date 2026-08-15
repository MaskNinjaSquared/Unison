// =============================================================================
// GroupQuery
//
// The one query shape every group operation is built on.
//
// Creating a group, renaming it, adding people, leaving: all of them are an IQ
// under w:g2 addressed either to the group or to the group server, differing
// only in the child node. Writing that envelope out at each call site is how
// small differences creep in, so it is written once here and the operations
// supply their content.
//
// Ports: rc14 groupQuery in src/Socket/groups.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Session;
using Unison.Socket.WABinary;

namespace Unison.Socket.UseCases.Groups
{
    internal static class GroupQuery
    {
        /// <summary>The group server itself, used to create a group or to join one by invite.</summary>
        public const string GroupServer = "@" + JidUtils.ServerGroup;

        public static Task<BinaryNode> ExecuteAsync(
            ConnectionHandler connection,
            string jid,
            string type,
            IList<BinaryNode> content,
            TimeSpan? timeout = null)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            if (string.IsNullOrEmpty(jid))
            {
                throw new ArgumentException("A group is required", nameof(jid));
            }

            var iq = new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", jid },
                    { "type", type },
                    { "xmlns", "w:g2" }
                },
                content != null ? new List<BinaryNode>(content) : null);

            return connection.QueryAsync(iq, timeout);
        }

        public static List<BinaryNode> Participants(IEnumerable<string> jids)
        {
            var nodes = new List<BinaryNode>();
            if (jids == null)
            {
                return nodes;
            }

            foreach (var jid in jids)
            {
                if (!string.IsNullOrEmpty(jid))
                {
                    nodes.Add(new BinaryNode(
                        "participant",
                        new Dictionary<string, string> { { "jid", jid } }));
                }
            }

            if (nodes.Count == 0)
            {
                throw new ArgumentException("At least one participant is required", nameof(jids));
            }

            return nodes;
        }
    }
}
