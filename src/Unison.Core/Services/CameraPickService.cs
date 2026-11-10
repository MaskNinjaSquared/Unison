// =============================================================================
// CameraPickService
//
// Core-owned pick session: one UI Func, one in-flight Task. No WinRT.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;

namespace Unison.Core.Services
{
    /// <summary>Default <see cref="ICameraPickService"/> — Shell attaches the capture Func.</summary>
    public sealed class CameraPickService : ICameraPickService
    {
        private readonly object _gate = new object();
        private Func<Task<byte[]>> _uiCapture;
        private int _busy;
        private TaskCompletionSource<byte[]> _cancelBridge;

        public void AttachUiCapture(Func<Task<byte[]>> captureAsync)
        {
            lock (_gate)
            {
                _uiCapture = captureAsync;
            }
        }

        public void DetachUiCapture()
        {
            TaskCompletionSource<byte[]> bridge;
            lock (_gate)
            {
                _uiCapture = null;
                bridge = _cancelBridge;
                _cancelBridge = null;
            }

            bridge?.TrySetResult(null);
        }

        public async Task<byte[]> PickJpegAsync()
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                return null;
            }

            Func<Task<byte[]>> ui;
            TaskCompletionSource<byte[]> bridge;
            lock (_gate)
            {
                ui = _uiCapture;
                bridge = new TaskCompletionSource<byte[]>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _cancelBridge = bridge;
            }

            if (ui == null)
            {
                Interlocked.Exchange(ref _busy, 0);
                return null;
            }

            try
            {
                Task<byte[]> captureTask = ui();
                if (captureTask == null)
                {
                    return null;
                }

                // Preserve caller sync context (UI): ContentDialog / x:Bind after pick.
                Task completed = await Task.WhenAny(captureTask, bridge.Task).ConfigureAwait(true);
                if (completed == bridge.Task)
                {
                    return null;
                }

                return await captureTask.ConfigureAwait(true);
            }
            catch
            {
                return null;
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_cancelBridge, bridge))
                    {
                        _cancelBridge = null;
                    }
                }

                Interlocked.Exchange(ref _busy, 0);
            }
        }
    }
}
