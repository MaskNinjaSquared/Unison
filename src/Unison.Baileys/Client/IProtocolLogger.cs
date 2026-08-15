using System.Collections.Generic;

namespace Unison.Baileys.Client
{
    /// <summary>
    /// Abstracts diagnostic key/state logging for protocol handlers.
    /// </summary>
    public interface IProtocolLogger
    {
        void LogKeyInfo(string title, Dictionary<string, string> values);
    }
}
