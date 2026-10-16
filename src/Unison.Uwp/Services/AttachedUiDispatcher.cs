using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Windows.ApplicationModel.Core;
using Windows.UI.Core;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// The UI thread the client and its façades marshal list and row edits onto.
    /// </summary>
    /// <remarks>
    /// App attaches the root frame's dispatcher once the container is built. Until one is known,
    /// work is dropped and recorded rather than run on a worker thread against a bound list —
    /// unlike <see cref="DispatcherService"/> and <see cref="UiThreadDispatcherService"/>, which
    /// run inline when they find no dispatcher.
    /// </remarks>
    public sealed class AttachedUiDispatcher : IDispatcher
    {
        private readonly IRuntimeDiagnostics _diagnostics;
        private CoreDispatcher _attached;

        public AttachedUiDispatcher(IRuntimeDiagnostics diagnostics)
        {
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        }

        public void Attach(CoreDispatcher dispatcher)
        {
            if (dispatcher != null)
            {
                _attached = dispatcher;
            }
        }

        /// <summary>The attached dispatcher, else the main view's, else null.</summary>
        public CoreDispatcher Current
        {
            get
            {
                if (_attached != null)
                {
                    return _attached;
                }

                try
                {
                    return CoreApplication.MainView?.CoreWindow?.Dispatcher;
                }
                catch
                {
                    return null;
                }
            }
        }

        public async Task RunAsync(Action action)
        {
            if (action == null)
            {
                return;
            }

            CoreDispatcher dispatcher = Current;
            if (dispatcher == null)
            {
                _diagnostics.Write("runtime", "ui-dispatch-skipped", "kind=sync; reason=no-dispatcher");
                return;
            }

            if (dispatcher.HasThreadAccess)
            {
                action();
                return;
            }

            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action());
        }

        /// <summary>Runs async UI work at low priority and waits for it to finish.</summary>
        public async Task RunTaskAsync(Func<Task> action)
        {
            if (action == null)
            {
                return;
            }

            CoreDispatcher dispatcher = Current;
            if (dispatcher == null)
            {
                _diagnostics.Write("runtime", "ui-dispatch-skipped", "kind=async; reason=no-dispatcher");
                return;
            }

            if (dispatcher.HasThreadAccess)
            {
                await action();
                return;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await dispatcher.RunAsync(CoreDispatcherPriority.Low, async () =>
            {
                try
                {
                    await action();
                    completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
            await completion.Task;
        }

        /// <summary>Queues work without waiting; dropped silently when no dispatcher is known.</summary>
        public void Post(Action action)
        {
            if (action == null)
            {
                return;
            }

            CoreDispatcher dispatcher = Current;
            if (dispatcher == null)
            {
                return;
            }

            if (dispatcher.HasThreadAccess)
            {
                action();
                return;
            }

            _ = dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action());
        }
    }
}
