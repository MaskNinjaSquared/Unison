using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Round profile-photo button. Camera / Gallery / Delete raise events for the host.
    /// </summary>
    public sealed partial class ProfilePhotoButton : UserControl
    {
        public ProfilePhotoButton()
        {
            InitializeComponent();
        }

        public event EventHandler CameraRequested;

        public event EventHandler GalleryRequested;

        public event EventHandler DeleteRequested;

        private void CameraItem_Click(object sender, RoutedEventArgs e) =>
            CameraRequested?.Invoke(this, EventArgs.Empty);

        private void GalleryItem_Click(object sender, RoutedEventArgs e) =>
            GalleryRequested?.Invoke(this, EventArgs.Empty);

        private void DeleteItem_Click(object sender, RoutedEventArgs e) =>
            DeleteRequested?.Invoke(this, EventArgs.Empty);
    }
}
