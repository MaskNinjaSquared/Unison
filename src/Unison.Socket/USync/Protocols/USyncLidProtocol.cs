// =============================================================================
// USyncLidProtocol
//
// The usync column that answers "what is this number's LID".
//
// It is the only way to learn a mapping the account has never encountered, and
// it is what LidMappingStore falls back to when neither its cache nor its
// storage knows an answer. The reply carries the LID in a "val" attribute.
//
// Ports: rc14 src/WAUSync/Protocols/UsyncLIDProtocol.ts
// =============================================================================
using System.Collections.Generic;
using Unison.Baileys.Protocol;

namespace Unison.Socket.USync.Protocols
{
    public sealed class USyncLidProtocol : IUSyncProtocol
    {
        public string Name
        {
            get { return "lid"; }
        }

        public BinaryNode GetQueryElement()
        {
            return new BinaryNode("lid");
        }

        /// <summary>Only needed when asking the reverse question, with a LID already in hand.</summary>
        public BinaryNode GetUserElement(USyncUser user)
        {
            if (user == null || string.IsNullOrEmpty(user.Lid))
            {
                return null;
            }

            return new BinaryNode("lid", new Dictionary<string, string> { { "jid", user.Lid } });
        }

        public object Parse(BinaryNode node)
        {
            if (node == null || node.Tag != Name)
            {
                return null;
            }

            var value = node.GetAttribute("val");
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
