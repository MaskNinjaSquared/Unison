using System;
using System.ComponentModel;
using Unison.Core.ViewModels;
using Unison.Uwp.UI.Controls;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Animation;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Controls
{
    /// <summary>
    /// Full-bleed Legacy "new status" dock hosted on <c>ShellView</c> (not inside the Status pivot).
    /// </summary>
    public sealed partial class NewStatusPickerOverlay : UserControl
    {
        public static readonly DependencyProperty ViewModelProperty =
            DependencyProperty.Register(
                nameof(ViewModel),
                typeof(StatusListViewModel),
                typeof(NewStatusPickerOverlay),
                new PropertyMetadata(null, OnViewModelChanged));

        private static readonly TimeSpan DockDuration = TimeSpan.FromMilliseconds(220);
        private const double TileMaxSize = 150;

        private StatusListViewModel _hookedVm;
        private Storyboard _storyboard;
        private bool _dockOpen;

        public NewStatusPickerOverlay()
        {
            InitializeComponent();
            Visibility = Visibility.Collapsed;
            SizeChanged += (_, e) => ApplyTileSize(e.NewSize.Width);
        }

        public StatusListViewModel ViewModel
        {
            get => (StatusListViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }

        private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((NewStatusPickerOverlay)d).Attach(e.NewValue as StatusListViewModel);
        }

        private void Attach(StatusListViewModel vm)
        {
            if (_hookedVm != null)
            {
                _hookedVm.PropertyChanged -= ViewModel_PropertyChanged;
            }

            _hookedVm = vm;
            DataContext = vm;

            if (_hookedVm != null)
            {
                _hookedVm.PropertyChanged += ViewModel_PropertyChanged;
                SetDockOpen(_hookedVm.IsNewStatusPickerOpen);
            }
            else
            {
                SetDockOpen(false);
            }
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(StatusListViewModel.IsNewStatusPickerOpen))
            {
                SetDockOpen(_hookedVm != null && _hookedVm.IsNewStatusPickerOpen);
            }
        }

        private void NewStatusScrim_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            ViewModel?.CloseNewStatusPicker();
        }

        private void SetDockOpen(bool open)
        {
            if (_dockOpen == open)
            {
                return;
            }

            _dockOpen = open;
            _storyboard?.Stop();

            if (open)
            {
                Visibility = Visibility.Visible;
                NewStatusScrim.Visibility = Visibility.Visible;
                NewStatusDock.Visibility = Visibility.Visible;
                ApplyTileSize(ActualWidth);
                NewStatusDock.UpdateLayout();
            }

            double distance = NewStatusDock.ActualHeight > 0 ? NewStatusDock.ActualHeight : 320;
            var easing = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn };
            var slide = new DoubleAnimation
            {
                From = open ? distance : 0,
                To = open ? 0 : distance,
                Duration = new Duration(DockDuration),
                EasingFunction = easing
            };
            Storyboard.SetTarget(slide, NewStatusDock);
            Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(TranslateTransform.Y)");

            var fade = new DoubleAnimation
            {
                To = open ? 1 : 0,
                Duration = new Duration(DockDuration)
            };
            Storyboard.SetTarget(fade, NewStatusScrim);
            Storyboard.SetTargetProperty(fade, "Opacity");

            var storyboard = new Storyboard();
            storyboard.Children.Add(slide);
            storyboard.Children.Add(fade);
            if (!open)
            {
                storyboard.Completed += DockHide_Completed;
            }

            _storyboard = storyboard;
            storyboard.Begin();
        }

        private void DockHide_Completed(object sender, object e)
        {
            if (_dockOpen)
            {
                return;
            }

            NewStatusScrim.Visibility = Visibility.Collapsed;
            NewStatusDock.Visibility = Visibility.Collapsed;
            Visibility = Visibility.Collapsed;
        }

        private void ApplyTileSize(double width)
        {
            if (NewStatusTilesGrid == null || width <= 0)
            {
                return;
            }

            double size = Math.Min(TileMaxSize, (width - 10 - (3 * 10)) / 3);
            if (size <= 0)
            {
                return;
            }

            foreach (UIElement child in NewStatusTilesGrid.Children)
            {
                if (child is AttachTile tile)
                {
                    tile.Width = size;
                }
            }
        }
    }
}
