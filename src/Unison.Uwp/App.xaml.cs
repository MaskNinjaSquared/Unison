using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation.Metadata;
using Windows.Graphics.Display;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Unison.Baileys.Client;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Diagnostics;
using Unison.Core.Factories;
using Unison.Core.Helpers;
using Unison.Core.Mappers;
using Unison.Core.State;
using Unison.Core.ViewModels;
using Unison.Socket.Abstractions;
using Unison.Socket.Signal;
using Unison.Uwp.Client;
using Unison.Uwp.Data;
using Unison.Uwp.Services;
using Unison.Uwp.Services.Socket;
using Unison.Uwp.Services.WhatsApp;
using Unison.Uwp.Services.WhatsApp.Chats;
using Unison.Uwp.Services.WhatsApp.Connection;
using Unison.Uwp.Services.WhatsApp.Contacts;
using Unison.Uwp.Services.WhatsApp.Diagnostics;
using Unison.Uwp.Services.WhatsApp.History;
using Unison.Uwp.Services.WhatsApp.Messages;
using Unison.Uwp.Services.WhatsApp.Profiles;

namespace Unison.Uwp
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    sealed partial class App : Application
    {
        private int _memoryCleanupRunning;
        private int _resumeDispatchRunning;
        private bool _visibilityHooked;
        private bool _windowWasHidden;
        private bool _statusBarOrientationHooked;

        public static IServiceProvider Services { get; private set; }

        public static bool IsWindowVisible { get; private set; } = true;

        /// <summary>DI-resolved WhatsApp service (same instance as the legacy singleton).</summary>
        /// <summary>
        /// The WhatsApp service, or null while the container is still being built. The service is
        /// no longer able to conjure itself on demand, because its state now belongs to the
        /// container, so an early caller is told there is nothing yet instead of being handed an
        /// instance that shares nothing with the one the app will actually use.
        /// </summary>
        internal static IWhatsAppService GetWhatsAppService()
        {
            var services = Services;
            return services == null ? null : services.GetRequiredService<IWhatsAppService>();
        }

        /// <summary>
        /// Initializes the singleton application object.
        /// </summary>
        public App()
        {
            this.UnhandledException += App_UnhandledException;
            try
            {
                SQLitePCL.Batteries.Init();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[App] SQLitePCL Batteries.Init failed: " + ex.Message);
            }
            RuntimeDiagnosticsService.Instance.Start();
            RuntimeDiagnosticsService.Instance.Write("lifecycle", "app-constructor");
            try
            {
                // Must run before InitializeComponent so ResourceLoader / x:Uid resolve correctly.
                ApplyLanguageOverrideEarly();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[App] Early language: " + ex.Message);
            }
            try
            {
                this.InitializeComponent();
            }
            catch (Exception ex)
            {
                LogStartupFailure("App.InitializeComponent", ex);
            }
            this.Suspending += OnSuspending;
            this.Resuming += OnResuming;
            this.EnteredBackground += OnEnteredBackground;
            this.LeavingBackground += OnLeavingBackground;
            WhatsAppService.InitializeLoggingSettings();

            // --- Monitor de memoria ---------------------------------------------
            // Em Windows 10 Mobile cada app tem um teto de memoria definido pelo
            // aparelho. Passar do teto faz o sistema encerrar o app -- e o que causava
            // o fechamento sozinho. Aqui registramos o teto e liberamos caches quando
            // o uso sobe, conforme recomenda a documentacao da plataforma.
            try
            {
                var limite = Windows.System.MemoryManager.AppMemoryUsageLimit / (1024 * 1024);
                var uso = Windows.System.MemoryManager.AppMemoryUsage / (1024 * 1024);
                System.Diagnostics.Debug.WriteLine($"[Memoria] Limite do app: {limite} MB | uso inicial: {uso} MB");

                Windows.System.MemoryManager.AppMemoryUsageIncreased += (s, e) =>
                {
                    var nivel = Windows.System.MemoryManager.AppMemoryUsageLevel;
                    var usoAtual = Windows.System.MemoryManager.AppMemoryUsage / (1024 * 1024);
                    System.Diagnostics.Debug.WriteLine($"[Memoria] Nivel={nivel}, uso={usoAtual} MB");
                    if (nivel == Windows.System.AppMemoryUsageLevel.High ||
                        nivel == Windows.System.AppMemoryUsageLevel.OverLimit)
                    {
                        RuntimeDiagnosticsService.Instance.Write(
                            "memory",
                            "pressure",
                            "level=" + nivel + "; usageMb=" + usoAtual + "; limitMb=" + limite);
                    }

                    if (nivel != Windows.System.AppMemoryUsageLevel.High &&
                        nivel != Windows.System.AppMemoryUsageLevel.OverLimit)
                    {
                        return;
                    }

                    // O handler antigo executava GC.Collect + WaitForPendingFinalizers de
                    // forma sincrona, congelando a interface. Coalescemos os avisos e
                    // liberamos referencias fora do callback do sistema.
                    if (Interlocked.Exchange(ref _memoryCleanupRunning, 1) != 0)
                    {
                        return;
                    }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Memory pressure can arrive before the container is up, and there is
                            // nothing holding memory to release at that point anyway.
                            var whatsApp = App.GetWhatsAppService();
                            if (whatsApp != null)
                            {
                                await whatsApp.ReleaseMemoryAsync();
                            }

                            if (nivel == Windows.System.AppMemoryUsageLevel.OverLimit)
                            {
                                GC.Collect();
                            }
                            System.Diagnostics.Debug.WriteLine("[Memoria] Recursos liberados para evitar encerramento");
                        }
                        catch (Exception ex)
                        {
                            RuntimeDiagnosticsService.Instance.RecordException(
                                "memory",
                                "cleanup-failed",
                                ex);
                            System.Diagnostics.Debug.WriteLine($"[Memoria] Falha na limpeza: {ex.Message}");
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _memoryCleanupRunning, 0);
                        }
                    });
                };
            }
            catch { }
            // ---------------------------------------------------------------------
        }

        private static void ConfigureServices(Frame rootFrame)
        {
            var services = new ServiceCollection();

            services.AddSingleton<IDispatcher, DispatcherService>();

            // Single owner of the in-memory chat state, and built before the service that fills
            // it: WhatsAppService receives this instance rather than creating one, so the store
            // outlives it and the view models that read it need nothing from that class.
            //
            // The concrete type is registered too because WhatsAppService still reaches the
            // transitional dictionaries on it. That registration goes away with those.
            services.AddSingleton<ChatStateStore>(_ => new ChatStateStore(new UiThreadDispatcherService()));
            services.AddSingleton<IChatStateStore>(sp => sp.GetRequiredService<ChatStateStore>());
            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<ILocalSettings, LocalSettingsService>();
            services.AddSingleton<INavigator>(_ => new NavigatorService(rootFrame));

            services.AddSingleton<SessionLoggerAdapter>();
            services.AddSingleton<ISessionLogger>(sp => sp.GetRequiredService<SessionLoggerAdapter>());
            services.AddSingleton<IProtocolLogger>(sp => sp.GetRequiredService<SessionLoggerAdapter>());

            services.AddSingleton<IMediaProcessor, MediaProcessorAdapter>();
            services.AddSingleton<IAuthPersistence>(_ => new AuthStore());
            services.AddSingleton<IKeyStore>(_ => new FileKeyStore());
            services.AddSingleton<IWhatsAppService>(
                sp => WhatsAppService.Create(sp.GetRequiredService<ChatStateStore>()));
#if DEBUG
            // Dev-only tooling: file-watch based debug send. Never attached/started in Release.
            services.AddSingleton<IDebugSendService, DebugSendService>();
#endif
            // The connection is a SocketBridge, so there is always a session to hand out.
            services.AddSingleton<IWhatsAppSessionProvider>(sp => new BridgeSessionProvider(
                () => ((WhatsAppService)sp.GetRequiredService<IWhatsAppService>()).Socket));

            services.AddSingleton<IProfileService>(sp => new ProfileFacade(
                sp.GetRequiredService<IWhatsAppSessionProvider>(),
                sp.GetRequiredService<IWhatsAppService>()));

            // Falls back to the service's own resync when the socket is down and the session
            // provider has nothing to give it.
            services.AddSingleton(sp => new HistoryFacade(
                sp.GetRequiredService<IWhatsAppSessionProvider>(),
                sp.GetRequiredService<IWhatsAppService>(),
                sp.GetRequiredService<IMessageStore>(),
                sp.GetRequiredService<ILocalSettings>()));
            services.AddSingleton<IHistoryService>(sp => sp.GetRequiredService<HistoryFacade>());

            services.AddSingleton<IMessageService, MessageFacade>();
            services.AddSingleton<IChatItemVmFactory, ChatItemVmFactory>();
            services.AddSingleton<IChatMessageVmFactory, ChatMessageVmFactory>();
            services.AddSingleton<IChatDetailInfoViewModelFactory, ChatDetailInfoViewModelFactory>();
            services.AddSingleton<INewChatDialogViewModelFactory, NewChatDialogViewModelFactory>();
            services.AddSingleton<IMessageStore, MessageStore>();
            services.AddSingleton<IPersonStore, PersonStore>();
            services.AddSingleton<IChatStore, ChatStore>();
            services.AddSingleton<ILocalContactsService, LocalContactsService>();

            // The LID mapping store is registered either way: it is the eventual replacement for
            // the JidAlias dictionaries, and nothing forces it to wait for the socket rewrite.
            services.AddSingleton<ILidMappingStorage, SqliteLidMappingStorage>();
            services.AddSingleton(sp => new LidMappingStore(sp.GetRequiredService<ILidMappingStorage>()));

            services.AddSingleton<IContactService>(sp => new ContactFacade(
                sp.GetRequiredService<IWhatsAppSessionProvider>(),
                sp.GetRequiredService<ILocalContactsService>(),
                sp.GetRequiredService<IPersonStore>(),
                sp.GetRequiredService<IWhatsAppService>(),
                sp.GetRequiredService<LidMappingStore>()));
            services.AddSingleton<IChatService>(sp => new ChatFacade(
                sp.GetRequiredService<IWhatsAppSessionProvider>(),
                sp.GetRequiredService<IWhatsAppService>(),
                sp.GetRequiredService<IChatStore>()));
            services.AddSingleton<ILiveTilesService>(_ => LiveTilesService.Instance);
            services.AddSingleton<IShortcutService, ShortcutService>();
            services.AddSingleton<INotificationService>(sp =>
            {
                var notifications = NotificationService.Instance;
                notifications.AttachLiveTiles(sp.GetRequiredService<ILiveTilesService>());
                notifications.AttachShortcuts(sp.GetRequiredService<IShortcutService>());
                return notifications;
            });
            services.AddSingleton<IRuntimeDiagnostics>(_ => RuntimeDiagnosticsService.Instance);
            services.AddSingleton<ISocketBrokerService>(_ => SocketBrokerCoordinator.Instance);
            services.AddSingleton<IBackgroundAccessService, BackgroundAccessService>();
            services.AddSingleton<IAppLifecycle, AppLifecycleService>();
            services.AddSingleton<IBackgroundAccessPrompt, BackgroundAccessPrompt>();
            services.AddSingleton<IUriLauncher, UriLauncherService>();
            services.AddSingleton<IFilePicker, FilePickerService>();
            services.AddSingleton<IShareService, ShareService>();
            services.AddSingleton<IAudioRecordingService, AudioRecordingService>();
            services.AddSingleton<IVoicePlaybackRoutingService, VoicePlaybackRoutingService>();
            services.AddSingleton<IReactionMapper, ReactionMapper>();
            services.AddSingleton<IChatMessageMapper, ChatMessageMapper>();
            services.AddSingleton<IStringResources, StringResourcesService>();
            services.AddSingleton<IConnectionService>(sp => new ConnectionFacade(
                sp.GetRequiredService<ILocalSettings>(),
                sp.GetRequiredService<INotificationService>(),
                sp.GetRequiredService<IStringResources>(),
                sp.GetRequiredService<IWhatsAppSessionProvider>()));
            services.AddSingleton<ISystemInfoProvider, SystemInfoProvider>();
            services.AddSingleton<IStatusBarService, StatusBarService>();
            services.AddSingleton<ILocationKeepAliveService, LocationKeepAliveService>();
            services.AddSingleton<IShellThemeService, ShellThemeService>();
            services.AddSingleton<IAppLanguageService, AppLanguageService>();

            // Validation harness for the Unison.Socket rewrite. Reachable only from the debug
            // pane and runs on throwaway credentials, so it cannot affect the signed-in session.
            services.AddSingleton<ISocketSliceProbe, SocketSliceProbe>();

            // Read side of diagnostics, for the debug pane only. Code that merely records events
            // keeps injecting ISessionLogger / IRuntimeDiagnostics directly.
            services.AddSingleton<IDiagnosticsConsole, DiagnosticsConsole>();

            services.AddTransient<LoginViewModel>();
            services.AddTransient<StartViewModel>();
            services.AddSingleton<ShellViewModel>();
            services.AddTransient<ChatListViewModel>();
            services.AddTransient<ChatDetailViewModel>();
            services.AddTransient<DebugViewModel>();
            services.AddTransient<NewChatDialogViewModel>();
            services.AddTransient<SettingsViewModel>();

            Services = services.BuildServiceProvider(validateScopes: true);
            var whatsApp = Services.GetRequiredService<IWhatsAppService>();
            var whatsAppImpl = (WhatsAppService)whatsApp;
            whatsAppImpl.AttachSystemInfoProvider(Services.GetRequiredService<ISystemInfoProvider>());
            whatsAppImpl.AttachMessageService(Services.GetRequiredService<IMessageService>());
            whatsAppImpl.AttachContactService(Services.GetRequiredService<IContactService>());
            whatsAppImpl.AttachConnectionService(Services.GetRequiredService<IConnectionService>());
            Services.GetRequiredService<IConnectionService>().AttachWhatsAppService(whatsApp);
            // The remaining facades relay client events to the screens, and they can only relay
            // what they were around to hear. Build them now rather than when a screen first asks.
            Services.GetRequiredService<IProfileService>();
            Services.GetRequiredService<IHistoryService>();
            whatsAppImpl.AttachPersonStore(Services.GetRequiredService<IPersonStore>());
            whatsAppImpl.AttachChatStore(Services.GetRequiredService<IChatStore>());
#if DEBUG
            whatsAppImpl.AttachDebugSendService(Services.GetRequiredService<IDebugSendService>());
#endif
            RuntimeDiagnosticsService.Instance.AttachWhatsAppService(whatsApp);
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.
        /// </summary>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            RuntimeDiagnosticsService.Instance.Write(
                "lifecycle",
                "launched",
                "prelaunch=" + e.PrelaunchActivated + "; previousState=" + e.PreviousExecutionState);
            RuntimeDiagnosticsService.Instance.StartHealthSampling();
            NotificationService.Instance.Initialize();
            LiveTilesService.Instance.Initialize();
            EnsureWindowVisibilityTracking();
            EnsureTitleBarHook();
            EnsureStatusBarOrientationHook();

            Frame rootFrame = EnsureRootFrame();

            // PrimaryLanguageOverride from LocalSettings (ctor also applies early for x:Uid).
            ReloadLanguageFromSettings();

            try
            {
                Services.GetRequiredService<IShellThemeService>().ApplyFromSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[App] Apply shell theme: " + ex.Message);
            }

            _ = ApplyLocationKeepAliveConfigAsync();

            if (e.PrelaunchActivated)
            {
                return;
            }

            // Title bar colors require an activated view; apply at window bootstrap
            // (and again on the next dispatcher turns — the OS can reset caption chrome).
            Window.Current.Activate();
            ApplyWindowChromeBootstrap();

            // Classic: empty frame → Boot once. Warm process keeps current page (Login/Shell).
            // Do not remount Boot/Login for language — Mobile Exit + cold start handles that.
            if (rootFrame.Content == null)
            {
                var launchArguments = e.Arguments;
                _ = rootFrame.Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    NavigateRootToBoot(launchArguments);
                });
                return;
            }

            HandleActivationArguments(e.Arguments);
        }

        protected override void OnActivated(IActivatedEventArgs args)
        {
            RuntimeDiagnosticsService.Instance.Write(
                "lifecycle",
                "activated",
                "kind=" + (args == null ? "<null>" : args.Kind.ToString()) +
                "; previousState=" + (args == null ? "<null>" : args.PreviousExecutionState.ToString()));
            RuntimeDiagnosticsService.Instance.StartHealthSampling();
            if (args != null && args.Kind == ActivationKind.ToastNotification)
            {
                NotificationService.Instance.Initialize();
                LiveTilesService.Instance.Initialize();
                EnsureWindowVisibilityTracking();
                ConfigureAppChrome();
                EnsureTitleBarHook();
                EnsureStatusBarOrientationHook();

                var toastArgs = args as ToastNotificationActivatedEventArgs;
                Frame rootFrame = EnsureRootFrame();
                string toastArgument = toastArgs?.Argument ?? string.Empty;

                RuntimeDiagnosticsService.Instance.Write(
                    "lifecycle",
                    "toast-activated",
                    "argLength=" + toastArgument.Length +
                    "; hasChat=" + (toastArgument.IndexOf("chat=", StringComparison.OrdinalIgnoreCase) >= 0));

                if (rootFrame.Content == null || !IsKnownRootContent(rootFrame.Content))
                {
                    NavigateRootToBoot(toastArgument);
                }

                HandleActivationArguments(toastArgument);

                Window.Current.Activate();
                ApplyWindowChromeBootstrap();
                return;
            }

            base.OnActivated(args);
        }

        /// <summary>Secondary tile / toast <c>chat=</c> deep link into an existing session.</summary>
        private void HandleActivationArguments(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments) || Services == null)
            {
                return;
            }

            try
            {
                var shell = Services.GetService<ShellViewModel>();
                shell?.QueueOpenChatFromActivation(arguments);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[App] HandleActivationArguments: " + ex.Message);
            }
        }

        /// <summary>Creates the root Frame and DI if needed.</summary>
        private Frame EnsureRootFrame()
        {
            Frame rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }

            if (Services == null)
            {
                ConfigureServices(rootFrame);
            }

            return rootFrame;
        }

        /// <summary>
        /// Entry route only — does not inspect session. BootView + ShellViewModel decide.
        /// </summary>
        private void NavigateRootToBoot(object parameter = null)
        {
            try
            {
                if (Services == null)
                {
                    EnsureRootFrame();
                }

                Services.GetRequiredService<INavigator>()
                    .NavigateAndClear(NavigationRoutes.Boot, parameter);
            }
            catch (Exception ex)
            {
                LogStartupFailure("Navigator.Boot", ex);
                try
                {
                    var frame = Window.Current.Content as Frame;
                    frame?.Navigate(typeof(UI.Views.BootView), parameter);
                }
                catch (Exception fallbackEx)
                {
                    ShowStartupFailureFallback("Boot navigation failed", fallbackEx);
                }
            }
        }

        private static bool IsKnownRootContent(object content)
        {
            return content is MainView
                || content is UI.Views.BootView
                || content is UI.Views.StartView
                || content is UI.Views.LoginView;
        }

        private void EnsureWindowVisibilityTracking()
        {
            if (_visibilityHooked || Window.Current?.CoreWindow == null)
            {
                return;
            }

            IsWindowVisible = Window.Current.CoreWindow.Visible;
            Window.Current.CoreWindow.VisibilityChanged += CoreWindow_VisibilityChanged;
            _visibilityHooked = true;
        }

        private void CoreWindow_VisibilityChanged(CoreWindow sender, VisibilityChangedEventArgs args)
        {
            IsWindowVisible = args.Visible;
            RuntimeDiagnosticsService.Instance.Write(
                "lifecycle",
                "visibility-changed",
                "visible=" + args.Visible);
            if (!args.Visible)
            {
                _windowWasHidden = true;
                return;
            }

            // Do not start a second connection during the first visible event of a cold
            // launch. Only a window that was previously hidden represents a resume path.
            if (_windowWasHidden)
            {
                _windowWasHidden = false;
                _ = ResumeConnectionWhenVisibleAsync();
            }
        }

        private async System.Threading.Tasks.Task ResumeConnectionWhenVisibleAsync()
        {
            if (Interlocked.Exchange(ref _resumeDispatchRunning, 1) != 0)
            {
                return;
            }

            try
            {
                await App.GetWhatsAppService().ResumeAsync();
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.RecordException(
                    "lifecycle",
                    "visibility-reconnect-failed",
                    ex);
                System.Diagnostics.Debug.WriteLine($"[App] Visibility reconnect failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _resumeDispatchRunning, 0);
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

        private void App_UnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            RuntimeDiagnosticsService.Instance.RecordException(
                "runtime",
                "xaml-unhandled-exception",
                e.Exception);
            _ = RuntimeDiagnosticsService.Instance.FlushAsync("xaml-unhandled");
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


        private async void OnEnteredBackground(object sender, EnteredBackgroundEventArgs e)
        {
            var deferral = e.GetDeferral();
            RuntimeDiagnosticsService.Instance.Write("lifecycle", "entered-background");
            try
            {
                bool transferred = await App.GetWhatsAppService().TransferActiveSocketToBrokerAsync("entered-background");
                RuntimeDiagnosticsService.Instance.Write(
                    "socket-broker",
                    "entered-background-result",
                    "transferred=" + transferred);
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.RecordException(
                    "socket-broker",
                    "entered-background-failed",
                    ex);
            }
            finally
            {
                deferral.Complete();
            }
        }

        private void OnLeavingBackground(object sender, LeavingBackgroundEventArgs e)
        {
            RuntimeDiagnosticsService.Instance.Write("lifecycle", "leaving-background");
            ApplyWindowChromeBootstrap();
            _ = ResumeConnectionWhenVisibleAsync();
        }

        /// <summary>
        /// Invoked when application execution is being suspended.
        /// </summary>
        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            RuntimeDiagnosticsService.Instance.Write("lifecycle", "suspending-start");
            try
            {
                var whatsApp = App.GetWhatsAppService();
                bool brokered = await whatsApp.TransferActiveSocketToBrokerAsync("suspending");
                if (brokered)
                {
                    await whatsApp.PrepareForSuspendAsync();
                    RuntimeDiagnosticsService.Instance.Write(
                        "lifecycle",
                        "suspending-broker-complete");
                }
                else
                {
                    await whatsApp.ShutdownAsync(persist: true);
                    RuntimeDiagnosticsService.Instance.Write(
                        "lifecycle",
                        "suspending-shutdown-complete");
                }
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.RecordException(
                    "lifecycle",
                    "suspending-shutdown-failed",
                    ex);
                System.Diagnostics.Debug.WriteLine($"[App] Suspend shutdown failed: {ex.Message}");
            }
            finally
            {
                try
                {
                    var diagnosticsFlush = RuntimeDiagnosticsService.Instance.FlushAsync("suspending");
                    await System.Threading.Tasks.Task.WhenAny(
                        diagnosticsFlush,
                        System.Threading.Tasks.Task.Delay(250));
                }
                catch
                {
                }
                deferral.Complete();
            }
        }

        private async void OnResuming(object sender, object e)
        {
            RuntimeDiagnosticsService.Instance.Write("lifecycle", "resuming-start");
            try
            {
                ApplyWindowChromeBootstrap();
                // Suspension intentionally closes the WebSocket. On Windows 10 Mobile
                // OnLaunched is not called again when the process is resumed, so the
                // connection must be restored explicitly.
                await ResumeConnectionWhenVisibleAsync();
                await ApplyLocationKeepAliveConfigAsync();
                RuntimeDiagnosticsService.Instance.Write(
                    "lifecycle",
                    "resuming-complete",
                    "connected=" + App.GetWhatsAppService().IsConnected);
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.RecordException(
                    "lifecycle",
                    "resume-reconnect-failed",
                    ex);
                System.Diagnostics.Debug.WriteLine($"[App] Resume reconnect failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Re-reads selected language and applies PrimaryLanguageOverride (no UI remount).
        /// </summary>
        private void ReloadLanguageFromSettings()
        {
            try
            {
                if (Services != null)
                {
                    Services.GetRequiredService<IAppLanguageService>().ApplyFromSettings();
                    return;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[App] Apply language (DI): " + ex.Message);
            }

            try
            {
                ApplyLanguageOverrideEarly();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[App] Apply language (early): " + ex.Message);
            }
        }

        private static void ApplyLanguageOverrideEarly()
        {
            int raw = Helpers.LocalSettingsAccess.Current.Get<int>(LocalSettingsConstants.SelectedLanguage);
            var language = Core.Helpers.AppLanguageInfo.FromStored(raw);
            AppLanguageService.ApplyOverride(language);
        }

        private static async Task ApplyLocationKeepAliveConfigAsync()
        {
            try
            {
                if (Services == null)
                {
                    return;
                }

                await Services.GetRequiredService<ILocationKeepAliveService>().ApplyConfigAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[App] KeepAlive ApplyConfig: " + ex.Message);
            }
        }

        /// <summary>
        /// Delegates title/status chrome to the active shell strategy.
        /// </summary>
        private static void ConfigureAppChrome()
        {
            try
            {
                Services?.GetService<IShellThemeService>()?.ApplyChrome();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[App] ConfigureAppChrome: " + ex.Message);
            }
        }

        /// <summary>
        /// Applies title-bar (and mobile status-bar) colors during window bootstrap.
        /// Must run after <see cref="Window.Activate"/> — setting ApplicationView.TitleBar
        /// too early is ignored or overwritten by the OS on desktop.
        /// </summary>
        private static void ApplyWindowChromeBootstrap()
        {
            ConfigureAppChrome();

            try
            {
                var dispatcher = Window.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    return;
                }

                // Re-apply after the first layout/paint pass; caption colors can reset once.
                _ = dispatcher.RunAsync(CoreDispatcherPriority.Normal, ConfigureAppChrome);
                _ = dispatcher.RunAsync(CoreDispatcherPriority.Low, ConfigureAppChrome);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[App] ApplyWindowChromeBootstrap: " + ex.Message);
            }
        }

        private void EnsureTitleBarHook()
        {
            try
            {
                Window.Current.Activated -= Window_ActivatedForTitleBar;
                Window.Current.Activated += Window_ActivatedForTitleBar;
            }
            catch
            {
            }
        }

        private void EnsureStatusBarOrientationHook()
        {
            if (_statusBarOrientationHooked)
            {
                return;
            }

            try
            {
                if (!ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar"))
                {
                    return;
                }

                DisplayInformation.GetForCurrentView().OrientationChanged -= Display_OrientationChangedForStatusBar;
                DisplayInformation.GetForCurrentView().OrientationChanged += Display_OrientationChangedForStatusBar;
                _statusBarOrientationHooked = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[App] EnsureStatusBarOrientationHook: " + ex.Message);
            }
        }

        private void Display_OrientationChangedForStatusBar(DisplayInformation sender, object args)
        {
            try
            {
                var theme = Services?.GetService<IShellThemeService>();
                if (theme != null)
                {
                    _ = theme.ApplyMobileStatusBarAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[App] Orientation chrome: " + ex.Message);
            }
        }

        private void Window_ActivatedForTitleBar(object sender, WindowActivatedEventArgs e)
        {
            if (e.WindowActivationState != CoreWindowActivationState.Deactivated)
            {
                ConfigureAppChrome();
            }
        }
    }
}
