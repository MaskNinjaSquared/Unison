using System.Collections.Generic;
using Unison.Baileys.Client;

namespace Unison.Uwp.Client
{
    /// <summary>
    /// Adapts SessionLogger to the Baileys IProtocolLogger contract.
    /// </summary>
    public sealed class ProtocolLoggerAdapter : IProtocolLogger
    {
        public void LogKeyInfo(string title, Dictionary<string, string> values)
        {
            SessionLogger.Instance.LogKeyInfo(title, values);
        }
    }
}
