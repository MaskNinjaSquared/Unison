namespace Unison.Core.Contracts
{
    /// <summary>
    /// Localized string lookup. UWP implements via ResourceLoader / .resw.
    /// </summary>
    public interface IStringResources
    {
        /// <param name="fallback">Used when the key is missing; when null, the key (or empty) is returned.</param>
        string Get(string key, string fallback = null);
    }
}
