// =============================================================================
// IUrgentDispatcher
//
// Same UI thread as IDispatcher, but at high priority. Live inbound strip
// updates use this so Normal-priority list reshuffles cannot starve the tip
// Move. The pump must not await this hop for stage=route (see MessageFacade).
// =============================================================================
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    public interface IUrgentDispatcher
    {
        Task RunUrgentAsync(
            Action action,
            [CallerMemberName] string caller = null,
            [CallerFilePath] string callerFile = null);
    }
}
