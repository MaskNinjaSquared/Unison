// =============================================================================
// USyncQueryResult
//
// The parsed reply of a usync query: one entry per user, each carrying whatever
// the requested protocols managed to read.
//
// Values are kept boxed and keyed by protocol name because a single query can
// mix answer types - a bool for contact, a string for lid, a device list for
// devices. Callers pull out the one they asked for; the UseCase above turns that
// into something typed.
//
// Ports: rc14 USyncQueryResult in src/WAUSync/USyncQuery.ts
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Baileys.Protocol;

namespace Unison.Socket.USync
{
    /// <summary>One user's answer. <see cref="Id"/> is the JID the server echoed back.</summary>
    public sealed class USyncQueryResultEntry
    {
        public USyncQueryResultEntry(string id)
        {
            Id = id;
            Values = new Dictionary<string, object>(StringComparer.Ordinal);
        }

        public string Id { get; private set; }

        /// <summary>Answers keyed by protocol name, e.g. "contact" or "lid".</summary>
        public IDictionary<string, object> Values { get; private set; }

        /// <summary>
        /// The row exactly as it arrived. A protocol reduces its column to the one answer it is
        /// about - contact to "does this account exist" - so anything the server volunteered
        /// alongside it, like the display name or an avatar id, is only readable here.
        /// </summary>
        public BinaryNode Node { get; set; }

        public bool TryGet<T>(string protocol, out T value)
        {
            value = default(T);
            if (protocol == null || !Values.TryGetValue(protocol, out var raw) || !(raw is T))
            {
                return false;
            }

            value = (T)raw;
            return true;
        }

        public T Get<T>(string protocol)
        {
            T value;
            return TryGet(protocol, out value) ? value : default(T);
        }
    }

    public sealed class USyncQueryResult
    {
        public USyncQueryResult()
        {
            List = new List<USyncQueryResultEntry>();
            SideList = new List<USyncQueryResultEntry>();
        }

        /// <summary>Answers for the users that were asked about.</summary>
        public IList<USyncQueryResultEntry> List { get; private set; }

        /// <summary>
        /// Users the server volunteered alongside the answer. Baileys does not parse this yet
        /// either; the list is here so the shape stays recognisable.
        /// </summary>
        public IList<USyncQueryResultEntry> SideList { get; private set; }
    }
}
