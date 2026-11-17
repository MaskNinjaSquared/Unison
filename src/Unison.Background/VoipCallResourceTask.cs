// =============================================================================
// VoipCallResourceTask
//
// Empty general background task required by VoipCallCoordinator.ReserveCallResourcesAsync.
// The system holds this registration while a VoIP call is active; no WA socket work here.
// =============================================================================
using Windows.ApplicationModel.Background;

namespace Unison.Background
{
    public sealed class VoipCallResourceTask : IBackgroundTask
    {
        public void Run(IBackgroundTaskInstance taskInstance)
        {
            // Resource reservation only — signaling stays on the chat socket / broker.
            BackgroundTaskDeferral deferral = taskInstance?.GetDeferral();
            try
            {
            }
            finally
            {
                deferral?.Complete();
            }
        }
    }
}
