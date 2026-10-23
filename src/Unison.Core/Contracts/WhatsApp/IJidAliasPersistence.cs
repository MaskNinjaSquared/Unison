using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Writes the LID/PN alias map now (not on the catalogue debounce). Used after a burst of
    /// new pairs so a killed process does not lose the table the follow-up just filled.
    /// </summary>
    public interface IJidAliasPersistence
    {
        Task PersistAliasesAsync(string reason);
    }
}
