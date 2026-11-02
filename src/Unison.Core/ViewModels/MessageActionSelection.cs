using System;
using System.ComponentModel;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// The one bubble the Legacy command bar acts on. Tapping it again closes the bar.
    /// </summary>
    public sealed class MessageActionSelection : INotifyPropertyChanged
    {
        public const uint PinFor24HoursSeconds = 86400;
        public const uint PinFor7DaysSeconds = 604800;
        public const uint PinFor30DaysSeconds = 2592000;

        public event PropertyChangedEventHandler PropertyChanged;

        private ChatMessageViewModel _message;
        private bool _isGroupChat;
        private bool _viewerIsGroupAdmin;
        private Func<DateTime> _utcNow;

        public MessageActionSelection()
            : this(() => DateTime.UtcNow)
        {
        }

        /// <summary>Test seam: inject the clock used for delete-for-everyone.</summary>
        public MessageActionSelection(Func<DateTime> utcNow)
        {
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public ChatMessageViewModel Message
        {
            get { return _message; }
        }

        public bool IsOpen
        {
            get { return _message != null; }
        }

        public bool CanCopy
        {
            get
            {
                return _message != null &&
                       _message.CopyCommand != null &&
                       _message.CopyCommand.CanExecute(null);
            }
        }

        public bool CanReply
        {
            get { return _message != null; }
        }

        public bool CanForward
        {
            get
            {
                return _message != null &&
                       _message.ForwardCommand != null &&
                       _message.ForwardCommand.CanExecute(null);
            }
        }

        public bool CanPin
        {
            get { return _message != null && !_message.IsPinned; }
        }

        public bool CanUnpin
        {
            get { return _message != null && _message.IsPinned; }
        }

        /// <summary>
        /// Delete-for-me: any non-revoked bubble (FAQ — sent or received).
        /// </summary>
        public bool CanDeleteForMe
        {
            get
            {
                return _message != null &&
                       MessageDeleteEligibility.CanDeleteForMe(_message.Model);
            }
        }

        /// <summary>
        /// Delete-for-everyone: within the two-day window; own message, or admin moderating.
        /// </summary>
        public bool CanDeleteForEveryone
        {
            get
            {
                return _message != null &&
                       MessageDeleteEligibility.CanDeleteForEveryone(
                           _message.Model,
                           _isGroupChat,
                           _viewerIsGroupAdmin,
                           _utcNow());
            }
        }

        /// <summary>
        /// Points the bar at <paramref name="message"/>. The same bubble a second time clears it.
        /// </summary>
        public void Select(
            ChatMessageViewModel message,
            bool isGroupChat = false,
            bool viewerIsGroupAdmin = false)
        {
            if (message == null || message.Model == null || string.IsNullOrWhiteSpace(message.Model.Id))
            {
                Dismiss();
                return;
            }

            if (ReferenceEquals(_message, message) &&
                _isGroupChat == isGroupChat &&
                _viewerIsGroupAdmin == viewerIsGroupAdmin)
            {
                Dismiss();
                return;
            }

            Detach();
            _message = message;
            _isGroupChat = isGroupChat;
            _viewerIsGroupAdmin = viewerIsGroupAdmin;
            _message.PropertyChanged += OnMessagePropertyChanged;
            RaiseAll();
        }

        public void Dismiss()
        {
            if (_message == null)
            {
                return;
            }

            Detach();
            _message = null;
            _isGroupChat = false;
            _viewerIsGroupAdmin = false;
            RaiseAll();
        }

        /// <summary>
        /// Refreshes group/admin flags for the open selection without toggling dismiss.
        /// </summary>
        public void UpdateChatContext(bool isGroupChat, bool viewerIsGroupAdmin)
        {
            if (_message == null)
            {
                return;
            }

            if (_isGroupChat == isGroupChat && _viewerIsGroupAdmin == viewerIsGroupAdmin)
            {
                return;
            }

            _isGroupChat = isGroupChat;
            _viewerIsGroupAdmin = viewerIsGroupAdmin;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanDeleteForEveryone)));
        }

        private void OnMessagePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            string name = e != null ? e.PropertyName : null;
            if (name == nameof(ChatMessage.IsPinned) ||
                name == nameof(ChatMessageViewModel.IsPinned) ||
                name == nameof(ChatMessage.Content) ||
                name == nameof(ChatMessage.Caption) ||
                name == nameof(ChatMessage.Kind) ||
                name == nameof(ChatMessage.IsFromMe) ||
                name == nameof(ChatMessage.IsRevoked) ||
                name == nameof(ChatMessageViewModel.IsRevoked) ||
                name == nameof(ChatMessage.Timestamp) ||
                string.IsNullOrEmpty(name))
            {
                RaisePinAndCopy();
            }
        }

        private void Detach()
        {
            if (_message != null)
            {
                _message.PropertyChanged -= OnMessagePropertyChanged;
            }
        }

        private void RaiseAll()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Message)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOpen)));
            RaisePinAndCopy();
        }

        private void RaisePinAndCopy()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCopy)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanReply)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanForward)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanPin)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUnpin)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanDeleteForMe)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanDeleteForEveryone)));
        }
    }
}
