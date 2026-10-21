using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unison.Uwp.UI.Views
{
    /// <summary>
    /// Full-page contact profile for People / <c>profile=</c> activation.
    /// Same fields as the chat-side info pane; bottom bar is Start chat + Pin contact.
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
            string jid = e.Parameter as string;
            _ = InitAsync(jid);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            UnhookInfo();
            ViewModel?.Uninitialize();
            if (UserInfoPanel != null)
            {
                UserInfoPanel.InfoViewModel = null;
            }
        }

        /// <summary>Legacy shells call this from their pivot Init path when needed.</summary>
        public Task InitAsync(string jid)
        {
            ProfileViewModel vm = ViewModel;
            if (vm == null)
            {
                return Task.CompletedTask;
            }

            return LoadAndBindAsync(vm, jid);
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
            ApplyState();
        }

        private void ProfileView_Unloaded(object sender, RoutedEventArgs e)
        {
            UnhookInfo();
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
            if (LoadingRing != null)
            {
                LoadingRing.IsActive = loading;
                LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            }
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
            }
        }

        private void ApplyPinLabels()
        {
            bool liveTiles = ViewModel?.Info?.AreLiveTilesAvailable != false;
            bool contactPinned = ViewModel?.Info?.IsContactWidgetPinned == true;
            bool canPinContact = ViewModel?.PinContactToStartCommand?.CanExecute(null) == true;
            if (PinContactButton != null)
            {
                PinContactButton.Visibility = liveTiles && (canPinContact || contactPinned)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                PinContactButton.IsEnabled = canPinContact;
            }

            if (PinContactButtonLabel != null)
            {
                PinContactButtonLabel.Visibility = contactPinned ? Visibility.Collapsed : Visibility.Visible;
            }

            if (UnpinContactButtonLabel != null)
            {
                UnpinContactButtonLabel.Visibility = contactPinned ? Visibility.Visible : Visibility.Collapsed;
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
