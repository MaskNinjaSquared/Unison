using System;
using System.Collections;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Generic contact/chat picker overlay. Hosts pass a title, collection, and click command —
    /// open presentation (compact rows, deferred bind, fade) comes from the active shell theme strategy.
    /// </summary>
    public sealed partial class ContactsVisualizerControl : UserControl
    {
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(
                nameof(Title),
                typeof(string),
                typeof(ContactsVisualizerControl),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register(
                nameof(ItemsSource),
                typeof(IEnumerable),
                typeof(ContactsVisualizerControl),
                new PropertyMetadata(null, OnItemsSourceChanged));

        public static readonly DependencyProperty ItemClickCommandProperty =
            DependencyProperty.Register(
                nameof(ItemClickCommand),
                typeof(ICommand),
                typeof(ContactsVisualizerControl),
                new PropertyMetadata(null));

        public static readonly DependencyProperty BackCommandProperty =
            DependencyProperty.Register(
                nameof(BackCommand),
                typeof(ICommand),
                typeof(ContactsVisualizerControl),
                new PropertyMetadata(null));

        public static readonly DependencyProperty IsOpenProperty =
            DependencyProperty.Register(
                nameof(IsOpen),
                typeof(bool),
                typeof(ContactsVisualizerControl),
                new PropertyMetadata(false, OnIsOpenChanged));

        private bool _backHooked;
        private bool _openInProgress;

        public ContactsVisualizerControl()
        {
            InitializeComponent();
            Visibility = Visibility.Collapsed;
            Opacity = 1;
            Unloaded += ContactsVisualizerControl_Unloaded;
        }

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public IEnumerable ItemsSource
        {
            get => (IEnumerable)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public ICommand ItemClickCommand
        {
            get => (ICommand)GetValue(ItemClickCommandProperty);
            set => SetValue(ItemClickCommandProperty, value);
        }

        public ICommand BackCommand
        {
            get => (ICommand)GetValue(BackCommandProperty);
            set => SetValue(BackCommandProperty, value);
        }

        public bool IsOpen
        {
            get => (bool)GetValue(IsOpenProperty);
            set => SetValue(IsOpenProperty, value);
        }

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as ContactsVisualizerControl;
            if (control == null || !control.IsOpen)
            {
                return;
            }

            // Share Target cold open often swaps ItemsSource while BeginOpen's deferred
            // Low bind is still in flight (_openInProgress). Still push the latest DP so
            // the deferred callback and the ListView both see the filled catalog.
            control.ItemsList.ItemsSource = e.NewValue;
        }

        private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as ContactsVisualizerControl;
            if (control == null)
            {
                return;
            }

            if ((bool)e.NewValue)
            {
                control.BeginOpen();
            }
            else
            {
                control.BeginClose();
            }
        }

        private void BeginOpen()
        {
            _openInProgress = true;
            ApplyPickerTemplate();

            bool animate = ResolveTheme()?.ContactsPickerAnimatesOpen ?? true;
            bool defer = ResolveTheme()?.ContactsPickerDefersItemsUntilOpen ?? true;

            if (animate)
            {
                Opacity = 0;
            }

            Visibility = Visibility.Visible;
            HookBack();

            if (defer)
            {
                // Realize rows after the overlay surface is on screen (Mobile jank fix).
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    if (!IsOpen)
                    {
                        _openInProgress = false;
                        return;
                    }

                    ItemsList.ItemsSource = ItemsSource;
                    _openInProgress = false;
                    if (animate)
                    {
                        RunOpenFade();
                    }
                    else
                    {
                        Opacity = 1;
                    }
                });
                return;
            }

            ItemsList.ItemsSource = ItemsSource;
            _openInProgress = false;
            if (animate)
            {
                RunOpenFade();
            }
            else
            {
                Opacity = 1;
            }
        }

        private void BeginClose()
        {
            UnhookBack();
            ItemsList.ItemsSource = null;
            Opacity = 1;
            Visibility = Visibility.Collapsed;
            _openInProgress = false;
        }

        private void ApplyPickerTemplate()
        {
            bool compact = ResolveTheme()?.ContactsPickerUsesCompactRows ?? true;
            if (compact)
            {
                ItemsList.ItemTemplate = (DataTemplate)Resources["ContactsPickerCompactTemplate"];
                return;
            }

            if (Resources.ContainsKey("ChatItemTemplate"))
            {
                ItemsList.ItemTemplate = Resources["ChatItemTemplate"] as DataTemplate;
            }
        }

        private void RunOpenFade()
        {
            var anim = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(160),
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(anim, this);
            Storyboard.SetTargetProperty(anim, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(anim);
            sb.Begin();
        }

        private static IShellThemeService ResolveTheme()
        {
            try
            {
                return App.Services?.GetService<IShellThemeService>();
            }
            catch
            {
                return null;
            }
        }

        private void HookBack()
        {
            if (_backHooked)
            {
                return;
            }

            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            _backHooked = true;
        }

        private void UnhookBack()
        {
            if (!_backHooked)
            {
                return;
            }

            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
            _backHooked = false;
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (!IsOpen)
            {
                return;
            }

            e.Handled = true;
            RequestClose();
        }

        private void ContactsVisualizerControl_Unloaded(object sender, RoutedEventArgs e)
        {
            UnhookBack();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            RequestClose();
        }

        private void RequestClose()
        {
            if (BackCommand != null && BackCommand.CanExecute(null))
            {
                BackCommand.Execute(null);
                return;
            }

            IsOpen = false;
        }

        private void ItemsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var chat = e.ClickedItem as ChatItem;
            if (chat == null || ItemClickCommand == null)
            {
                return;
            }

            if (ItemClickCommand.CanExecute(chat))
            {
                ItemClickCommand.Execute(chat);
            }
        }
    }
}
