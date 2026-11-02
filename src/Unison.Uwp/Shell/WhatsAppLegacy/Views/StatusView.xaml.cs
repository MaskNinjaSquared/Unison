using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.ViewModels;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Views
{
    /// <summary>
    /// Legacy Status pivot: list only. Detail opens on <see cref="ShellView"/> DetailHost
    /// (same pattern as Chats/Channels — not inside the Pivot item).
    /// </summary>
    public sealed partial class StatusView : Page
    {
        private ShellViewModel _shell;
        private bool _surfaceActive;

        public event EventHandler MenuClicked;

        public event EventHandler SearchRequested;

        public event EventHandler ProfileRequested;

        public event EventHandler DebugRequested;

        public event EventHandler SettingsRequested;

        public event EventHandler<StatusAuthorSelectedEventArgs> AuthorOpenRequested;

        public event EventHandler StatusClosed;

        public StatusListView ListChrome => StatusListPart;

        public StatusView()
        {
            InitializeComponent();
            StatusListPart.SearchRequested += (s, e) => SearchRequested?.Invoke(this, e);
            StatusListPart.ProfileRequested += (s, e) => ProfileRequested?.Invoke(this, e);
            StatusListPart.DebugRequested += (s, e) => DebugRequested?.Invoke(this, e);
            StatusListPart.SettingsRequested += (s, e) => SettingsRequested?.Invoke(this, e);
            NavigationCacheMode = NavigationCacheMode.Disabled;
        }

        public Task InitAsync(ShellViewModel shell)
        {
            _shell = shell ?? App.Services?.GetService<ShellViewModel>();
            if (!_surfaceActive)
            {
                _shell?.EnterStatusSurface();
                _surfaceActive = true;
            }

            return Task.CompletedTask;
        }

        public Task LeaveAsync()
        {
            if (_surfaceActive)
            {
                StatusListPart?.ClearSelection();
                _shell?.LeaveStatusSurface();
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
            base.OnNavigatedFrom(e);
            _ = LeaveAsync();
        }

        public void ClearSelection() => StatusListPart?.ClearSelection();

        private void StatusListPart_AuthorSelected(object sender, StatusAuthorSelectedEventArgs e)
        {
            if (e?.Author == null)
            {
                return;
            }

            AuthorOpenRequested?.Invoke(this, e);
        }

        private void StatusListPart_SelectionCleared(object sender, EventArgs e)
        {
            StatusClosed?.Invoke(this, EventArgs.Empty);
        }

        private void StatusListPart_MenuClicked(object sender, EventArgs e)
        {
            if (MenuClicked != null)
            {
                MenuClicked.Invoke(this, EventArgs.Empty);
                return;
            }

            try
            {
                var shell = App.Services?.GetService<ShellViewModel>();
                if (shell != null)
                {
                    shell.IsPaneOpen = !shell.IsPaneOpen;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[StatusView] Menu fallback failed: " + ex.Message);
            }
        }
    }
}
