// =============================================================================
// CallScreenPresenter
//
// Full-window popup carrying LegacyCallView while CallScreenViewModel is open.
// Sits above the frame, so every shell theme gets the same screen for now.
// =============================================================================
using System;
using System.ComponentModel;
using Unison.Core.Contracts;
using Unison.Core.ViewModels;
using Unison.Uwp.Shell.WhatsAppLegacy.Controls;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls.Primitives;

namespace Unison.Uwp.Services.Calls
{
    internal sealed class CallScreenPresenter : ICallScreenPresenter
    {
        private readonly CallScreenViewModel _viewModel;
        private readonly Action<string> _openChat;
        private Popup _popup;
        private LegacyCallView _view;
        private bool _started;

        public CallScreenPresenter(CallScreenViewModel viewModel, Action<string> openChat)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _openChat = openChat ?? throw new ArgumentNullException(nameof(openChat));
        }

        public void Start()
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.OpenChatRequested += OnOpenChatRequested;
            Sync();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, nameof(CallScreenViewModel.IsOpen), StringComparison.Ordinal))
            {
                Sync();
            }
        }

        private void OnOpenChatRequested(object sender, string jid)
        {
            Hide();
            _openChat(jid);
        }

        private void Sync()
        {
            if (_viewModel.IsOpen)
            {
                Show();
            }
            else
            {
                Hide();
            }
        }

        private void Show()
        {
            if (_popup == null)
            {
                _view = new LegacyCallView { DataContext = _viewModel };
                _popup = new Popup { Child = _view };
            }

            if (_popup.IsOpen)
            {
                return;
            }

            Fit();
            Window.Current.SizeChanged += OnWindowSizeChanged;
            _popup.IsOpen = true;
        }

        private void Hide()
        {
            if (_popup == null || !_popup.IsOpen)
            {
                return;
            }

            Window.Current.SizeChanged -= OnWindowSizeChanged;
            _popup.IsOpen = false;
        }

        private void OnWindowSizeChanged(object sender, WindowSizeChangedEventArgs e) => Fit();

        private void Fit()
        {
            Windows.Foundation.Rect bounds = Window.Current.Bounds;
            _view.Width = bounds.Width;
            _view.Height = bounds.Height;
        }
    }
}
