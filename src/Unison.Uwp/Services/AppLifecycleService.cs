using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Unison.Uwp.Services
{
    public sealed class AppLifecycleService : IAppLifecycle
    {
        public void Exit()
        {
            try
            {
                Application.Current.Exit();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AppLifecycle] Exit: " + ex.Message);
            }
        }

        public async Task WaitUntilForegroundAsync()
        {
            Window window = Window.Current;
            if (window == null)
            {
                return;
            }

            var resumed = new TaskCompletionSource<bool>();
            bool leftForeground = false;
            try
            {
                if (window.CoreWindow != null &&
                    window.CoreWindow.ActivationMode == CoreWindowActivationMode.Deactivated)
                {
                    leftForeground = true;
                }
            }
            catch
            {
            }

            WindowActivatedEventHandler handler = null;
            handler = (s, e) =>
            {
                if (e.WindowActivationState == CoreWindowActivationState.Deactivated)
                {
                    leftForeground = true;
                    return;
                }

                if (leftForeground)
                {
                    resumed.TrySetResult(true);
                }
            };

            window.Activated += handler;
            try
            {
                if (!leftForeground)
                {
                    Task first = await Task.WhenAny(resumed.Task, Task.Delay(1500));
                    if (first == resumed.Task || !leftForeground)
                    {
                        return;
                    }
                }

                await Task.WhenAny(resumed.Task, Task.Delay(TimeSpan.FromMinutes(10)));
            }
            catch
            {
            }
            finally
            {
                window.Activated -= handler;
            }
        }
    }
}
