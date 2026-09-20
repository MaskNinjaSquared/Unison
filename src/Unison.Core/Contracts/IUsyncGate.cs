using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// One at a time across every usync-style directory query: contact name resolution and profile
    /// picture lookups both go through here.
    ///
    /// They share a gate because they share a server surface that answers slowly and rate-limits.
    /// Two of these in flight is not twice as fast, it is one of them failing, so the gate is not
    /// a lock protecting shared memory — nothing here is shared — it is a queue of one in front of
    /// the wire. That is why it outlived the class it started in: contacts and avatars are moving
    /// to different owners, and both still have to take a number.
    /// </summary>
    public interface IUsyncGate
    {
        /// <summary>
        /// Waits for the wire, then returns the lease to dispose when done. Cancelling while
        /// waiting throws and takes nothing, so a cancelled caller never has to release.
        /// </summary>
        Task<IDisposable> AcquireAsync(CancellationToken token = default(CancellationToken));
    }
}
