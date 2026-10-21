using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.ViewModels;
using Unison.Uwp.Helpers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Views
{
    /// <summary>
    /// Metro / WP8 contact profile overlay for the Legacy shell (People / profile=).
    /// Shares <see cref="ProfileViewModel"/> with the Unison/WhatsApp Profile page.
    /// Bottom chrome matches the chat-list expansive <see cref="CommandBar"/>.
    /// </summary>
    public sealed partial class ProfileView : Page
    {
        private ProfileViewModel _viewModel;
        private ChatDetailInfoViewModel _hookedInfo;
        private bool _infoHooked;

        public ProfileView()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;
            Loaded += ProfileView_Loaded;
            Unloaded += ProfileView_Unloaded;
        }

        private ProfileViewModel ViewModel
        {
            get
            {
                if (_viewModel == null && App.Services != null)
                {
                    _viewModel = App.Services.GetRequiredService<ProfileViewModel>();
                    DataContext = _viewModel;
                }

                return _viewModel;
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _ = InitAsync(ResolveProfileJid(e.Parameter as string));
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            TearDown();
        }

        public Task InitAsync(string jid)
        {
            jid = ResolveProfileJid(jid);
            if (string.IsNullOrWhiteSpace(jid))
            {
                // Do not wipe a good load with "Contact not found" when a race re-inits without JID.
                return Task.CompletedTask;
            }

            ProfileViewModel vm = ViewModel;
            if (vm == null)
            {
                return Task.CompletedTask;
            }

            return LoadAndBindAsync(vm, jid);
        }

        private static string ResolveProfileJid(string jid)
        {
            if (!string.IsNullOrWhiteSpace(jid))
            {
                return jid.Trim();
            }

            try
            {
                ShellViewModel shell = App.Services?.GetService<ShellViewModel>();
                if (shell == null)
                {
                    return null;
                }

                string pending = shell.PendingOpenProfileJid ?? shell.LastQueuedProfileJid;
                return string.IsNullOrWhiteSpace(pending) ? null : pending.Trim();
            }
            catch
            {
                return null;
            }
        }

        private async Task LoadAndBindAsync(ProfileViewModel vm, string jid)
        {
            SetLoading(true);
            try
            {
                await vm.InitAsync(jid);
            }
            finally
            {
                SetLoading(false);
                ApplyState();
            }
        }

        private void ProfileView_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyCommandBarLabels();
            ApplyState();
        }

        private void ProfileView_Unloaded(object sender, RoutedEventArgs e) => UnhookInfo();

        private void ProfileCommandBar_Loaded(object sender, RoutedEventArgs e)
        {
            LegacyCommandBarLayout.CenterPrimaryCommands(sender as CommandBar);
        }

        private void TearDown()
        {
            UnhookInfo();
            ViewModel?.Uninitialize();
            if (UserInfoPanel != null)
            {
                UserInfoPanel.InfoViewModel = null;
            }
        }

        private void ApplyState()
        {
            ProfileViewModel vm = ViewModel;
            if (vm == null)
            {
                return;
            }

            UnhookInfo();
            _hookedInfo = vm.Info;
            if (UserInfoPanel != null)
            {
                UserInfoPanel.InfoViewModel = _hookedInfo;
            }

            HookInfo();
            ApplyPinLabels();

            bool hasError = !string.IsNullOrWhiteSpace(vm.ErrorMessage);
            if (ErrorLabel != null)
            {
                ErrorLabel.Text = vm.ErrorMessage ?? string.Empty;
                ErrorLabel.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
            }

            if (UserInfoPanel != null)
            {
                UserInfoPanel.Visibility = hasError || vm.IsLoading
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            if (StartChatButton != null)
            {
                StartChatButton.IsEnabled = vm.StartChatCommand?.CanExecute(null) == true;
            }

            ApplyPinLabels();
        }

        private void SetLoading(bool loading)
        {
            if (LoadingRing == null)
            {
                return;
            }

            LoadingRing.IsActive = loading;
            LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        }

        private void HookInfo()
        {
            if (_hookedInfo == null || _infoHooked)
            {
                return;
            }

            _hookedInfo.PropertyChanged += Info_PropertyChanged;
            _infoHooked = true;
        }

        private void UnhookInfo()
        {
            if (_hookedInfo == null || !_infoHooked)
            {
                return;
            }

            _hookedInfo.PropertyChanged -= Info_PropertyChanged;
            _infoHooked = false;
        }

        private void Info_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChatDetailInfoViewModel.IsContactWidgetPinned) ||
                e.PropertyName == nameof(ChatDetailInfoViewModel.DisplayName) ||
                string.IsNullOrEmpty(e.PropertyName))
            {
                ApplyPinLabels();
                if (TitleLabel != null)
                {
                    TitleLabel.Text = ViewModel?.DisplayTitle ?? string.Empty;
                }
            }
        }

        private void ApplyCommandBarLabels()
        {
            if (StartChatButton != null)
            {
                StartChatButton.Label = LocalizedStrings.Get(
                    "Profile_StartChatAppBarLabel.Text",
                    "Start chat");
            }

            ApplyPinLabels();
        }

        private void ApplyPinLabels()
        {
            bool liveTiles = ViewModel?.Info?.AreLiveTilesAvailable != false;

            if (PinContactButton != null)
            {
                bool contactPinned = ViewModel?.Info?.IsContactWidgetPinned == true;
                bool canPinContact = ViewModel?.PinContactToStartCommand?.CanExecute(null) == true;
                PinContactButton.Visibility = liveTiles && (canPinContact || contactPinned)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                PinContactButton.Label = contactPinned
                    ? LocalizedStrings.Get("ChatDetailInfo_UnpinContactAppBarLabel.Text", "Unpin contact")
                    : LocalizedStrings.Get("ChatDetailInfo_PinContactAppBarLabel.Text", "Pin contact");
                PinContactButton.IsEnabled = canPinContact;
            }
        }

        private void PinContactButton_Click(object sender, RoutedEventArgs e)
        {
            var command = ViewModel?.PinContactToStartCommand;
            if (command != null && command.CanExecute(null))
            {
                command.Execute(null);
            }
        }
    }
}
