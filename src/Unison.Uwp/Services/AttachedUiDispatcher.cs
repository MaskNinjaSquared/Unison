using System;
using System.Runtime.CompilerServices;
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
    public sealed class AttachedUiDispatcher : IDispatcher, IUrgentDispatcher, ILowPriorityDispatcher
    {
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly UiStallWatchdog _watchdog;
        private CoreDispatcher _attached;

        public AttachedUiDispatcher(IRuntimeDiagnostics diagnostics, UiStallWatchdog watchdog)
        {
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _watchdog = watchdog ?? throw new ArgumentNullException(nameof(watchdog));
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

        public Task RunAsync(
            Action action,
            [CallerMemberName] string caller = null,
            [CallerFilePath] string callerFile = null) =>
            RunCoreAsync(action, CoreDispatcherPriority.Normal, "sync", caller, callerFile);

        /// <inheritdoc />
        public Task RunUrgentAsync(
            Action action,
            [CallerMemberName] string caller = null,
            [CallerFilePath] string callerFile = null) =>
            RunCoreAsync(action, CoreDispatcherPriority.High, "urgent", caller, callerFile);

        /// <inheritdoc />
        public Task RunLowAsync(
            Action action,
            [CallerMemberName] string caller = null,
            [CallerFilePath] string callerFile = null) =>
            RunCoreAsync(action, CoreDispatcherPriority.Low, "low", caller, callerFile);

        private async Task RunCoreAsync(
            Action action,
            CoreDispatcherPriority priority,
            string kind,
            string caller,
            string callerFile)
        {
            if (action == null)
            {
                return;
            }

            CoreDispatcher dispatcher = Current;
            if (dispatcher == null)
            {
                _diagnostics.Write(
                    "runtime",
                    "ui-dispatch-skipped",
                    "kind=" + kind + "; reason=no-dispatcher");
                return;
            }

            if (dispatcher.HasThreadAccess)
            {
                _watchdog.Run(action, kind, caller, callerFile);
                return;
            }

            await dispatcher.RunAsync(priority, () => _watchdog.Run(action, kind, caller, callerFile));
        }

        /// <summary>Runs async UI work at low priority and waits for it to finish.</summary>
        public async Task RunTaskAsync(
            Func<Task> action,
            [CallerMemberName] string caller = null,
            [CallerFilePath] string callerFile = null)
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
                await StartTracked(action, caller, callerFile);
                return;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await dispatcher.RunAsync(CoreDispatcherPriority.Low, async () =>
            {
                try
                {
                    await StartTracked(action, caller, callerFile);
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
        public void Post(
            Action action,
            [CallerMemberName] string caller = null,
            [CallerFilePath] string callerFile = null)
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
                _watchdog.Run(action, "post", caller, callerFile);
                return;
            }

            _ = dispatcher.RunAsync(
                CoreDispatcherPriority.Normal,
                () => _watchdog.Run(action, "post", caller, callerFile));
        }

        /// <summary>Tracks only the synchronous slice; awaits inside resume untracked.</summary>
        private Task StartTracked(Func<Task> action, string caller, string callerFile)
        {
            object token = _watchdog.Enter("async", caller, callerFile);
            try
            {
                return action() ?? Task.CompletedTask;
            }
            finally
            {
                _watchdog.Exit(token);
            }
        }
    }
}
