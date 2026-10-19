using System;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// Answers questions about a JID from the alias table the live session builds.
    /// </summary>
    /// <remarks>
    /// The table used to live inside the client and this was a wrapper over it. Since phase 3.7
    /// the table stands on its own and this reads it directly; since phase 4 so does
    /// <see cref="Self"/>, which is the table's bound account rather than the client's profile.
    /// </remarks>
    internal sealed class JidResolver : IJidResolver
    {
        private readonly JidAliasTable _aliases;

        public JidResolver(JidAliasTable aliases)
        {
            _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
        }

        public Profile Self => _aliases.Self;

        public string GetCanonicalJid(string jid) => _aliases.GetCanonicalJid(jid);

        public bool IsSelfLinked(string jid) => _aliases.IsSelfLinked(jid);

        public bool TryGetAlias(string jid, out string alias)
        {
            alias = null;
            if (string.IsNullOrWhiteSpace(jid))
            {
                return false;
            }

            return _aliases.TryGetValue(JidHelper.Normalize(jid), out alias);
        }
    }
}
