// =============================================================================
// ILowPriorityDispatcher
//
// Same UI thread as IDispatcher, but behind input, paint and Normal hops.
// Background row projections (roster flush) use this so a burst of members
// never delays a tap or the open-chat timeline.
// =============================================================================
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    public interface ILowPriorityDispatcher
    {
        Task RunLowAsync(
            Action action,
            [CallerMemberName] string caller = null,
            [CallerFilePath] string callerFile = null);
    }
}
