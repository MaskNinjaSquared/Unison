using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Core.ViewModels;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Views
{
    /// <summary>
    /// Legacy Calls pivot: list + CommandBar. Detail opens on <see cref="ShellView"/> DetailHost
    /// (same pattern as Chats/Channels — not inside the Pivot item).
    /// </summary>
    public sealed partial class CallsView : Page
    {
        private ShellViewModel _shell;
        private bool _surfaceActive;

        public event EventHandler SearchRequested;

        public event EventHandler ProfileRequested;

        public event EventHandler DebugRequested;

        public event EventHandler SettingsRequested;

        /// <summary>Raised when Recents/Favorites open call details (shell hosts the pane).</summary>
        public event EventHandler CallDetailRequested;

        /// <summary>Raised when details close or selection clears.</summary>
        public event EventHandler CallDetailClosed;

        public CallsViewModel ViewModel { get; private set; }

        public CallsView()
        {
            if (App.Services != null)
            {
                ViewModel = App.Services.GetService<CallsViewModel>();
            }

            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;
            DataContext = ViewModel;

            CallsAppBar.SetNewCallVisible(true);
            CallsAppBar.SearchRequested += (_, __) => SearchRequested?.Invoke(this, EventArgs.Empty);
            CallsAppBar.ProfileRequested += (_, __) => ProfileRequested?.Invoke(this, EventArgs.Empty);
            CallsAppBar.DebugRequested += (_, __) => DebugRequested?.Invoke(this, EventArgs.Empty);
            CallsAppBar.SettingsRequested += (_, __) => SettingsRequested?.Invoke(this, EventArgs.Empty);
            CallsAppBar.NewCallRequested += CallsAppBar_NewCallRequested;

            if (ViewModel != null)
            {
                ViewModel.PropertyChanged += ViewModel_PropertyChanged;
                ViewModel.NewCallRequested += (_, __) => _ = ShowNewCallUnavailableAsync();
                ViewModel.OpenChatRequested += ViewModel_OpenChatRequested;
                CallsList.Bind(ViewModel);
            }
        }

        public Task InitAsync(ShellViewModel shell)
        {
            _shell = shell ?? App.Services?.GetService<ShellViewModel>();
            if (!_surfaceActive)
            {
                _shell?.EnterCallsSurface();
                _surfaceActive = true;
            }

            return Task.CompletedTask;
        }

        public Task LeaveAsync()
        {
            if (ViewModel != null && ViewModel.IsCallDetailsOpen)
            {
                ViewModel.CloseDetailsCommand?.Execute(null);
            }

            if (_surfaceActive)
            {
                _shell?.LeaveCallsSurface();
                _surfaceActive = false;
            }

            return Task.CompletedTask;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _ = InitAsync(App.Services?.GetService<ShellViewModel>());
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _ = LeaveAsync();
            base.OnNavigatedFrom(e);
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null || ViewModel == null)
            {
                return;
            }

            if (string.Equals(e.PropertyName, nameof(CallsViewModel.IsCallDetailsOpen), StringComparison.Ordinal))
            {
                if (ViewModel.IsCallDetailsOpen)
                {
                    CallDetailRequested?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    CallDetailClosed?.Invoke(this, EventArgs.Empty);
                }

                return;
            }

            if (ViewModel.IsCallDetailsOpen &&
                (string.Equals(e.PropertyName, nameof(CallsViewModel.SelectedSubject), StringComparison.Ordinal) ||
                 string.Equals(e.PropertyName, nameof(CallsViewModel.SelectedHistory), StringComparison.Ordinal) ||
                 string.Equals(e.PropertyName, nameof(CallsViewModel.SelectedLatest), StringComparison.Ordinal) ||
                 string.Equals(e.PropertyName, nameof(CallsViewModel.SelectedHistoryRest), StringComparison.Ordinal)))
            {
                CallDetailRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        private void ViewModel_OpenChatRequested(object sender, string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            if (ViewModel != null && ViewModel.IsCallDetailsOpen)
            {
                ViewModel.CloseDetailsCommand?.Execute(null);
            }

            ShellViewModel shell = _shell ?? App.Services?.GetService<ShellViewModel>();
            shell?.QueueOpenChatFromActivation(
                "chat=" + Uri.EscapeDataString(jid),
                delayChatListUntilConversationReady: false);
        }

        private void CallsAppBar_NewCallRequested(object sender, EventArgs e)
        {
            _ = ShowNewCallUnavailableAsync();
        }

        private async Task ShowNewCallUnavailableAsync()
        {
            IDialogService dialogs = App.Services?.GetService<IDialogService>();
            if (dialogs == null)
            {
                return;
            }

            IStringResources strings = App.Services?.GetService<IStringResources>();
            string title = strings?.Get("Calls_Header", "Calls") ?? "Calls";
            string body = strings?.Get(
                "Calls_NewCallUnavailable",
                "Calling is not available yet.") ?? "Calling is not available yet.";
            string close = strings?.Get("Common_OK", "OK") ?? "OK";
            await dialogs.ShowMessageAsync(title, body, close);
        }
    }
}
