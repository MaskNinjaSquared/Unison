using System;
using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// What restoring a group roster still needs from the compatibility client.
    /// </summary>
    /// <remarks>
    /// Two things, both of which the client owns for reasons unrelated to groups. The UI
    /// dispatcher it marshals onto is attached after the container is built, and a pass that
    /// arrives before then is dropped rather than run on a worker thread against a bound list.
    /// A member's label comes from the name caches and the self-chat wording (phase 3.6 / 3.9).
    /// </remarks>
    public interface IGroupRosterHost
    {
        /// <summary>Runs on the UI thread, or not at all when no dispatcher is attached yet.</summary>
        Task RunOnUiThreadAsync(Action action);

        /// <summary>The label a member shows in the roster. Never null; falls back to the JID.</summary>
        string ResolveMemberDisplayName(string jid);
    }
}
