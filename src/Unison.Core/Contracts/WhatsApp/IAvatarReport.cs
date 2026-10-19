using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>Tells the rest of the app that a chat picture landed on disk.</summary>
    public interface IAvatarReport
    {
        void ReportCached(string jid, string localUri);
    }
}
