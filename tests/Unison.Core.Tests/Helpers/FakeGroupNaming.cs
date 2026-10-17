using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.Tests.Helpers
{
    /// <summary>Names from dictionaries; the subject cache is a real table the applier writes.</summary>
    internal sealed class FakeGroupNaming : IGroupNaming
    {
        public Dictionary<string, string> MemberNames { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> ChatNames { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public DisplayNameTable CachedNames { get; } = new DisplayNameTable();

        public string ResolveMemberDisplayName(string jid)
        {
            string name;
            return MemberNames.TryGetValue(jid, out name) ? name : jid;
        }

        public string ResolveChatDisplayName(string jid)
        {
            string name;
            if (ChatNames.TryGetValue(jid, out name))
            {
                return name;
            }

            return CachedNames.TryGetValue(jid, out name) ? name : jid;
        }

        public bool IsMeaningfulGroupLabel(string label, string groupJid) =>
            !GroupIdPlaceholder.IsIdPlaceholder(label, groupJid);
    }
}
