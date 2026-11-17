using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Unison.Core.Models
{
    /// <summary>One WP8-style solid swatch in the conversation-theme color overlay.</summary>
    public sealed class ChatBackgroundSolidColorOption : INotifyPropertyChanged
    {
        private bool _isSelected;

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>#AARRGGBB; null = Transparent (theme light/dark doodle underfill).</summary>
        public string Hex { get; set; }

        public string Title { get; set; }

        /// <summary>True for the Transparent chip — theme wallpaper brush, saves null.</summary>
        public bool IsThemeDefault { get; set; }

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
