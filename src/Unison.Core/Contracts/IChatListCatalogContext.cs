// =============================================================================
// IChatListCatalogContext
//
// Singleton catalogue projection for every chat list (Active / Channels /
// Archived). ViewModels are transient and only consult this — they do not
// rebuild canonical scratch or re-walk membership rules alone.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    public interface IChatListCatalogContext
    {
        /// <summary>Rows that belong under <paramref name="scope"/> (live catalogue order).</summary>
        IReadOnlyList<ChatItem> ForScope(ChatListScope scope);

        /// <summary>Peek canonical (no LidMapping promote / Changed).</summary>
        string PeekCanonical(string jid);

        /// <summary>
        /// Canonical→row map for one Active projection pass. Rebuilt when the catalogue
        /// or alias generation changes; shared across all list VMs.
        /// </summary>
        IReadOnlyDictionary<string, ChatItem> GetCanonicalMap();

        /// <summary>O(1) lookup via <see cref="GetCanonicalMap"/>.</summary>
        ChatItem ResolveByCanonical(string jid);
    }
}
