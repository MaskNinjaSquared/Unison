using System;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Unison.Uwp.UI.Controls;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media.Animation;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Controls
{
    /// <summary>
    /// WhatsApp Legacy New Conversation overlay; hosts Legacy CreateConversationFlowView (CommandBar).
    /// System Back via <see cref="INavigator.PushLocalBack"/> (not a direct BackRequested hook).
    /// </summary>
    public sealed partial class NewConversationView : UserControl
    {
        public static readonly DependencyProperty ViewModelProperty =
            DependencyProperty.Register(
                nameof(ViewModel),
                typeof(NewConversationViewModel),
                typeof(NewConversationView),
                new PropertyMetadata(null, OnViewModelChanged));

        private NewConversationViewModel _hookedVm;
        private IDisposable _localBackRegistration;
        private bool _layoutHooked;
        private VisualStateGroup _layoutStates;
        private AlphabetSemanticZoomHost _contactsZoom;

        public NewConversationView()
        {
            InitializeComponent();
            Visibility = Visibility.Collapsed;
            Opacity = 1;
            Loaded += NewConversationView_Loaded;
            Unloaded += NewConversationView_Unloaded;
        }

        public NewConversationViewModel ViewModel
        {
            get => (NewConversationViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }

        private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as NewConversationView;
            control?.AttachViewModel(e.OldValue as NewConversationViewModel, e.NewValue as NewConversationViewModel);
        }

        private void AttachViewModel(NewConversationViewModel oldVm, NewConversationViewModel newVm)
        {
            if (_hookedVm != null)
            {
                _hookedVm.PropertyChanged -= ViewModel_PropertyChanged;
                _hookedVm = null;
            }

            DataContext = newVm;
            CreateFlowPart.ViewModel = newVm;
            _hookedVm = newVm;
            ApplyCreateFlowVisibility();
            if (_hookedVm == null)
            {
                BeginClose(animate: false);
                return;
            }

            _hookedVm.PropertyChanged += ViewModel_PropertyChanged;
            if (_hookedVm.IsOpen)
            {
                BeginOpen();
            }
            else
            {
                BeginClose(animate: false);
            }
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null || ViewModel == null)
            {
                return;
            }

            if (string.Equals(e.PropertyName, nameof(NewConversationViewModel.IsOpen), StringComparison.Ordinal))
            {
                if (ViewModel.IsOpen)
                {
                    BeginOpen();
                }
                else
                {
                    BeginClose(animate: true);
                }

                return;
            }

            if (string.Equals(e.PropertyName, nameof(NewConversationViewModel.IsCreateFlowOpen), StringComparison.Ordinal))
            {
                ApplyCreateFlowVisibility();
                return;
            }

            if (string.Equals(e.PropertyName, nameof(NewConversationViewModel.IsCatalogLoading), StringComparison.Ordinal))
            {
                TryPromoteOptionsIntoListHeader();
                return;
            }

            if (string.Equals(e.PropertyName, nameof(NewConversationViewModel.IsContactsSurfaceReady), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(NewConversationViewModel.GroupedContacts), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(NewConversationViewModel.JumpTiles), StringComparison.Ordinal))
            {
                BindGroupedSource();
            }
        }

        private void BeginOpen()
        {
            EnsureContactsZoom();
            _contactsZoom?.ResetZoomedIn();
            Opacity = 0;
            Visibility = Visibility.Visible;
            ApplyCreateFlowVisibility();
            HookBack();
            HookLayoutStates();
            BindGroupedSource();
            RunOpenFade();
        }

        private void BeginClose(bool animate)
        {
            UnhookBack();
            // Keep the x:Load A–Z host warm; only hide the overlay.
            Opacity = 1;
            Visibility = Visibility.Collapsed;
        }

        private void EnsureContactsZoom()
        {
            // ContactsZoom is x:Load'd — null until IsContactsSurfaceReady.
            if (_contactsZoom != null || ContactsZoom == null || ContactsList == null || JumpList == null)
            {
                return;
            }

            _contactsZoom = new AlphabetSemanticZoomHost(ContactsZoom, ContactsList, JumpList);
        }

        private void BindGroupedSource()
        {
            if (ViewModel == null || !ViewModel.IsContactsSurfaceReady)
            {
                return;
            }

            EnsureContactsZoom();
            _contactsZoom?.Bind(ViewModel.GroupedContacts, ViewModel.JumpTiles);
            TryPromoteOptionsIntoListHeader();
        }

        /// <summary>
        /// After the A–Z host is warm, move the action tiles into the list header so short
        /// viewports can scroll past the tiles and reach contacts (keeps search sticky).
        /// </summary>
        private void TryPromoteOptionsIntoListHeader()
        {
            if (ViewModel == null || ViewModel.IsCatalogLoading)
            {
                return;
            }

            if (ContactsList == null || OptionsChromeHost == null)
            {
                return;
            }

            if (ReferenceEquals(ContactsList.Header, OptionsChromeHost))
            {
                return;
            }

            var parent = OptionsChromeHost.Parent as Panel;
            if (parent != null)
            {
                parent.Children.Remove(OptionsChromeHost);
            }

            ContactsList.Header = OptionsChromeHost;
        }

        /// <summary>Catalog (search + tiles + contacts) and the create wizard never share the screen.</summary>
        private void ApplyCreateFlowVisibility()
        {
            bool isCreateFlowOpen = ViewModel != null && ViewModel.IsCreateFlowOpen;
            Visibility catalog = isCreateFlowOpen ? Visibility.Collapsed : Visibility.Visible;
            CatalogHeader.Visibility = catalog;
            SearchBox.Visibility = catalog;
            if (OptionsChromeHost != null)
            {
                OptionsChromeHost.Visibility = catalog;
            }

            if (ContactsAreaHost != null)
            {
                ContactsAreaHost.Visibility = catalog;
            }

            if (ContactsZoom != null)
            {
                ContactsZoom.Visibility = catalog;
            }

            CreateFlowPart.Visibility = isCreateFlowOpen ? Visibility.Visible : Visibility.Collapsed;
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

        private void HookBack()
        {
            if (_localBackRegistration != null)
            {
                return;
            }

            INavigator navigator = App.Services?.GetService<INavigator>();
            if (navigator == null)
            {
                return;
            }

            _localBackRegistration = navigator.PushLocalBack(TryHandleLocalBack);
        }

        private void UnhookBack()
        {
            _localBackRegistration?.Dispose();
            _localBackRegistration = null;
        }

        private void HookLayoutStates()
        {
            if (_layoutHooked)
            {
                return;
            }

            FrameworkElement host = LayoutRoot ?? (Content as FrameworkElement);
            if (host == null)
            {
                return;
            }

            foreach (VisualStateGroup group in VisualStateManager.GetVisualStateGroups(host))
            {
                if (group.Name != "LayoutStates")
                {
                    continue;
                }

                _layoutStates = group;
                _layoutStates.CurrentStateChanged += LayoutStates_CurrentStateChanged;
                _layoutHooked = true;
                ReportLayoutFromStates();
                return;
            }
        }

        private void UnhookLayoutStates()
        {
            if (!_layoutHooked || _layoutStates == null)
            {
                return;
            }

            _layoutStates.CurrentStateChanged -= LayoutStates_CurrentStateChanged;
            _layoutStates = null;
            _layoutHooked = false;
        }

        private void LayoutStates_CurrentStateChanged(object sender, VisualStateChangedEventArgs e)
        {
            ReportLayoutFromStates();
        }

        private void ReportLayoutFromStates()
        {
            string name = _layoutStates?.CurrentState?.Name;
            bool minimal = string.IsNullOrEmpty(name) ||
                           string.Equals(name, "Minimal", StringComparison.Ordinal);
            ViewModel?.ReportLayoutMinimal(minimal);
        }

        private bool TryHandleLocalBack()
        {
            if (ViewModel == null || !ViewModel.IsOpen)
            {
                return false;
            }

            if (CreateFlowPart != null && CreateFlowPart.TryHandleMembersZoomBack())
            {
                return true;
            }

            if (ViewModel.TryHandleCreateBack())
            {
                return true;
            }

            if (_contactsZoom != null && _contactsZoom.TryHandleBackToZoomedIn())
            {
                return true;
            }

            ViewModel.Close();
            return true;
        }

        private void NewConversationView_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureContactsZoom();
            HookLayoutStates();
        }

        private void NewConversationView_Unloaded(object sender, RoutedEventArgs e)
        {
            UnhookBack();
            UnhookLayoutStates();
            if (_hookedVm != null)
            {
                _hookedVm.PropertyChanged -= ViewModel_PropertyChanged;
                _hookedVm = null;
            }
        }

        private void OptionsTilesHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (OptionsTilesHost == null ||
                OptionsTilesHost.Visibility != Visibility.Visible ||
                e.NewSize.Width <= 0)
            {
                return;
            }

            const double tileMargin = 5;
            const double maxSide = 150;
            const int columns = 3;
            double side = Math.Floor((e.NewSize.Width / columns) - (tileMargin * 2));
            if (side < 1)
            {
                return;
            }

            if (side > maxSide)
            {
                side = maxSide;
            }

            ApplyMinimalTileSide(TileNewGroup, side);
            ApplyMinimalTileSide(TileNewContact, side);
            ApplyMinimalTileSide(TileNewCommunity, side);
            ApplyMinimalTileSide(TileNewChannel, side);
            ApplyMinimalTileSide(TilePhoneNumber, side);
            ApplyMinimalTileSide(SelfTile, side);
        }

        private static void ApplyMinimalTileSide(FrameworkElement tile, double side)
        {
            if (tile == null)
            {
                return;
            }

            tile.Width = side;
            tile.Height = side;
        }

        private void ContactsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var chat = e.ClickedItem as ChatItem;
            if (chat == null || ViewModel?.SelectContactCommand == null)
            {
                return;
            }

            if (ViewModel.SelectContactCommand.CanExecute(chat))
            {
                ViewModel.SelectContactCommand.Execute(chat);
            }
        }

        // Jump / SemanticZoom handlers kept for XAML event wiring; host owns the logic.
        private void JumpList_ItemClick(object sender, ItemClickEventArgs e)
        {
            // AlphabetSemanticZoomHost also listens; keep empty for XAML compatibility if wired.
        }

        private void ContactsZoom_ViewChangeStarted(object sender, SemanticZoomViewChangedEventArgs e)
        {
        }

        private void ContactsZoom_ViewChangeCompleted(object sender, SemanticZoomViewChangedEventArgs e)
        {
        }
    }
}
