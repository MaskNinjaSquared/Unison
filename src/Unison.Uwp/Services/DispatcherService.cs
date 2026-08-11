using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Unison.Uwp.Services
{
    public class DispatcherService : IDispatcher
    {
        private readonly CoreDispatcher _dispatcher;

        public DispatcherService()
        {
            _dispatcher = Window.Current?.Dispatcher
                ?? CoreWindow.GetForCurrentThread()?.Dispatcher;
        }

        public Task RunAsync(Action action)
        {
            if (_dispatcher == null || _dispatcher.HasThreadAccess)
            {
                action();
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>();
            _ = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    action();
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
