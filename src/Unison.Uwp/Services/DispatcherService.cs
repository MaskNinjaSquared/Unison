using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Unison.Uwp.Services
{
    public class DispatcherService : IDispatcher
    {
        private readonly CoreDispatcher _dispatcher;
        private readonly UiStallWatchdog _watchdog;

        public DispatcherService(UiStallWatchdog watchdog)
        {
            _watchdog = watchdog ?? throw new ArgumentNullException(nameof(watchdog));
            _dispatcher = Window.Current?.Dispatcher
                ?? CoreWindow.GetForCurrentThread()?.Dispatcher;
        }

        public Task RunAsync(
            Action action,
            [CallerMemberName] string caller = null,
            [CallerFilePath] string callerFile = null)
        {
            if (_dispatcher == null)
            {
                action();
                return Task.CompletedTask;
            }

            if (_dispatcher.HasThreadAccess)
            {
                _watchdog.Run(action, "vm", caller, callerFile);
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>();
            _ = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    _watchdog.Run(action, "vm", caller, callerFile);
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task;
        }
    }
}
