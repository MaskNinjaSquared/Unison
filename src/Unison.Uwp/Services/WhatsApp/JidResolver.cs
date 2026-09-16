using System;
using System.Collections.Generic;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// Reads the alias table the live session builds.
    /// </summary>
    /// <remarks>
    /// A wrapper rather than the table itself: the pairs are learned while decrypting messages and
    /// applying history, so the client still owns them. What this buys now is the seam - callers
    /// stop naming the client to ask a question about a JID, and the table can move here later
    /// without touching any of them again.
    /// </remarks>
    internal sealed class JidResolver : IJidResolver
    {
        private readonly WhatsAppService _client;

        public JidResolver(WhatsAppService client)
        {
            if (client == null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            _client = client;
        }

        public Profile Self => _client.CurrentProfile;

        public string GetCanonicalJid(string jid)
        {
            return _client.GetCanonicalJid(jid);
        }

        public bool TryGetAlias(string jid, out string alias)
        {
            alias = null;
            if (string.IsNullOrWhiteSpace(jid))
            {
                return false;
            }

            IReadOnlyDictionary<string, string> map = _client.JidAlias;
            return map != null && map.TryGetValue(jid, out alias);
        }
    }
}
