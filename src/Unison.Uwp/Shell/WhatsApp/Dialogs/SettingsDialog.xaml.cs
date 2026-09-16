using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.ViewModels;
using Unison.Uwp.Services;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace Unison.Uwp.Shell.WhatsApp.Dialogs
{
    /// <summary>
    /// WhatsApp-shell settings overlay. Phone handset uses FullSizeDesired (Imgur) and
    /// never sizes BackgroundElement. Desktop uses chrome Width + Overlay/Inline panes.
    /// Underlay tap dismisses.
    /// </summary>
    public sealed partial class SettingsDialog : ContentDialog
    {
        private const double NarrowBelow = 600;
        private const double FullModeAtOrAbove = 800;
        private const double CompactBoxSize = 600;
        private const double FullBoxMinWidth = 800;
        private const double DialogMaxWidthCap = 960;
        private const double DialogMaxHeightCap = 720;

        private SettingsViewModel _viewModel;
        private bool _syncingLanguageCombo;
        private bool _leaveHooked;
        private bool _suppressSectionSync;
        private bool _windowSizeHooked;
        private string _layoutState;
        private UIElement _underlay;
        private FrameworkElement _dialogChrome;

        public SettingsDialog()
        {
            InitializeComponent();
            PrimaryButtonText = string.Empty;
            SecondaryButtonText = string.Empty;
            CloseButtonText = string.Empty;

            // FullSizeDesired before ShowAsync (handset only — Imgur pattern).
            ApplyOpenSizing(GetHostWidth());

            Opened += SettingsDialog_Opened;
            Closed += SettingsDialog_Closed;
        }

        public void Bind(SettingsViewModel viewModel)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = _viewModel;
        }

        private void SettingsDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            HookWindowSizeChanged();
            HookUnderlayDismiss();
            _dialogChrome = null;
            ApplyLayout(GetHostWidth(), allowFullSizeDesired: false);
            Activate();
            HookLeaveRequested();
            ShowSection("general");
        }

        private void SettingsDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            UnhookUnderlayDismiss();
            UnhookWindowSizeChanged();
            UnhookLeaveRequested();
            _dialogChrome = null;
            try
            {
                App.Services?.GetService<ShellViewModel>()
                    ?.NavigateToSectionCommand.Execute(NavigationRoutes.Chats);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppSettingsDialog] reset section failed: " + ex.Message);
            }
        }

        private void LayoutStates_CurrentStateChanged(object sender, VisualStateChangedEventArgs e)
        {
            ApplyChromeSize(GetHostWidth());
            ApplyDialogSizeKeys(GetHostWidth());
        }

        private void HookUnderlayDismiss()
        {
            UnhookUnderlayDismiss();

            try
            {
                UIElement smoke = GetTemplateChild("SmokeLayerBackground") as UIElement;
                if (smoke == null)
                {
                    smoke = FindOpenPopupSmoke();
                }

                if (smoke == null)
                {
                    Debug.WriteLine("[WhatsAppSettingsDialog] underlay smoke not found");
                    return;
                }

                _underlay = smoke;
                _underlay.Tapped += Underlay_Tapped;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppSettingsDialog] underlay hook failed: " + ex.Message);
            }
        }

        private void UnhookUnderlayDismiss()
        {
            if (_underlay == null)
            {
                return;
            }

            try
            {
                _underlay.Tapped -= Underlay_Tapped;
            }
            catch
            {
            }

            _underlay = null;
        }

        private void Underlay_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            Hide();
        }

        private static UIElement FindOpenPopupSmoke()
        {
            var popups = VisualTreeHelper.GetOpenPopups(Window.Current);
            foreach (var popup in popups)
            {
                UIElement child = popup.Child;
                if (child == null)
                {
                    continue;
                }

                if (child is Rectangle)
                {
                    return child;
                }

                int count = VisualTreeHelper.GetChildrenCount(child);
                for (int i = 0; i < count; i++)
                {
                    DependencyObject nested = VisualTreeHelper.GetChild(child, i);
                    if (nested is Rectangle rect)
                    {
                        return rect;
                    }
                }
            }

            return null;
        }

        private void HookWindowSizeChanged()
        {
            if (_windowSizeHooked)
            {
                return;
            }

            try
            {
                Window.Current.SizeChanged += Window_SizeChanged;
                _windowSizeHooked = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppSettingsDialog] SizeChanged hook failed: " + ex.Message);
            }
        }

        private void UnhookWindowSizeChanged()
        {
            if (!_windowSizeHooked)
            {
                return;
            }

            try
            {
                Window.Current.SizeChanged -= Window_SizeChanged;
            }
            catch
            {
            }

            _windowSizeHooked = false;
        }

        private void Window_SizeChanged(object sender, WindowSizeChangedEventArgs e)
        {
            // Do not toggle FullSizeDesired after ShowAsync.
            ApplyLayout(GetHostWidth(), allowFullSizeDesired: false);
        }

        /// <summary>
        /// Windows 10 Mobile handset (not Continuum) — same gate as Imgur / MainView.
        /// </summary>
        private static bool IsPhoneHandset()
        {
            try
            {
                ISystemInfoProvider info = App.Services?.GetService<ISystemInfoProvider>();
                if (info != null)
                {
                    return info.IsMobile() && !info.IsContinuum();
                }

                return SystemInfoProvider.DetectIsMobile();
            }
            catch
            {
                return SystemInfoProvider.DetectIsMobile();
            }
        }

        private static Rect GetHostBounds()
        {
            try
            {
                if (IsPhoneHandset())
                {
                    return ApplicationView.GetForCurrentView().VisibleBounds;
                }
            }
            catch
            {
            }

            try
            {
                return Window.Current.Bounds;
            }
            catch
            {
                return new Rect(0, 0, NarrowBelow, DialogMaxHeightCap);
            }
        }

        private static double GetHostWidth()
        {
            return GetHostBounds().Width;
        }

        private static double GetHostHeight()
        {
            return GetHostBounds().Height;
        }

        /// <summary>
        /// Once, before ShowAsync. Never set MinWidth/MaxWidth on this ContentDialog.
        /// </summary>
        private void ApplyOpenSizing(double width)
        {
            ClearValue(WidthProperty);
            ClearValue(HeightProperty);
            ClearValue(MinWidthProperty);
            ClearValue(MaxWidthProperty);
            ClearValue(MaxHeightProperty);

            MinHeight = 400;
            ApplyLayout(width, allowFullSizeDesired: true);
        }

        private void ApplyLayout(double width, bool allowFullSizeDesired)
        {
            if (allowFullSizeDesired)
            {
                // Imgur: full-bleed only on phone handset — not Continuum / desktop narrow.
                FullSizeDesired = IsPhoneHandset();
            }

            ApplyDialogSizeKeys(width);
            ApplyChromeSize(width);
            ApplyRootBoxSize(width);
            ApplyPaneMode(width);
        }

        private void ApplyPaneMode(double width)
        {
            // Handset always Overlay + hamburger (never Inline 800 chrome).
            bool handset = IsPhoneHandset() || FullSizeDesired;
            bool full = !handset && width >= FullModeAtOrAbove;
            string next = handset || width < NarrowBelow
                ? "Phone"
                : full ? "Full" : "Compact";

            bool enteredOverlay = !string.Equals(_layoutState, next, StringComparison.Ordinal) &&
                                  (string.Equals(next, "Phone", StringComparison.Ordinal) ||
                                   string.Equals(next, "Compact", StringComparison.Ordinal));
            bool leftFull = string.Equals(_layoutState, "Full", StringComparison.Ordinal);

            if (!string.Equals(_layoutState, next, StringComparison.Ordinal))
            {
                _layoutState = next;
            }

            if (RootSplitView != null)
            {
                if (full)
                {
                    RootSplitView.DisplayMode = SplitViewDisplayMode.Inline;
                    RootSplitView.IsPaneOpen = true;
                    RootSplitView.OpenPaneLength = 260;
                    try
                    {
                        RootSplitView.PaneBackground =
                            Application.Current.Resources["SettingsDialogPaneBackgroundBrush"] as Brush
                            ?? RootSplitView.PaneBackground;
                    }
                    catch
                    {
                    }
                }
                else
                {
                    RootSplitView.DisplayMode = SplitViewDisplayMode.Overlay;
                    RootSplitView.OpenPaneLength = 240;
                    try
                    {
                        RootSplitView.PaneBackground =
                            Application.Current.Resources["AcrylicInAppFillColorDefaultBrush"] as Brush
                            ?? RootSplitView.PaneBackground;
                    }
                    catch
                    {
                    }

                    if (enteredOverlay && leftFull)
                    {
                        RootSplitView.IsPaneOpen = false;
                    }
                }
            }

            if (PaneToggleButton != null)
            {
                PaneToggleButton.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void ApplyDialogSizeKeys(double width)
        {
            double minKey;
            double maxKey;
            double maxHeight = DialogMaxHeightCap;

            if (FullSizeDesired || IsPhoneHandset())
            {
                minKey = 320;
                maxKey = Math.Max(width, 320);
                maxHeight = Math.Max(GetHostHeight(), 400);
            }
            else if (width < NarrowBelow)
            {
                minKey = 320;
                maxKey = Math.Max(width, 320);
            }
            else if (width < FullModeAtOrAbove)
            {
                minKey = CompactBoxSize;
                maxKey = CompactBoxSize;
            }
            else
            {
                minKey = FullBoxMinWidth;
                maxKey = DialogMaxWidthCap;
            }

            Resources["ContentDialogMinWidth"] = minKey;
            Resources["ContentDialogMaxWidth"] = maxKey;
            Resources["ContentDialogMaxHeight"] = maxHeight;
        }

        /// <summary>
        /// Sizes BackgroundElement on desktop only. Handset + FullSizeDesired: leave alone
        /// (touching chrome collapses the dialog on W10M).
        /// </summary>
        private void ApplyChromeSize(double hostWidth)
        {
            if (FullSizeDesired || IsPhoneHandset())
            {
                return;
            }

            if (_dialogChrome == null)
            {
                _dialogChrome = GetTemplateChild("BackgroundElement") as FrameworkElement;
            }

            FrameworkElement chrome = _dialogChrome;
            if (chrome == null)
            {
                return;
            }

            chrome.ClearValue(FrameworkElement.HeightProperty);
            chrome.ClearValue(FrameworkElement.MaxWidthProperty);
            chrome.ClearValue(FrameworkElement.MinWidthProperty);
            chrome.ClearValue(FrameworkElement.WidthProperty);
            chrome.ClearValue(FrameworkElement.MaxHeightProperty);
            chrome.MinHeight = 400;

            if (hostWidth < NarrowBelow)
            {
                chrome.Width = hostWidth;
                chrome.MaxWidth = hostWidth;
                chrome.MaxHeight = DialogMaxHeightCap;
            }
            else if (hostWidth < FullModeAtOrAbove)
            {
                chrome.MinWidth = CompactBoxSize;
                chrome.MaxWidth = CompactBoxSize;
                chrome.Width = CompactBoxSize;
                chrome.MaxHeight = DialogMaxHeightCap;
            }
            else
            {
                double target = Math.Min(Math.Max(hostWidth * 0.85, FullBoxMinWidth), DialogMaxWidthCap);
                chrome.MinWidth = FullBoxMinWidth;
                chrome.MaxWidth = DialogMaxWidthCap;
                chrome.Width = target;
                chrome.MaxHeight = DialogMaxHeightCap;
            }
        }

        private void ApplyRootBoxSize(double width)
        {
            if (RootLayout == null)
            {
                return;
            }

            RootLayout.ClearValue(FrameworkElement.WidthProperty);
            RootLayout.ClearValue(FrameworkElement.HeightProperty);
            RootLayout.ClearValue(FrameworkElement.MinWidthProperty);
            RootLayout.ClearValue(FrameworkElement.MaxWidthProperty);
            RootLayout.HorizontalAlignment = HorizontalAlignment.Stretch;
            RootLayout.VerticalAlignment = VerticalAlignment.Stretch;
            RootLayout.MinHeight = 400;

            if (FullSizeDesired || IsPhoneHandset())
            {
                RootLayout.ClearValue(FrameworkElement.MaxHeightProperty);
            }
            else
            {
                RootLayout.MaxHeight = DialogMaxHeightCap;
            }
        }

        private void HookLeaveRequested()
        {
            if (_viewModel == null || _leaveHooked)
            {
                return;
            }

            _viewModel.LeaveRequested += ViewModel_LeaveRequested;
            _leaveHooked = true;
        }

        private void UnhookLeaveRequested()
        {
            if (!_leaveHooked || _viewModel == null)
            {
                return;
            }

            _viewModel.LeaveRequested -= ViewModel_LeaveRequested;
            _leaveHooked = false;
        }

        private void ViewModel_LeaveRequested(object sender, EventArgs e)
        {
            Hide();
        }

        /// <summary>
        /// UWP allows only one ContentDialog. Close settings before the disconnect confirm runs.
        /// </summary>
        private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();

            // Yield so Hide() releases the dialog slot before ShowConfirmAsync in the ViewModel.
            await Task.Yield();

            try
            {
                if (_viewModel?.DisconnectCommand?.CanExecute(null) == true)
                {
                    _viewModel.DisconnectCommand.Execute(null);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppSettingsDialog] Disconnect click failed: " + ex.Message);
            }
        }

        private void PaneToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (RootSplitView != null)
            {
                RootSplitView.IsPaneOpen = !RootSplitView.IsPaneOpen;
            }
        }

        private void SectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSectionSync || SectionList?.SelectedItem == null)
            {
                return;
            }

            var item = SectionList.SelectedItem as ListViewItem;
            string tag = item?.Tag as string;
            if (string.IsNullOrEmpty(tag))
            {
                return;
            }

            ShowSection(tag);

            if (RootSplitView != null &&
                RootSplitView.DisplayMode == SplitViewDisplayMode.Overlay)
            {
                RootSplitView.IsPaneOpen = false;
            }
        }

        private void ShowSection(string tag)
        {
            if (GeneralPanel != null)
            {
                GeneralPanel.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
            }

            if (NotificationsPanel != null)
            {
                NotificationsPanel.Visibility = tag == "notifications" ? Visibility.Visible : Visibility.Collapsed;
            }

            if (CustomizationPanel != null)
            {
                CustomizationPanel.Visibility = tag == "customization" ? Visibility.Visible : Visibility.Collapsed;
            }

            if (AdvancedPanel != null)
            {
                AdvancedPanel.Visibility = tag == "advanced" ? Visibility.Visible : Visibility.Collapsed;
            }

            if (AboutPanel != null)
            {
                AboutPanel.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
            }

            if (SectionTitleText != null)
            {
                SectionTitleText.Text = ResolveSectionTitle(tag);
            }

            _suppressSectionSync = true;
            try
            {
                if (SectionList != null)
                {
                    foreach (var obj in SectionList.Items)
                    {
                        var listItem = obj as ListViewItem;
                        if (listItem != null &&
                            string.Equals(listItem.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
                        {
                            SectionList.SelectedItem = listItem;
                            break;
                        }
                    }
                }
            }
            finally
            {
                _suppressSectionSync = false;
            }
        }

        private static string ResolveSectionTitle(string tag)
        {
            IStringResources strings = null;
            try
            {
                strings = App.Services?.GetService<IStringResources>();
            }
            catch
            {
            }

            switch (tag)
            {
                case "notifications":
                    return Loc(strings, "Settings_NotificationsSection.Text", "Notifications");
                case "customization":
                    return Loc(strings, "Settings_Customization.Text", "Customization");
                case "advanced":
                    return Loc(strings, "Settings_Advanced.Text", "Advanced");
                case "about":
                    return Loc(strings, "Settings_About.Text", "About");
                default:
                    return Loc(strings, "Settings_General.Text", "General");
            }
        }

        private static string Loc(IStringResources strings, string key, string fallback)
        {
            if (strings == null)
            {
                return fallback;
            }

            string value = strings.Get(key, fallback);
            if (string.IsNullOrWhiteSpace(value) ||
                value.StartsWith("Settings_", StringComparison.Ordinal))
            {
                return fallback;
            }

            return value;
        }

        private void Activate()
        {
            string version = "?";
            try
            {
                var v = Package.Current.Id.Version;
                version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch
            {
            }

            _syncingLanguageCombo = true;
            try
            {
                _viewModel?.Initialize(version);
                SyncLanguageComboSelection();
            }
            finally
            {
                _syncingLanguageCombo = false;
            }
        }

        private void SyncLanguageComboSelection()
        {
            if (_viewModel == null || LanguageComboBox == null)
            {
                return;
            }

            int index = _viewModel.SelectedLanguageIndex;
            if (index < 0 || index >= LanguageComboBox.Items.Count)
            {
                return;
            }

            bool wasSyncing = _syncingLanguageCombo;
            _syncingLanguageCombo = true;
            try
            {
                LanguageComboBox.SelectedIndex = index;
            }
            finally
            {
                _syncingLanguageCombo = wasSyncing;
            }
        }

        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingLanguageCombo || _viewModel == null || LanguageComboBox == null)
            {
                return;
            }

            int index = LanguageComboBox.SelectedIndex;
            if (index < 0 || index == _viewModel.SelectedLanguageIndex)
            {
                return;
            }

            Debug.WriteLine("[WhatsAppSettingsDialog] LanguageComboBox → index=" + index);
            if (_viewModel.ChangeLanguageCommand != null &&
                _viewModel.ChangeLanguageCommand.CanExecute(index))
            {
                _viewModel.ChangeLanguageCommand.Execute(index);
            }
        }
    }
}
