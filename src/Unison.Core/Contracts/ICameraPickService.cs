// =============================================================================
// ICameraPickService
//
// One-shot JPEG capture. Callers await PickJpegAsync; the Shell plugs a UI
// capture Func (CameraPickView) so Core never references WinRT / the view.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Shell-hosted camera pick. Returns JPEG bytes, or null when cancelled / busy / no UI.
    /// </summary>
    public interface ICameraPickService
    {
        /// <summary>
        /// Registers the Shell capture pipeline (open overlay, run <c>CameraPickView</c>, return bytes).
        /// Pass null (or call <see cref="DetachUiCapture"/>) on Unloaded.
        /// </summary>
        void AttachUiCapture(Func<Task<byte[]>> captureAsync);

        /// <summary>Clears the UI capture Func. In-flight pick completes with null.</summary>
        void DetachUiCapture();

        /// <summary>
        /// Opens the Shell camera via the plugged Func. Null when cancelled, busy, or no UI.
        /// </summary>
        Task<byte[]> PickJpegAsync();
    }
}
