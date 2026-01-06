using System;
using Windows.ApplicationModel.Activation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Windows.Foundation;

namespace Unison.UWPApp
{
    public sealed partial class ExtendedSplashPage : Page
    {
        private SplashScreen _splash; // Variable to hold the splash screen object.
        private Rect _splashImageRect; // Rect to hold splash screen image coordinates.
        private bool _dismissed = false; // Variable to track splash screen dismissal status.
        private Frame _rootFrame;

        public ExtendedSplashPage(SplashScreen splashscreen, bool loadState)
        {
            this.InitializeComponent();

            // Listen for window resize events to reposition the extended splash screen image accordingly.
            Window.Current.SizeChanged += new WindowSizeChangedEventHandler(ExtendedSplash_OnResize);

            _splash = splashscreen;

            if (_splash != null)
            {
                // Register an event handler to be executed when the splash screen has been dismissed.
                _splash.Dismissed += new Windows.Foundation.TypedEventHandler<SplashScreen, object>(DismissedEventHandler);

                // Retrieve the window coordinates of the splash screen image.
                _splashImageRect = _splash.ImageLocation;
                PositionImage();
            }

            // Create a Frame to act as the navigation context
            _rootFrame = new Frame();
        }

        // Position the extended splash screen image in the same location as the system splash screen image.
        void PositionImage()
        {
            // The extended splash screen image in this sample is just a placeholder.
            // But we want it full screen anyway as per user request (3840x2160 logic)
            // so we don't strictly need to match the system splash location, 
            // but we keep the logic for standard practice.
        }

        void ExtendedSplash_OnResize(object sender, WindowSizeChangedEventArgs e)
        {
            // Safely update the extended splash screen image coordinates. This function will be fired in response to snapping, unsnapping, rotation, etc...
            if (_splash != null)
            {
                _splashImageRect = _splash.ImageLocation;
                PositionImage();
            }
        }

        // Include code to be executed when the system has transitioned from the splash screen to the extended splash screen (applicationMainPage).
        async void DismissedEventHandler(SplashScreen sender, object e)
        {
            _dismissed = true;

            // Navigate to main page after a small delay to ensure the user sees the high-res splash
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                await System.Threading.Tasks.Task.Delay(1500); // 1.5s branding exposure
                Window.Current.Content = _rootFrame;
                _rootFrame.Navigate(typeof(MainPage));
            });
        }
    }
}
