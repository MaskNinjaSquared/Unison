using System.ComponentModel;
using System.Runtime.CompilerServices;
using Unison.Core.Models;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Face for <see cref="ChatThemePreview"/> templates — form factor + wallpaper knobs
    /// so <see cref="ChatBackgroundSurface"/> binds doodles/solid color live.
    /// </summary>
    public sealed class ChatThemePreviewState : INotifyPropertyChanged
    {
        private bool _isMobile;
        private ChatBackgroundKind _backgroundKind = ChatBackgroundKind.Default;
        private string _customPath;
        private string _solidColorHex;

        public event PropertyChangedEventHandler PropertyChanged;

        public bool IsMobile
        {
            get { return _isMobile; }
            set
            {
                if (_isMobile == value)
                {
                    return;
                }

                _isMobile = value;
                OnPropertyChanged();
            }
        }

        public ChatBackgroundKind BackgroundKind
        {
            get { return _backgroundKind; }
            set
            {
                if (_backgroundKind == value)
                {
                    return;
                }

                _backgroundKind = value;
                OnPropertyChanged();
            }
        }

        public string CustomPath
        {
            get { return _customPath; }
            set
            {
                if (string.Equals(_customPath, value, System.StringComparison.Ordinal))
                {
                    return;
                }

                _customPath = value;
                OnPropertyChanged();
            }
        }

        public string SolidColorHex
        {
            get { return _solidColorHex; }
            set
            {
                if (string.Equals(_solidColorHex, value, System.StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _solidColorHex = value;
                OnPropertyChanged();
            }
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
