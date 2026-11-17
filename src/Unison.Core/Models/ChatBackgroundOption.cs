using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Unison.Core.Models
{
    /// <summary>One thumb in the chat wallpaper gallery (150×150 live-tile footprint).</summary>
    public sealed class ChatBackgroundOption : INotifyPropertyChanged
    {
        private bool _isSelected;

        public event PropertyChangedEventHandler PropertyChanged;

        public ChatBackgroundKind Kind { get; set; }

        public string Title { get; set; }

        /// <summary>Appx URI for the thumb (Default/preset); null for Custom until a path exists.</summary>
        public string ThumbUri { get; set; }

        /// <summary>WP8 color-picker style: selected tile draws a contrasting border.</summary>
        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                OnPropertyChanged();
            }
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
