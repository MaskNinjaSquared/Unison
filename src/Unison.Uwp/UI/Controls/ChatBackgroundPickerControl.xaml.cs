// =============================================================================
// ChatBackgroundPickerControl
//
// Unison/WhatsApp: title + close top-right; ChatDetailInfo-style footer (glyph
// above, "Background" below). Legacy: WP8 bottom strip with Back + Wallpaper.
// Sections: BACKGROUND preview, BACKGROUNDS gallery, PERSONALIZAR doodles.
// =============================================================================
using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    public sealed partial class ChatBackgroundPickerControl : UserControl
    {
        public static readonly DependencyProperty UseLegacyCommandBarProperty =
            DependencyProperty.Register(
                nameof(UseLegacyCommandBar),
                typeof(bool),
                typeof(ChatBackgroundPickerControl),
                new PropertyMetadata(false, OnChromeChanged));

        private bool _syncingDoodlesToggle;
        private ChatDetailViewModel _boundVm;

        public ChatBackgroundPickerControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            DataContextChanged += OnDataContextChanged;
        }

        /// <summary>True on WhatsApp Legacy: bottom Back + Wallpaper; hide modern header/footer.</summary>
        public bool UseLegacyCommandBar
        {
            get { return (bool)GetValue(UseLegacyCommandBarProperty); }
            set { SetValue(UseLegacyCommandBarProperty, value); }
        }

        private ChatDetailViewModel ViewModel => DataContext as ChatDetailViewModel;

        private static void OnChromeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as ChatBackgroundPickerControl;
            control?.ApplyChrome();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyChrome();
            HookViewModel(ViewModel);
            SyncDoodlesToggle();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            HookViewModel(null);
        }

        private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            HookViewModel(args.NewValue as ChatDetailViewModel);
            SyncDoodlesToggle();
        }

        private void HookViewModel(ChatDetailViewModel vm)
        {
            if (ReferenceEquals(_boundVm, vm))
            {
                return;
            }

            if (_boundVm != null)
            {
                _boundVm.PropertyChanged -= ViewModel_PropertyChanged;
            }

            _boundVm = vm;
            if (_boundVm != null)
            {
                _boundVm.PropertyChanged += ViewModel_PropertyChanged;
            }
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChatDetailViewModel.PickerDoodlesOn) ||
                e.PropertyName == nameof(ChatDetailViewModel.PickerSelectedKind) ||
                e.PropertyName == nameof(ChatDetailViewModel.IsPickerDoodlesEnabled))
            {
                SyncDoodlesToggle();
            }
        }

        private void SyncDoodlesToggle()
        {
            if (DoodlesToggle == null)
            {
                return;
            }

            ChatDetailViewModel vm = ViewModel;
            bool on = vm != null && vm.PickerDoodlesOn;
            if (DoodlesToggle.IsOn == on)
            {
                return;
            }

            _syncingDoodlesToggle = true;
            try
            {
                DoodlesToggle.IsOn = on;
            }
            finally
            {
                _syncingDoodlesToggle = false;
            }
        }

        private void ApplyChrome()
        {
            if (ModernHeader == null || ModernFooter == null || LegacyFooter == null)
            {
                return;
            }

            bool legacy = UseLegacyCommandBar;
            ModernHeader.Visibility = legacy ? Visibility.Collapsed : Visibility.Visible;
            ModernFooter.Visibility = legacy ? Visibility.Collapsed : Visibility.Visible;
            LegacyFooter.Visibility = legacy ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            ChatDetailViewModel vm = ViewModel;
            if (vm == null)
            {
                return;
            }

            if (vm.IsSolidColorPickerOpen)
            {
                vm.CloseSolidColorPicker();
                return;
            }

            vm.CloseChatBackgroundPicker();
        }

        private void SolidColorBack_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CloseSolidColorPicker();
        }

        private void SolidColorRow_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.OpenSolidColorPicker();
        }

        private void PresetGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            var option = e.ClickedItem as ChatBackgroundOption;
            if (option == null || ViewModel == null)
            {
                return;
            }

            _ = ViewModel.SelectBackgroundOptionAsync(option);
        }

        private void SolidColorGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            var option = e.ClickedItem as ChatBackgroundSolidColorOption;
            if (option == null || ViewModel == null)
            {
                return;
            }

            _ = ViewModel.SelectSolidColorOptionAsync(option);
        }

        private void DoodlesToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncingDoodlesToggle || ViewModel == null || DoodlesToggle == null)
            {
                return;
            }

            if (!ViewModel.IsPickerDoodlesEnabled)
            {
                SyncDoodlesToggle();
                return;
            }

            _ = ViewModel.SetBackgroundDoodlesAsync(DoodlesToggle.IsOn);
        }

        private async void WallpaperButton_Click(object sender, RoutedEventArgs e)
        {
            ChatDetailViewModel vm = ViewModel;
            ChatItem chat = vm?.ActiveChat;
            if (vm == null || chat == null)
            {
                return;
            }

            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.Thumbnail,
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");

            StorageFile file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }

            try
            {
                string relative = await CopyWallpaperAsync(chat, file).ConfigureAwait(true);
                await vm.ApplyCustomBackgroundPathAsync(relative).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatBackground] pick failed: " + ex.Message);
            }
        }

        private static async Task<string> CopyWallpaperAsync(ChatItem chat, StorageFile file)
        {
            string jidKey = (chat.JID ?? chat.Id ?? "chat").Replace('@', '_').Replace(':', '_');
            StorageFolder root = ApplicationData.Current.LocalFolder;
            StorageFolder folder = await root
                .CreateFolderAsync("ChatBackgrounds", CreationCollisionOption.OpenIfExists)
                .AsTask()
                .ConfigureAwait(true);
            StorageFolder chatFolder = await folder
                .CreateFolderAsync(jidKey, CreationCollisionOption.OpenIfExists)
                .AsTask()
                .ConfigureAwait(true);

            string ext = Path.GetExtension(file.Name);
            if (string.IsNullOrEmpty(ext))
            {
                ext = ".jpg";
            }

            string name = "wall" + ext;
            StorageFile dest = await file
                .CopyAsync(chatFolder, name, NameCollisionOption.ReplaceExisting)
                .AsTask()
                .ConfigureAwait(true);

            return Path.Combine("ChatBackgrounds", jidKey, dest.Name);
        }
    }
}
