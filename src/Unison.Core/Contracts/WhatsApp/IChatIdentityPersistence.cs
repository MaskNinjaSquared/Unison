namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Writes the chat catalogue, the name tables and the alias map now, not on the debounce
    /// timer. Used after a list fold so a killed process does not bring the folded rows back.
    /// </summary>
    public interface IChatIdentityPersistence
    {
        /// <summary>Fire-and-forget; failures are logged by the implementation.</summary>
        void PersistNow(string reason);
    }
}
