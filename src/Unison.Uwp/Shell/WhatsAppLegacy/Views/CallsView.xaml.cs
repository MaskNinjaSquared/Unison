using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.ViewModels;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Views
{
    /// <summary>
    /// Legacy Calls pivot: shared list + detail, bottom CommandBar owns New Call / menu.
    /// Unison/WhatsApp use <see cref="UI.Views.CallsView"/> (header chrome) instead.
    /// </summary>
    public sealed partial class CallsView : Page
    {
        private ShellViewModel _shell;
        private bool _hooked;
        private bool _surfaceActive;
        private bool _splitterDragging;
        private bool _splitterHover;
        private double _dragStartX;
        private double _dragStartListWidth;
        private CoreCursor _previousCursor;

        public event EventHandler SearchRequested;

        public event EventHandler ProfileRequested;

        public event EventHandler DebugRequested;

        public event EventHandler SettingsRequested;

        public CallsViewModel ViewModel { get; private set; }

        /// <summary>True while Minimal/narrow shows call details full-screen.</summary>
        public bool HasOpenCallDetail => _shell != null && _shell.HasOpenCallDetail;

        public CallsView()
        {
            if (App.Services != null)
            {
                ViewModel = App.Services.GetService<CallsViewModel>();
            }

            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;
            DataContext = ViewModel;
            PaneSplitter.Width = ChatPaneLayoutConstants.SplitterWidth;
            Column0.MinWidth = ChatPaneLayoutConstants.MinListWidth;
            Column0.MaxWidth = ChatPaneLayoutConstants.MaxListWidth;
            Column1.MinWidth = ChatPaneLayoutConstants.MinDetailWidth;
            Loaded += CallsView_Loaded;

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
                DetailsPane.BackRequested += DetailsPane_BackRequested;
                DetailsPane.OpenChatRequested += DetailsPane_OpenChatRequested;
                ViewModel.OpenChatRequested += ViewModel_OpenChatRequested;
                CallsList.Bind(ViewModel);
                UpdateDetails();
            }
        }

        /// <summary>Legacy pivot: bind ChatPane + enter Calls surface.</summary>
        public Task InitAsync(ShellViewModel shell)
        {
            _shell = shell ?? App.Services?.GetService<ShellViewModel>();
            if (_shell != null && !_hooked)
            {
                _shell.PropertyChanged += Shell_PropertyChanged;
                _hooked = true;
            }

            if (!_surfaceActive)
            {
                _shell?.EnterCallsSurface();
                _surfaceActive = true;
            }

            ApplyChatPaneState();
            return Task.CompletedTask;
        }

        /// <summary>Leaving Calls: close detail and restore chat pane rules.</summary>
        public Task LeaveAsync()
        {
            if (ViewModel != null && ViewModel.IsCallDetailsOpen)
            {
                ViewModel.CloseDetailsCommand?.Execute(null);
            }

            if (_shell != null && _hooked)
            {
                _shell.PropertyChanged -= Shell_PropertyChanged;
                _hooked = false;
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

        private void CallsView_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyChatPaneState();
        }

        private void CallsAppBar_NewCallRequested(object sender, EventArgs e)
        {
            _ = ShowNewCallUnavailableAsync();
        }

        private void Shell_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.ChatPane) ||
                e.PropertyName == nameof(ShellViewModel.IsNarrowWindow))
            {
                ApplyChatPaneState();
            }
            else if (e.PropertyName == nameof(ShellViewModel.ChatListPaneWidth) && IsWideBoth())
            {
                ApplyListWidth(_shell.ChatListPaneWidth);
                UpdateSplitterPosition();
            }
        }

        private void ApplyChatPaneState()
        {
            if (_shell == null)
            {
                return;
            }

            string pane = _shell.ChatPane;
            VisualStateManager.GoToState(this, pane, false);

            bool wideBoth = string.Equals(pane, ShellViewModel.PaneWideBoth, StringComparison.Ordinal);
            if (wideBoth)
            {
                ApplyListWidth(_shell.ChatListPaneWidth);
                UpdateSplitterPosition();
                UpdateSplitterChrome();
            }
            else
            {
                _splitterDragging = false;
                _splitterHover = false;
                SplitterChrome.Opacity = 0;
                RestoreCursor();
            }
        }

        private void RootContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!IsWideBoth())
            {
                return;
            }

            double current = Column0.ActualWidth > 0
                ? Column0.ActualWidth
                : (_shell != null ? _shell.ChatListPaneWidth : ChatPaneLayoutConstants.DefaultListWidth);
            ApplyListWidth(current);
            UpdateSplitterPosition();
        }

        private bool IsWideBoth()
        {
            return _shell != null &&
                   !_shell.IsNarrowWindow &&
                   string.Equals(_shell.ChatPane, ShellViewModel.PaneWideBoth, StringComparison.Ordinal) &&
                   PaneSplitter.Visibility == Visibility.Visible;
        }

        private void ApplyListWidth(double desired)
        {
            double max = GetMaxListWidth();
            double width = Math.Max(
                ChatPaneLayoutConstants.MinListWidth,
                Math.Min(max, desired));
            Column0.Width = new GridLength(width);
            Column0.MinWidth = ChatPaneLayoutConstants.MinListWidth;
            Column0.MaxWidth = ChatPaneLayoutConstants.MaxListWidth;
        }

        private double GetMaxListWidth()
        {
            double total = RootContentGrid.ActualWidth;
            if (total <= 0)
            {
                return ChatPaneLayoutConstants.MaxListWidth;
            }

            double maxFromDetail = Math.Max(
                ChatPaneLayoutConstants.MinListWidth,
                total - ChatPaneLayoutConstants.MinDetailWidth);
            return Math.Min(ChatPaneLayoutConstants.MaxListWidth, maxFromDetail);
        }

        private void UpdateSplitterPosition()
        {
            if (PaneSplitter == null || !IsWideBoth())
            {
                return;
            }

            double listWidth = Column0.ActualWidth;
            if (listWidth <= 0 && Column0.Width.IsAbsolute)
            {
                listWidth = Column0.Width.Value;
            }

            double left = Math.Max(0, listWidth - ChatPaneLayoutConstants.SplitterOverlapList);
            PaneSplitter.Margin = new Thickness(left, 0, 0, 0);
        }

        private void UpdateSplitterChrome()
        {
            if (SplitterChrome == null)
            {
                return;
            }

            SplitterChrome.Opacity = (_splitterHover || _splitterDragging) ? 1 : 0;
        }

        private void PaneSplitter_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (!IsWideBoth())
            {
                return;
            }

            _splitterHover = true;
            UpdateSplitterChrome();
            SetResizeCursor();
        }

        private void PaneSplitter_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (_splitterDragging)
            {
                return;
            }

            _splitterHover = false;
            UpdateSplitterChrome();
            RestoreCursor();
        }

        private void PaneSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!IsWideBoth())
            {
                return;
            }

            _splitterDragging = true;
            _splitterHover = true;
            _dragStartX = e.GetCurrentPoint(RootContentGrid).Position.X;
            _dragStartListWidth = Column0.ActualWidth > 0
                ? Column0.ActualWidth
                : (Column0.Width.IsAbsolute
                    ? Column0.Width.Value
                    : ChatPaneLayoutConstants.DefaultListWidth);

            PaneSplitter.CapturePointer(e.Pointer);
            UpdateSplitterChrome();
            SetResizeCursor();
            e.Handled = true;
        }

        private void PaneSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_splitterDragging)
            {
                return;
            }

            double x = e.GetCurrentPoint(RootContentGrid).Position.X;
            ApplyListWidth(_dragStartListWidth + (x - _dragStartX));
            UpdateSplitterPosition();
            e.Handled = true;
        }

        private void PaneSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_splitterDragging)
            {
                return;
            }

            EndSplitterDrag(e.Pointer);
            e.Handled = true;
        }

        private void PaneSplitter_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (_splitterDragging)
            {
                EndSplitterDrag(null);
            }
        }

        private void EndSplitterDrag(Pointer pointer)
        {
            _splitterDragging = false;
            if (pointer != null)
            {
                try
                {
                    PaneSplitter.ReleasePointerCapture(pointer);
                }
                catch
                {
                }
            }

            double width = Column0.ActualWidth > 0
                ? Column0.ActualWidth
                : (Column0.Width.IsAbsolute
                    ? Column0.Width.Value
                    : ChatPaneLayoutConstants.DefaultListWidth);
            width = Math.Max(
                ChatPaneLayoutConstants.MinListWidth,
                Math.Min(GetMaxListWidth(), width));

            if (_shell != null)
            {
                _shell.ChatListPaneWidth = width;
            }

            ApplyListWidth(width);
            UpdateSplitterPosition();
            if (!_splitterHover)
            {
                RestoreCursor();
            }
        }

        private void SetResizeCursor()
        {
            try
            {
                var window = Window.Current;
                if (window?.CoreWindow == null)
                {
                    return;
                }

                if (_previousCursor == null)
                {
                    _previousCursor = window.CoreWindow.PointerCursor;
                }

                window.CoreWindow.PointerCursor = new CoreCursor(CoreCursorType.SizeWestEast, 1);
            }
            catch
            {
            }
        }

        private void RestoreCursor()
        {
            try
            {
                var window = Window.Current;
                if (window?.CoreWindow == null)
                {
                    return;
                }

                window.CoreWindow.PointerCursor = _previousCursor ?? new CoreCursor(CoreCursorType.Arrow, 1);
                _previousCursor = null;
            }
            catch
            {
            }
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (string.Equals(e.PropertyName, nameof(CallsViewModel.IsCallDetailsOpen), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(CallsViewModel.SelectedSubject), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(CallsViewModel.SelectedHistory), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(CallsViewModel.GroupedHistory), StringComparison.Ordinal))
            {
                UpdateDetails();
            }
        }

        private void UpdateDetails()
        {
            if (ViewModel == null)
            {
                return;
            }

            bool open = ViewModel.IsCallDetailsOpen;
            DetailsPane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            RightEmpty.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            if (open)
            {
                DetailsPane.BindSubject(
                    ViewModel.SelectedSubject,
                    ViewModel.SelectedHistory,
                    ViewModel.GroupedHistory);
            }

            _shell?.SelectCallDetail(open);
            ApplyChatPaneState();
        }

        private void DetailsPane_BackRequested(object sender, EventArgs e)
        {
            CloseDetail();
        }

        private void DetailsPane_OpenChatRequested(object sender, EventArgs e)
        {
            if (ViewModel?.OpenChatCommand != null && ViewModel.OpenChatCommand.CanExecute(null))
            {
                ViewModel.OpenChatCommand.Execute(null);
            }
        }

        private void ViewModel_OpenChatRequested(object sender, string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            CloseDetail();
            ShellViewModel shell = _shell ?? App.Services?.GetService<ShellViewModel>();
            if (shell == null)
            {
                return;
            }

            shell.QueueOpenChatFromActivation(
                "chat=" + Uri.EscapeDataString(jid),
                delayChatListUntilConversationReady: false);
        }

        private void CloseDetail()
        {
            ViewModel?.CloseDetailsCommand?.Execute(null);
            _shell?.SelectCallDetail(false);
            ApplyChatPaneState();
        }

        public bool TryHandleBack()
        {
            if (HasOpenCallDetail ||
                (ViewModel != null && ViewModel.IsCallDetailsOpen) ||
                (_shell != null &&
                 _shell.IsNarrowWindow &&
                 string.Equals(_shell.ChatPane, ShellViewModel.PaneNarrowDetail, StringComparison.Ordinal)))
            {
                CloseDetail();
                return true;
            }

            return false;
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
