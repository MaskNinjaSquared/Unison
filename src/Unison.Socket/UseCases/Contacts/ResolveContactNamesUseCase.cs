// =============================================================================
// ResolveContactNamesUseCase
//
// Asks the server who a set of numbers belong to.
//
// This is the last outbound action that had no home outside the god class. There
// it is roughly three hundred lines, because the query, the parsing, and what the
// app does with the answer - repairing our own identity, merging duplicated
// chats, kicking off avatar fetches - are written as one function. Only the first
// two belong to the protocol, and that is all this use case does: it returns what
// the server said and forms no opinion about it.
//
// It also asks for the lid column, which the hand-rolled query never did. The
// mapping is free, arrives in the same round trip, and is the piece that stops
// the same person appearing twice in the chat list.
//
// Ports: rc14 src/WAUSync (the contact and lid protocols); the name column has no
// rc14 caller because Baileys learns push names from message stanzas instead.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Session;
using Unison.Socket.UseCases.USync;
using Unison.Socket.USync;
using Unison.Socket.WABinary;

namespace Unison.Socket.UseCases.Contacts
{
    /// <summary>What the server knows about one contact. Every field may be absent.</summary>
    public sealed class ResolvedContact
    {
        public ResolvedContact(string jid)
        {
            Jid = jid;
        }

        /// <summary>The JID the server echoed back, which is the authoritative one for this number.</summary>
        public string Jid { get; private set; }

        /// <summary>The name the contact chose for themselves. Null when they set none.</summary>
        public string Name { get; set; }

        /// <summary>Their LID, when the reply carried the mapping.</summary>
        public string Lid { get; set; }

        /// <summary>
        /// The id of their current avatar. Not a URL: it says the picture changed, and the URL
        /// still has to be fetched separately.
        /// </summary>
        public string PictureId { get; set; }

        /// <summary>False when the number turned out to have no account.</summary>
        public bool Exists { get; set; }
    }

    public sealed class ResolveContactNamesUseCase
    {
        private readonly ExecuteUSyncQueryUseCase _usync;

        public ResolveContactNamesUseCase(ConnectionHandler connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            _usync = new ExecuteUSyncQueryUseCase(connection);
        }

        /// <summary>
        /// Looks up a batch in one round trip. Inputs may be JIDs or bare numbers; groups,
        /// newsletters and broadcasts are dropped, because this endpoint answers about people.
        /// </summary>
        /// <param name="context">
        /// "interactive" when someone is waiting for the answer, "background" otherwise. The
        /// server weighs the two differently, and a background pass that claims to be
        /// interactive is how a refresh ends up competing with the message path.
        /// </param>
        public async Task<IReadOnlyList<ResolvedContact>> ExecuteAsync(
            IEnumerable<string> jids,
            string context = "interactive",
            TimeSpan? timeout = null)
        {
            var results = new List<ResolvedContact>();
            if (jids == null)
            {
                return results;
            }

            var query = new USyncQuery()
                .WithContext(context)
                .WithContactProtocol()
                .WithLidProtocol();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var jid in jids)
            {
                var phone = ToInternationalNumber(jid);
                if (phone == null || !seen.Add(phone))
                {
                    continue;
                }

                query.WithUser(new USyncUser().WithPhone(phone));
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
                var contact = new ResolvedContact(entry.Id);

                bool exists;
                contact.Exists = !entry.TryGet("contact", out exists) || exists;

                string lid;
                if (entry.TryGet("lid", out lid) && !string.IsNullOrEmpty(lid))
                {
                    // The column often carries the bare user, not a full address. We asked with
                    // a number, so the counterpart is a LID.
                    contact.Lid = lid.IndexOf('@') >= 0 ? lid : lid + "@" + JidUtils.ServerLid;
                }

                if (entry.Node != null)
                {
                    contact.Name = ReadName(entry.Node.GetChild("contact"));

                    var picture = entry.Node.GetChild("picture");
                    if (picture != null)
                    {
                        contact.PictureId = picture.GetAttribute("id");
                    }
                }

                results.Add(contact);
            }

            return results;
        }

        /// <summary>
        /// The display name, wherever the server decided to put it. It has used all three of
        /// these, and a reply that carries the name only as text is not rare enough to ignore.
        /// </summary>
        private static string ReadName(BinaryNode contact)
        {
            if (contact == null)
            {
                return null;
            }

            var name = contact.GetAttribute("notify");
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }

            name = contact.GetAttribute("name");
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }

            var text = contact.GetContentString();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        /// <summary>
        /// The number to ask about, or null when this JID is not a person. A LID is rejected the
        /// same way rc14 rejects one here: the server answers this query by number, and asking
        /// with a LID returns nothing rather than an error, which reads as "no such account".
        /// </summary>
        private static string ToInternationalNumber(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid) ||
                JidUtils.IsGroup(jid) ||
                JidUtils.IsAnyLid(jid) ||
                JidUtils.IsBroadcast(jid) ||
                JidUtils.IsNewsletter(jid))
            {
                return null;
            }

            var value = jid.Trim();

            var at = value.IndexOf('@');
            if (at >= 0)
            {
                value = value.Substring(0, at);
            }

            var colon = value.IndexOf(':');
            if (colon >= 0)
            {
                value = value.Substring(0, colon);
            }

            value = value.Replace("+", string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty);

            return value.Length == 0 ? null : "+" + value;
        }
    }
}
