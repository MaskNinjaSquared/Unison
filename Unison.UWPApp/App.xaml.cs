using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Unison.UWPApp.Services;

namespace Unison.UWPApp
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    sealed partial class App : Application
    {
        /// <summary>
        /// Initializes the singleton application object.
        /// </summary>
        public App()
        {
            this.UnhandledException += App_UnhandledException;
            try
            {
                this.InitializeComponent();
            }
            catch (Exception ex)
            {
                LogStartupFailure("App.InitializeComponent", ex);
            }
            this.Suspending += OnSuspending;
            WhatsAppService.InitializeLoggingSettings();
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.
        /// </summary>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            Frame rootFrame = Window.Current.Content as Frame;

            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;

                if (e.PreviousExecutionState == ApplicationExecutionState.Terminated)
                {
                    // TODO: Load state from previously suspended application
                }
            }

            if (e.PrelaunchActivated == false)
            {
                if (rootFrame.Content == null)
                {
                    try
                    {
                        rootFrame.Navigate(typeof(MainPage), e.Arguments);
                    }
                    catch (Exception ex)
                    {
                        LogStartupFailure("MainPage.Navigate", ex);
                        ShowStartupFailureFallback("MainPage navigation failed", ex);
                    }
                }
                Window.Current.Activate();
            }
        }

        /// <summary>
        /// Invoked when Navigation to a certain page fails
        /// </summary>
        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            LogStartupFailure($"NavigationFailed:{e.SourcePageType?.FullName}", e.Exception);
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private void App_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            LogStartupFailure("UnhandledException", e.Exception);
            ShowStartupFailureFallback("Unhandled startup error", e.Exception);
            e.Handled = true;
        }

        private static async void LogStartupFailure(string stage, Exception ex)
        {
            try
            {
                var lines =
                    $"[{DateTimeOffset.Now:O}] {stage}{Environment.NewLine}" +
                    (ex?.ToString() ?? "<no exception>") +
                    Environment.NewLine +
                    "----------------------------------------" +
                    Environment.NewLine;

                System.Diagnostics.Debug.WriteLine($"[App] Startup failure during {stage}: {ex}");

                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    "StartupFailure.txt",
                    CreationCollisionOption.OpenIfExists);
                await FileIO.AppendTextAsync(file, lines);
            }
            catch
            {
                // Best-effort diagnostics only.
            }
        }

        private static void ShowStartupFailureFallback(string stage, Exception ex)
        {
            try
            {
                var root = new Grid
                {
                    Background = new SolidColorBrush(Colors.White)
                };

                var stack = new StackPanel
                {
                    Width = 520,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var logo = new Image
                {
                    Width = 96,
                    Height = 96,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Source = new BitmapImage(new Uri("ms-appx:///Assets/WhatsAppLogo.png"))
                };

                var title = new TextBlock
                {
                    Text = "Unison failed to start",
                    Margin = new Thickness(0, 24, 0, 8),
                    TextAlignment = TextAlignment.Center,
                    FontSize = 24,
                    Foreground = new SolidColorBrush(Colors.Black)
                };

                var details = new TextBlock
                {
                    Text = $"{stage}{Environment.NewLine}{ex?.GetType().Name}: {ex?.Message}{Environment.NewLine}{Environment.NewLine}Details were saved to StartupFailure.txt in LocalState.",
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Colors.Black)
                };

                stack.Children.Add(logo);
                stack.Children.Add(title);
                stack.Children.Add(details);
                root.Children.Add(stack);

                Window.Current.Content = root;
                Window.Current.Activate();
            }
            catch
            {
                // Nothing else we can do here.
            }
        }

        /// <summary>
        /// Invoked when application execution is being suspended.
        /// </summary>
        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await WhatsAppService.Instance.ShutdownAsync(persist: true);
                }
                catch
                {
                    // Best-effort shutdown path.
                }
                finally
                {
                    deferral.Complete();
                }
            });
        }
    }
}
