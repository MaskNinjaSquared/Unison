// =============================================================================
// OnWhatsAppUseCase
//
// Answers whether a phone number has a WhatsApp account, and under which JID.
//
// Unison has never had this: the new-chat search resolves a number by asking for
// its contact name and treating silence as "no account", which is why searching
// for a real number sometimes finds nothing. The server has a direct answer, and
// this is the query that asks for it.
//
// Ports: rc14 onWhatsApp in src/Socket/socket.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Socket.Session;
using Unison.Socket.UseCases.USync;
using Unison.Socket.USync;
using Unison.Socket.WABinary;

namespace Unison.Socket.UseCases.Contacts
{
    public sealed class OnWhatsAppResult
    {
        public OnWhatsAppResult(string jid, bool exists)
        {
            Jid = jid;
            Exists = exists;
        }

        /// <summary>The JID the server assigned to the number, valid only when <see cref="Exists"/> is true.</summary>
        public string Jid { get; private set; }

        public bool Exists { get; private set; }
    }

    public sealed class OnWhatsAppUseCase
    {
        private readonly ExecuteUSyncQueryUseCase _usync;

        public OnWhatsAppUseCase(ConnectionHandler connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            _usync = new ExecuteUSyncQueryUseCase(connection);
        }

        /// <summary>
        /// Looks up one or more numbers in a single round trip. Numbers may be given raw, with a
        /// plus, or as a JID; LIDs are rejected because the server answers this query by number.
        /// Returns an entry only for the numbers it got an answer about.
        /// </summary>
        public async Task<IReadOnlyList<OnWhatsAppResult>> ExecuteAsync(
            IEnumerable<string> phoneNumbers,
            TimeSpan? timeout = null)
        {
            var results = new List<OnWhatsAppResult>();
            if (phoneNumbers == null)
            {
                return results;
            }

            var query = new USyncQuery();
            var contactRequested = false;

            foreach (var number in phoneNumbers)
            {
                if (string.IsNullOrWhiteSpace(number) || JidUtils.IsAnyLid(number))
                {
                    continue;
                }

                if (!contactRequested)
                {
                    query.WithContactProtocol();
                    contactRequested = true;
                }

                query.WithUser(new USyncUser().WithPhone(ToInternationalNumber(number)));
            }

            if (query.Users.Count == 0)
            {
                return results;
            }

            var reply = await _usync.ExecuteAsync(query, timeout).ConfigureAwait(false);
            if (reply == null)
            {
                return results;
            }

            foreach (var entry in reply.List)
            {
                bool exists;
                if (entry.TryGet("contact", out exists))
                {
                    results.Add(new OnWhatsAppResult(entry.Id, exists));
                }
            }

            return results;
        }

        /// <summary>Strips a JID wrapper and any device suffix, leaving "+" and digits.</summary>
        private static string ToInternationalNumber(string value)
        {
            var trimmed = value.Trim().Replace("+", string.Empty);

            var at = trimmed.IndexOf('@');
            if (at >= 0)
            {
                trimmed = trimmed.Substring(0, at);
            }

            var colon = trimmed.IndexOf(':');
            if (colon >= 0)
            {
                trimmed = trimmed.Substring(0, colon);
            }

            return "+" + trimmed;
        }
    }
}
