using System;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// Answers questions about a JID from the alias table the live session builds.
    /// </summary>
    /// <remarks>
    /// The table used to live inside the client and this was a wrapper over it. Since phase 3.7
    /// the table stands on its own and this reads it directly, so asking what a JID means no
    /// longer reaches through the client at all. Only <see cref="Self"/> still does, because a
    /// profile is a display name and a picture rather than an address.
    /// </remarks>
    internal sealed class JidResolver : IJidResolver
    {
        private readonly JidAliasTable _aliases;
        private readonly IWhatsAppService _session;

        public JidResolver(JidAliasTable aliases, IWhatsAppService session)
        {
            _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public Profile Self => _session.CurrentProfile;

        public string GetCanonicalJid(string jid) => _aliases.GetCanonicalJid(jid);

        public bool IsSelfLinked(string jid) => _aliases.IsSelfLinked(jid);

        public bool TryGetAlias(string jid, out string alias)
        {
            alias = null;
            if (string.IsNullOrWhiteSpace(jid))
            {
                return false;
            }

            return _aliases.TryGetValue(jid, out alias);
        }
    }
}
