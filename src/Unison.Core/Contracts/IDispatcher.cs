using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    public interface IDispatcher
    {
        /// <remarks>
        /// Callers never pass <paramref name="caller"/> / <paramref name="callerFile"/>; the UI
        /// stall watchdog names the hop from them (.NET Native strips lambda metadata).
        /// </remarks>
        Task RunAsync(
            Action action,
            [CallerMemberName] string caller = null,
            [CallerFilePath] string callerFile = null);
    }
}
