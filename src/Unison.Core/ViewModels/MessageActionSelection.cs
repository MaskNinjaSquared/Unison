using System.ComponentModel;
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

        public bool CanPin
        {
            get { return _message != null && !_message.IsPinned; }
        }

        public bool CanUnpin
        {
            get { return _message != null && _message.IsPinned; }
        }

        /// <summary>
        /// Points the bar at <paramref name="message"/>. The same bubble a second time clears it.
        /// </summary>
        public void Select(ChatMessageViewModel message)
        {
            if (message == null || message.Model == null || string.IsNullOrWhiteSpace(message.Model.Id))
            {
                Dismiss();
                return;
            }

            if (ReferenceEquals(_message, message))
            {
                Dismiss();
                return;
            }

            Detach();
            _message = message;
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
            RaiseAll();
        }

        private void OnMessagePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            string name = e != null ? e.PropertyName : null;
            if (name == nameof(ChatMessage.IsPinned) ||
                name == nameof(ChatMessageViewModel.IsPinned) ||
                name == nameof(ChatMessage.Content) ||
                name == nameof(ChatMessage.Caption) ||
                name == nameof(ChatMessage.Kind) ||
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanPin)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUnpin)));
        }
    }
}
