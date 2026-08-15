// =============================================================================
// USyncContactProtocol
//
// The usync column that answers "does this number have WhatsApp".
//
// The answer is a single attribute: type="in" means the account exists and is
// reachable. Anything else - a missing attribute, an error child - means no.
// This is the check the current code never implemented, which is why searching
// for a number today means asking for its name and hoping something comes back.
//
// Ports: rc14 src/WAUSync/Protocols/USyncContactProtocol.ts
// =============================================================================
using System.Collections.Generic;
using Unison.Baileys.Protocol;

namespace Unison.Socket.USync.Protocols
{
    public sealed class USyncContactProtocol : IUSyncProtocol
    {
        public string Name
        {
            get { return "contact"; }
        }

        public BinaryNode GetQueryElement()
        {
            return new BinaryNode("contact");
        }

        public BinaryNode GetUserElement(USyncUser user)
        {
            if (user == null)
            {
                return new BinaryNode("contact");
            }

            if (!string.IsNullOrEmpty(user.Phone))
            {
                return new BinaryNode("contact", null, user.Phone);
            }

            if (!string.IsNullOrEmpty(user.Username))
            {
                var attrs = new Dictionary<string, string> { { "username", user.Username } };
                if (!string.IsNullOrEmpty(user.UsernameKey))
                {
                    attrs["pin"] = user.UsernameKey;
                }

                if (!string.IsNullOrEmpty(user.Lid))
                {
                    attrs["lid"] = user.Lid;
                }

                return new BinaryNode("contact", attrs);
            }

            if (!string.IsNullOrEmpty(user.Type))
            {
                return new BinaryNode("contact", new Dictionary<string, string> { { "type", user.Type } });
            }

            return new BinaryNode("contact");
        }

        /// <summary>Boxed bool: true when the account exists. An error child is reported as "does not exist".</summary>
        public object Parse(BinaryNode node)
        {
            if (node == null || node.Tag != Name)
            {
                return false;
            }

            if (node.GetChild("error") != null)
            {
                return false;
            }

            return node.GetAttribute("type") == "in";
        }
    }
}
