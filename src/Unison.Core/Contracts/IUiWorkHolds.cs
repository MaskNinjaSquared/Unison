using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Helpers.Ui;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// The only place deferrable UI work waits for its gates. New wait loops go through here
    /// (docs/wiki/Ui-Work-Scheduling.md).
    /// </summary>
    public interface IUiWorkHolds
    {
        /// <summary>The gate holding <paramref name="kind"/> now, or null.</summary>
        string Describe(UiWorkKind kind);

        /// <summary>
        /// One multi-slice pass. A gate that timed out once in the run is not waited on again,
        /// so a stuck gate costs one budget per run instead of one per slice.
        /// </summary>
        IUiWaitRun BeginRun(string reason);
    }

    public interface IUiWaitRun
    {
        Task<UiWaitOutcome> WaitAsync(UiWorkKind kind, CancellationToken cancellationToken = default(CancellationToken));
    }
}
