using System;
using System.ComponentModel;
using System.Windows.Input;
using Unison.Core.ViewModels;
using Unison.Uwp.Helpers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Chat-side profile / group info pane. Host supplies <see cref="InfoViewModel"/> and
    /// <see cref="CloseCommand"/> from <see cref="ChatDetailViewModel"/>.
    /// </summary>
    public sealed partial class ChatDetailInfoControl : UserControl
    {
        public static readonly DependencyProperty InfoViewModelProperty =
            DependencyProperty.Register(
                nameof(InfoViewModel),
                typeof(ChatDetailInfoViewModel),
                typeof(ChatDetailInfoControl),
                new PropertyMetadata(null, OnInfoViewModelChanged));

        public static readonly DependencyProperty CloseCommandProperty =
            DependencyProperty.Register(
                nameof(CloseCommand),
                typeof(ICommand),
                typeof(ChatDetailInfoControl),
                new PropertyMetadata(null));

        private ChatDetailInfoViewModel _boundInfo;

        public ChatDetailInfoControl()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                ApplyInfoViewModel();
                StretchProfileScrollViewer();
            };
        }

        public ChatDetailInfoViewModel InfoViewModel
        {
            get { return (ChatDetailInfoViewModel)GetValue(InfoViewModelProperty); }
            set { SetValue(InfoViewModelProperty, value); }
        }

        public ICommand CloseCommand
        {
            get { return (ICommand)GetValue(CloseCommandProperty); }
            set { SetValue(CloseCommandProperty, value); }
        }

        private void InfoBodyHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            StretchProfileScrollViewer();
        }

        private void InfoPivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            StretchProfileScrollViewer();
        }

        /// <summary>
        /// Pivot does not stretch item content; size the Profile ScrollViewer to the
        /// measured Pivot items host (full width + remaining height under headers).
        /// </summary>
        private void StretchProfileScrollViewer()
        {
            if (ProfileScrollViewer == null || InfoBodyHost == null)
            {
                return;
            }

            double width = InfoBodyHost.ActualWidth;
            double height = InfoBodyHost.ActualHeight;

            FrameworkElement itemsHost = FindPivotItemsHost(InfoPivot);
            if (itemsHost != null && itemsHost.ActualWidth > 0)
            {
                width = itemsHost.ActualWidth;
            }

            if (itemsHost != null && itemsHost.ActualHeight > 0)
            {
                height = itemsHost.ActualHeight;
            }
            else if (InfoPivot != null && InfoPivot.ActualHeight > 0)
            {
                // Headers strip ~48–56px when host size is unavailable yet.
                height = Math.Max(0, InfoPivot.ActualHeight - 52);
                width = InfoPivot.ActualWidth > 0 ? InfoPivot.ActualWidth : width;
            }

            if (width <= 0 || height <= 0)
            {
                return;
            }

            if (Math.Abs(ProfileScrollViewer.Width - width) > 0.5)
            {
                ProfileScrollViewer.Width = width;
            }

            if (Math.Abs(ProfileScrollViewer.Height - height) > 0.5)
            {
                ProfileScrollViewer.Height = height;
            }
        }

        private static FrameworkElement FindPivotItemsHost(Pivot pivot)
        {
            if (pivot == null)
            {
                return null;
            }

            // Prefer the panel that actually lays out PivotItems.
            FrameworkElement panel = FindDescendantByName(pivot, "Panel") as FrameworkElement;
            if (panel != null && panel.ActualHeight > 0)
            {
                return panel;
            }

            return FindFirstDescendant<ItemsPresenter>(pivot);
        }

        private static T FindFirstDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null)
            {
                return null;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                {
                    return match;
                }

                T deeper = FindFirstDescendant<T>(child);
                if (deeper != null)
                {
                    return deeper;
                }
            }

            return null;
        }

        private static DependencyObject FindDescendantByName(DependencyObject root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement fe &&
                    string.Equals(fe.Name, name, StringComparison.Ordinal))
                {
                    return child;
                }

                DependencyObject deeper = FindDescendantByName(child, name);
                if (deeper != null)
                {
                    return deeper;
                }
            }

            return null;
        }

        private static void OnInfoViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as ChatDetailInfoControl;
            control?.OnInfoViewModelChanged(e.OldValue as ChatDetailInfoViewModel, e.NewValue as ChatDetailInfoViewModel);
        }

        private void OnInfoViewModelChanged(ChatDetailInfoViewModel oldVm, ChatDetailInfoViewModel newVm)
        {
            if (_boundInfo != null)
            {
                _boundInfo.PropertyChanged -= Info_PropertyChanged;
            }

            _boundInfo = newVm;
            if (_boundInfo != null)
            {
                _boundInfo.PropertyChanged += Info_PropertyChanged;
            }

            ApplyInfoViewModel();
            StretchProfileScrollViewer();
        }

        private void Info_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            ApplyInfoViewModel();
        }

        private void ApplyInfoViewModel()
        {
            var vm = InfoViewModel;
            if (vm == null || ProfilePivotItem == null)
            {
                return;
            }

            ProfilePivotItem.Header = Upper(vm.ProfilePivotHeader);
            FilesPivotItem.Header = Upper(LocalizedOr("ChatDetailInfo_Files", "Files"));
            CallsPivotItem.Header = Upper(LocalizedOr("ChatDetailInfo_Calls", "Calls"));
            CallsPivotItem.Visibility = vm.IsUser ? Visibility.Visible : Visibility.Collapsed;

            if (InfoAvatar != null)
            {
                InfoAvatar.AvatarUrl = vm.AvatarUrl;
                InfoAvatar.IsGroup = vm.IsGroup;
            }

            if (NameLabel != null)
            {
                NameLabel.Text = Upper(vm.NameSectionLabel);
            }

            if (NameValue != null)
            {
                NameValue.Text = vm.DisplayName ?? string.Empty;
            }

            if (PhoneSection != null)
            {
                PhoneSection.Visibility = vm.IsUser ? Visibility.Visible : Visibility.Collapsed;
            }

            if (PhoneLabel != null)
            {
                PhoneLabel.Text = Upper(vm.PhoneSectionLabel);
            }

            if (PhoneValue != null)
            {
                PhoneValue.Text = string.IsNullOrWhiteSpace(vm.PhoneValue)
                    ? "—"
                    : vm.PhoneValue;
            }

            if (StatusLabel != null)
            {
                StatusLabel.Text = Upper(vm.StatusSectionLabel);
            }

            if (StatusValue != null)
            {
                StatusValue.Text = vm.HasStatusOrDescription
                    ? vm.StatusOrDescription
                    : "—";
            }

            if (NotificationsLabel != null)
            {
                NotificationsLabel.Text = Upper(vm.NotificationsSectionLabel);
            }

            if (NotificationsValue != null)
            {
                NotificationsValue.Text = vm.NotificationsValue ?? string.Empty;
            }

            if (PinButton != null)
            {
                string pinLabel = vm.PinMenuLabel ?? "Pin to\nStart";
                if (PinButtonLabel != null)
                {
                    PinButtonLabel.Text = pinLabel;
                }

                // Tooltip keeps a single-line friendly form.
                string tip = pinLabel.Replace('\n', ' ').Replace("  ", " ").Trim();
                ToolTipService.SetToolTip(PinButton, tip);
                PinButton.IsEnabled = vm.PinToStartCommand?.CanExecute(null) == true;
            }
        }

        private static string Upper(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.ToUpperInvariant();
        }

        private static string LocalizedOr(string key, string fallback)
        {
            try
            {
                return LocalizedStrings.Get(key, fallback);
            }
            catch
            {
                return fallback;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (CloseCommand?.CanExecute(null) == true)
            {
                CloseCommand.Execute(null);
            }
        }

        private void PinButton_Click(object sender, RoutedEventArgs e)
        {
            var cmd = InfoViewModel?.PinToStartCommand;
            if (cmd?.CanExecute(null) == true)
            {
                cmd.Execute(null);
            }
        }
    }
}
