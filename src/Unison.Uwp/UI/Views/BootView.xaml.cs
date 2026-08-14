using System;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.ViewModels;
using Unison.Uwp.Services.WhatsApp;

namespace Unison.Uwp.UI.Views
{
    /// <summary>
    /// Boot surface: cold start (session resolve) and post-QR green bridge before next root page.
    /// Green lives on <c>RootGrid</c>; exit floats it up to reveal the page theme background.
    /// Icon size tracks the window short side so splash → Boot stays consistent when not maximized.
    /// </summary>
    public sealed partial class BootView : Page
    {
        /// <summary>Parameter for <see cref="NavigationRoutes.Boot"/> after QR exit wipe.</summary>
        public const string PostPairingParameter = "postPairing";

        private const int MinDwellMs = 3000;

        /// <summary>Matches BootWordmark Opacity BeginTime (ms) — when Unison starts fading in.</summary>
        private const int WordmarkBeginMs = 1200;

        /// <summary>Approx. splash mark vs short window edge (UWP splash scales with available space).</summary>
        private const double SplashIconShortSideFraction = 0.15;
        private const double SplashIconMinDip = 56;
        private const double SplashIconMaxDip = 108;
        private const double IconEndScale = 0.58;
        private const double IconWordmarkGapDip = 12;

        private bool _introStarted;
        private bool _themeBackgroundApplied;

        public BootView()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;
            Loaded += BootView_Loaded;
            SizeChanged += BootView_SizeChanged;
        }

        private void BootView_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateSplashMatchedIconSize();
            TryStartIntroAnimation();
            try
            {
                App.Services?.GetService<IShellThemeService>()?.ApplyChrome();
            }
            catch
            {
            }
        }

        private void BootView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateSplashMatchedIconSize();
            TryStartIntroAnimation();
        }

        /// <summary>
        /// Match system splash sizing: mark scales with the shorter window side and clamps.
        /// Final icon + "Unison" pair is centered as one group (measured widths), not magic offsets.
        /// </summary>
        private void UpdateSplashMatchedIconSize()
        {
            if (BootIcon == null || BootWordmark == null)
            {
                return;
            }

            double w = ActualWidth > 1 ? ActualWidth : RootGrid?.ActualWidth ?? 0;
            double h = ActualHeight > 1 ? ActualHeight : RootGrid?.ActualHeight ?? 0;
            if (w < 32 || h < 32)
            {
                return;
            }

            double shortSide = Math.Min(w, h);
            double size = shortSide * SplashIconShortSideFraction;
            if (size < SplashIconMinDip)
            {
                size = SplashIconMinDip;
            }
            else if (size > SplashIconMaxDip)
            {
                size = SplashIconMaxDip;
            }

            BootIcon.Width = size;
            BootIcon.Height = size;

            // Wordmark ~height of the icon after shrink (animation Scale To=IconEndScale).
            double endIconW = size * IconEndScale;
            BootWordmark.FontSize = Math.Max(24, Math.Min(endIconW * 0.92, 80));
            BootWordmark.Margin = new Thickness(0);
            BootWordmark.UpdateLayout();

            double textW = BootWordmark.ActualWidth;
            if (textW < 1)
            {
                // Pre-measure fallback for "Unison" + CharacterSpacing before first arrange.
                textW = BootWordmark.FontSize * 4.4;
            }

            // Both elements are HorizontalAlignment=Center; TranslateX is offset from screen center.
            // Visual icon center after Scale(IconEndScale) @ origin 0.5 stays at layout center + TranslateX.
            double total = endIconW + IconWordmarkGapDip + textW;
            double iconTranslateX = -total / 2.0 + endIconW / 2.0;
            double wordmarkTranslateX = -total / 2.0 + endIconW + IconWordmarkGapDip + textW / 2.0;

            if (BootWordmarkTranslate != null)
            {
                BootWordmarkTranslate.X = wordmarkTranslateX;
            }

            if (BootIconTranslateXAnim != null)
            {
                BootIconTranslateXAnim.To = iconTranslateX;
            }
        }

        private void TryStartIntroAnimation()
        {
            if (_introStarted || BootIntroStoryboard == null || BootIcon == null)
            {
                return;
            }

            if (BootIcon.ActualWidth < 8 && BootIcon.Width < 8)
            {
                return;
            }

            if (ActualWidth < 32 || ActualHeight < 32)
            {
                return;
            }

            _introStarted = true;
            try
            {
                BootIntroStoryboard.Begin();
                _ = ApplyThemeBackgroundWhenWordmarkStartsAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BootView] Intro animation failed: " + ex.Message);
                ApplyThemePageBackground();
            }
        }

        /// <summary>
        /// Keep green until Unison begins; then Page uses theme brush so exit wipe
        /// reveals the right colour (no dark/light flash behind RootGrid earlier).
        /// </summary>
        private async Task ApplyThemeBackgroundWhenWordmarkStartsAsync()
        {
            try
            {
                await Task.Delay(WordmarkBeginMs);
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, ApplyThemePageBackground);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BootView] Theme background switch failed: " + ex.Message);
            }
        }

        private void ApplyThemePageBackground()
        {
            if (_themeBackgroundApplied)
            {
                return;
            }

            try
            {
                object brush = null;
                if (Application.Current?.Resources?.ContainsKey("ApplicationPageBackgroundThemeBrush") == true)
                {
                    brush = Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
                }

                if (brush is Brush themeBrush)
                {
                    Background = themeBrush;
                }
                else
                {
                    Background = (Brush)Resources["ApplicationPageBackgroundThemeBrush"];
                }

                _themeBackgroundApplied = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BootView] ApplyThemePageBackground: " + ex.Message);
            }
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            ShellViewModel shell = null;
            try
            {
                (App.GetWhatsAppService() as WhatsAppService)?.AttachUiDispatcher(Dispatcher);
                shell = App.Services?.GetRequiredService<ShellViewModel>();
                if (shell == null)
                {
                    return;
                }

                string param = e.Parameter as string;
                bool postPairing = string.Equals(param, PostPairingParameter, StringComparison.OrdinalIgnoreCase);

                // Queue before Initialize so ChatsView can see PendingOpenChatJid on first load.
                if (!postPairing)
                {
                    shell.QueueOpenChatFromActivation(param);
                }

                Task dwellTask = Task.Delay(MinDwellMs);

                // Prepare next root surface without leaving Boot yet.
                shell.SuppressRootNavigation = true;
                try
                {
                    if (!postPairing)
                    {
                        await shell.InitializeAsync();
                    }
                    // postPairing: already Connected after QR wipe.
                }
                finally
                {
                    // Keep suppress until FinishBootRootNavigation after exit anim.
                }

                await dwellTask;
                // Ensure theme bg is ready before exit wipe even if intro timing drifted.
                ApplyThemePageBackground();
                await PlayExitAnimationAsync();
                shell.FinishBootRootNavigation();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BootView] Initialize failed: " + ex);
                try
                {
                    if (shell != null)
                    {
                        shell.SuppressRootNavigation = false;
                        shell.FinishBootRootNavigation();
                    }
                }
                catch
                {
                }
            }
        }

        private Task PlayExitAnimationAsync()
        {
            var tcs = new TaskCompletionSource<bool>();
            try
            {
                double travel = RootGrid?.ActualHeight ?? ActualHeight;
                if (travel < 200)
                {
                    travel = 640;
                }

                BootExitYAnim.To = -travel;
                BootExitYAnim.From = 0;
                RootGridTranslate.Y = 0;
                RootGrid.Opacity = 1;

                EventHandler<object> onCompleted = null;
                onCompleted = (s, e) =>
                {
                    BootExitStoryboard.Completed -= onCompleted;
                    tcs.TrySetResult(true);
                };
                BootExitStoryboard.Completed += onCompleted;
                BootExitStoryboard.Begin();

                _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
                {
                    try
                    {
                        await Task.Delay(1200);
                        tcs.TrySetResult(false);
                    }
                    catch
                    {
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BootView] Exit animation failed: " + ex.Message);
                tcs.TrySetResult(false);
            }

            return tcs.Task;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            try
            {
                BootIntroStoryboard?.Stop();
                BootExitStoryboard?.Stop();
            }
            catch
            {
            }
        }
    }
}
