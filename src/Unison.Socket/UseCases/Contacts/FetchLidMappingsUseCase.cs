// =============================================================================
// FetchLidMappingsUseCase
//
// Asks the server for the LID behind each phone number.
//
// This is the only source of a mapping the account has never seen, and it is
// what LidMappingStore calls when cache and storage both come up empty. It runs
// in the "background" context because it is plumbing, not something the user
// asked for, and the server treats the two differently under load.
//
// Ports: rc14 pnFromLIDUSync in src/Socket/socket.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Socket.Session;
using Unison.Socket.Signal;
using Unison.Socket.UseCases.USync;
using Unison.Socket.USync;
using Unison.Socket.WABinary;

namespace Unison.Socket.UseCases.Contacts
{
    public sealed class FetchLidMappingsUseCase
    {
        private readonly ExecuteUSyncQueryUseCase _usync;

        public FetchLidMappingsUseCase(ConnectionHandler connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            _usync = new ExecuteUSyncQueryUseCase(connection);
        }

        /// <summary>
        /// Resolves phone-number JIDs to their LIDs. Anything already in LID form is skipped -
        /// this query only travels in one direction - and numbers the server has no mapping for
        /// are simply absent from the result.
        /// </summary>
        public async Task<IReadOnlyList<LidMapping>> ExecuteAsync(
            IEnumerable<string> phoneNumberJids,
            TimeSpan? timeout = null)
        {
            var mappings = new List<LidMapping>();
            if (phoneNumberJids == null)
            {
                return mappings;
            }

            var query = new USyncQuery().WithLidProtocol().WithContext("background");

            foreach (var jid in phoneNumberJids)
            {
                if (string.IsNullOrWhiteSpace(jid) || JidUtils.IsAnyLid(jid))
                {
                    continue;
                }

                query.WithUser(new USyncUser().WithId(jid));
            }

            if (query.Users.Count == 0)
            {
                return mappings;
            }

            var reply = await _usync.ExecuteAsync(query, timeout).ConfigureAwait(false);
            if (reply == null)
            {
                return mappings;
            }

            foreach (var entry in reply.List)
            {
                string lid;
                if (entry.TryGet("lid", out lid) && !string.IsNullOrEmpty(lid))
                {
                    mappings.Add(new LidMapping(lid, entry.Id));
                }
            }

            return mappings;
        }
    }
}
