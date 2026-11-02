// =============================================================================
// JoinGroupUseCase
//
// Ask the server to add this account to a group (community subgroup can-join).
// Prefer invite accept when a code is known; otherwise IQ set with join + self.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Session;

namespace Unison.Socket.UseCases.Groups
{
    public sealed class JoinGroupResult
    {
        public bool Succeeded { get; set; }

        public string GroupJid { get; set; }

        public string FailureReason { get; set; }
    }

    public sealed class JoinGroupUseCase
    {
        private readonly ConnectionHandler _connection;
        private readonly GroupInviteUseCase _invites;

        public JoinGroupUseCase(ConnectionHandler connection, GroupInviteUseCase invites)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _invites = invites ?? throw new ArgumentNullException(nameof(invites));
        }

        /// <summary>Join via invite code (Baileys groupAcceptInvite).</summary>
        public async Task<JoinGroupResult> AcceptInviteAsync(string code, TimeSpan? timeout = null)
        {
            var result = new JoinGroupResult();
            if (string.IsNullOrWhiteSpace(code))
            {
                result.FailureReason = "empty-code";
                return result;
            }

            try
            {
                string jid = await _invites.AcceptAsync(code, timeout).ConfigureAwait(false);
                result.GroupJid = jid;
                result.Succeeded = !string.IsNullOrWhiteSpace(jid);
                if (!result.Succeeded)
                {
                    result.FailureReason = "no-jid";
                }
            }
            catch (Exception ex)
            {
                result.FailureReason = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Direct join IQ for a linked community subgroup (open membership).
        /// </summary>
        public async Task<JoinGroupResult> JoinByJidAsync(
            string groupJid,
            string selfJid,
            TimeSpan? timeout = null)
        {
            var result = new JoinGroupResult { GroupJid = groupJid };
            if (string.IsNullOrWhiteSpace(groupJid))
            {
                result.FailureReason = "empty-jid";
                return result;
            }

            try
            {
                var content = new List<BinaryNode>();
                if (!string.IsNullOrWhiteSpace(selfJid))
                {
                    content.Add(new BinaryNode(
                        "add",
                        null,
                        GroupQuery.Participants(new[] { selfJid })));
                }
                else
                {
                    content.Add(new BinaryNode("join"));
                }

                BinaryNode response = await GroupQuery
                    .ExecuteAsync(_connection, groupJid, "set", content, timeout)
                    .ConfigureAwait(false);

                result.Succeeded = response != null;
                if (!result.Succeeded)
                {
                    result.FailureReason = "empty-response";
                }
            }
            catch (WaConnectionException ex)
            {
                result.FailureReason = ((int)ex.Reason).ToString();
            }
            catch (Exception ex)
            {
                result.FailureReason = ex.Message;
            }

            return result;
        }
    }
}