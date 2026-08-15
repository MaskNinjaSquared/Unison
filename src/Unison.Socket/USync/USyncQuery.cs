// =============================================================================
// USyncQuery
//
// Builds a usync request and reads its reply.
//
// usync is the server's general-purpose "tell me about these users" endpoint,
// and every contact feature goes through it: does this number have WhatsApp,
// what is this number's LID, which devices does it have. Today Unison hand-rolls
// one such node for contacts and another for devices, which is why the contact
// path never learned to ask for the lid column. Here the columns are composable,
// so asking for one more is one more call.
//
// Ports: rc14 src/WAUSync/USyncQuery.ts
// =============================================================================
using System.Collections.Generic;
using Unison.Baileys.Protocol;
using Unison.Socket.USync.Protocols;

namespace Unison.Socket.USync
{
    public sealed class USyncQuery
    {
        public USyncQuery()
        {
            Protocols = new List<IUSyncProtocol>();
            Users = new List<USyncUser>();
            Context = "interactive";
            Mode = "query";
        }

        public IList<IUSyncProtocol> Protocols { get; private set; }

        public IList<USyncUser> Users { get; private set; }

        /// <summary>Why we are asking: "interactive" for user-visible work, "background" otherwise.</summary>
        public string Context { get; private set; }

        public string Mode { get; private set; }

        public USyncQuery WithMode(string mode)
        {
            Mode = mode;
            return this;
        }

        public USyncQuery WithContext(string context)
        {
            Context = context;
            return this;
        }

        public USyncQuery WithUser(USyncUser user)
        {
            if (user != null)
            {
                Users.Add(user);
            }

            return this;
        }

        public USyncQuery WithProtocol(IUSyncProtocol protocol)
        {
            if (protocol != null)
            {
                Protocols.Add(protocol);
            }

            return this;
        }

        public USyncQuery WithContactProtocol()
        {
            return WithProtocol(new USyncContactProtocol());
        }

        public USyncQuery WithLidProtocol()
        {
            return WithProtocol(new USyncLidProtocol());
        }

        public USyncQuery WithDeviceProtocol()
        {
            return WithProtocol(new USyncDeviceProtocol());
        }

        /// <summary>
        /// Reads a usync reply. A reply that is not a result - an error, or a node from another
        /// exchange - produces null rather than an exception, because a failed lookup is a normal
        /// outcome for every caller of this class.
        /// </summary>
        public USyncQueryResult ParseResult(BinaryNode result)
        {
            if (result == null || result.GetAttribute("type") != "result")
            {
                return null;
            }

            var parsers = new Dictionary<string, IUSyncProtocol>();
            foreach (var protocol in Protocols)
            {
                parsers[protocol.Name] = protocol;
            }

            var parsed = new USyncQueryResult();

            var usync = result.GetChild("usync");
            var list = usync != null ? usync.GetChild("list") : null;
            if (list == null)
            {
                return parsed;
            }

            foreach (var userNode in list.GetChildren("user"))
            {
                var id = userNode.GetAttribute("jid");
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                var entry = new USyncQueryResultEntry(id) { Node = userNode };
                foreach (var child in userNode.GetAllChildren())
                {
                    IUSyncProtocol protocol;
                    if (!parsers.TryGetValue(child.Tag, out protocol))
                    {
                        continue;
                    }

                    var value = protocol.Parse(child);
                    if (value != null)
                    {
                        entry.Values[child.Tag] = value;
                    }
                }

                parsed.List.Add(entry);
            }

            return parsed;
        }
    }
}
