using System.Collections.Generic;
using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Alias table writes the façade cannot own yet: file LID/PN pairs (raises follow-up) and
    /// flush the map now so a killed process does not lose a burst of new pairs.
    /// </summary>
    public interface IJidAliasPersistence
    {
        /// <summary>
        /// Files each well-formed LID→PN pair both ways. Notifies ContactFacade via
        /// <c>NotifyJidAliasResolved</c> per new pair so it can coalesce persist / dedupe /
        /// avatar retry.
        /// </summary>
        void RegisterAliasMappings(IEnumerable<KeyValuePair<string, string>> lidToPn, string source);

        Task PersistAliasesAsync(string reason);
    }
}
