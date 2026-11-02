// =============================================================================
// FetchCommunityLinkedGroupsUseCase
//
// Lists every linked subgroup of a community (including ones this account has
// not joined). Ports Baileys communityFetchLinkedGroups: resolve the parent
// when given a subgroup JID, then IQ get + sub_groups.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Models;
using Unison.Socket.Session;

namespace Unison.Socket.UseCases.Groups
{
    public sealed class CommunityLinkedGroupInfo
    {
        public string Jid { get; set; }

        public string Subject { get; set; }

        public long CreationUnixSeconds { get; set; }

        public string Owner { get; set; }

        public int Size { get; set; }
    }

    public sealed class CommunityLinkedGroupsResult
    {
        public string CommunityJid { get; set; }

        public bool IsCommunity { get; set; }

        public IList<CommunityLinkedGroupInfo> LinkedGroups { get; } =
            new List<CommunityLinkedGroupInfo>();

        public string FailureReason { get; set; }
    }

    public sealed class FetchCommunityLinkedGroupsUseCase
    {
        private readonly ConnectionHandler _connection;
        private readonly FetchGroupMetadataUseCase _metadata;

        public FetchCommunityLinkedGroupsUseCase(
            ConnectionHandler connection,
            FetchGroupMetadataUseCase metadata)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        }

        public async Task<CommunityLinkedGroupsResult> ExecuteAsync(
            string jid,
            TimeSpan? timeout = null)
        {
            var result = new CommunityLinkedGroupsResult();
            if (string.IsNullOrWhiteSpace(jid))
            {
                result.FailureReason = "empty-jid";
                return result;
            }

            string communityJid = jid;
            bool isCommunity = false;
            try
            {
                GroupMetadataResult metaResult = await _metadata.ExecuteAsync(jid, timeout)
                    .ConfigureAwait(false);
                GroupMetadata meta = metaResult != null ? metaResult.Metadata : null;
                if (meta != null && !string.IsNullOrWhiteSpace(meta.LinkedParent))
                {
                    communityJid = meta.LinkedParent;
                }
                else
                {
                    isCommunity = meta == null || meta.IsCommunity ||
                        string.IsNullOrWhiteSpace(meta.LinkedParent);
                }
            }
            catch (Exception)
            {
                communityJid = jid;
                isCommunity = true;
            }

            result.CommunityJid = communityJid;
            result.IsCommunity = isCommunity;

            BinaryNode response;
            try
            {
                response = await GroupQuery
                    .ExecuteAsync(
                        _connection,
                        communityJid,
                        "get",
                        new List<BinaryNode> { new BinaryNode("sub_groups") },
                        timeout)
                    .ConfigureAwait(false);
            }
            catch (WaConnectionException ex)
            {
                result.FailureReason = ((int)ex.Reason).ToString();
                return result;
            }
            catch (Exception ex)
            {
                result.FailureReason = ex.Message;
                return result;
            }

            BinaryNode subGroups = response != null ? response.GetChild("sub_groups") : null;
            if (subGroups == null)
            {
                result.FailureReason = "no-sub-groups";
                return result;
            }

            foreach (CommunityLinkedGroupInfo info in ParseSubGroups(subGroups))
            {
                result.LinkedGroups.Add(info);
            }

            return result;
        }

        /// <summary>Parses Baileys-style <c>sub_groups</c> children into snapshots (test seam).</summary>
        public static IList<CommunityLinkedGroupInfo> ParseSubGroups(BinaryNode subGroups)
        {
            var list = new List<CommunityLinkedGroupInfo>();
            if (subGroups == null)
            {
                return list;
            }

            foreach (BinaryNode groupNode in subGroups.GetChildren("group"))
            {
                if (groupNode == null)
                {
                    continue;
                }

                string id = groupNode.GetAttribute("id") ?? groupNode.GetAttribute("jid");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                string groupJid = id.IndexOf('@') >= 0
                    ? id
                    : WA.JidEncode(id, WA.G_US);

                long creation = 0;
                long.TryParse(groupNode.GetAttribute("creation"), out creation);
                int size = 0;
                int.TryParse(groupNode.GetAttribute("size"), out size);

                string owner = groupNode.GetAttribute("creator") ?? groupNode.GetAttribute("s_o");
                if (!string.IsNullOrWhiteSpace(owner) && owner.IndexOf('@') < 0)
                {
                    owner = WA.JidEncode(owner, WA.S_WHATSAPP_NET);
                }

                list.Add(new CommunityLinkedGroupInfo
                {
                    Jid = groupJid,
                    Subject = groupNode.GetAttribute("subject") ?? string.Empty,
                    CreationUnixSeconds = creation,
                    Owner = owner,
                    Size = size
                });
            }

            return list;
        }
    }
}
