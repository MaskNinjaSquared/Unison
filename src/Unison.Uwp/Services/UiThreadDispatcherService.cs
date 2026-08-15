using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Windows.ApplicationModel.Core;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// An <see cref="IDispatcher"/> that finds the UI thread when the work arrives rather than
    /// when it is constructed.
    /// </summary>
    /// <remarks>
    /// <see cref="DispatcherService"/> captures <c>Window.Current.Dispatcher</c> in its constructor,
    /// which is empty while the container is being built - and a dispatcher that came out null runs
    /// its callbacks inline, on whichever thread called. That is fatal for anything mutating an
    /// <c>ObservableCollection</c> the list is bound to, so state that is composed at startup uses
    /// this one instead: it looks the dispatcher up per call and falls back to the main view's.
    /// </remarks>
    public sealed class UiThreadDispatcherService : IDispatcher
    {
        public Task RunAsync(Action action)
        {
            if (action == null)
            {
                return Task.CompletedTask;
            }

            var dispatcher = Resolve();
            if (dispatcher == null || dispatcher.HasThreadAccess)
            {
                // No window yet means no bound list either, so running inline is safe and is
                // better than dropping the mutation on the floor.
                action();
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource<bool>();
            var ignored = dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    action();
                    completion.SetResult(true);
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });

            return completion.Task;
        }

        private static CoreDispatcher Resolve()
        {
            try
            {
                var window = Window.Current;
                if (window != null && window.Dispatcher != null)
                {
                    return window.Dispatcher;
                }

                var mainView = CoreApplication.MainView;
                var coreWindow = mainView == null ? null : mainView.CoreWindow;
                return coreWindow == null ? null : coreWindow.Dispatcher;
            }
            catch
            {
                // CoreWindow throws rather than returning null when read off a non-UI view.
                return null;
            }
        }
    }
}
