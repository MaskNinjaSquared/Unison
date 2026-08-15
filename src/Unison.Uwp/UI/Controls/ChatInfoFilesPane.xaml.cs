using System.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    public sealed partial class ChatInfoFilesPane : UserControl
    {
        public ChatInfoFilesPane()
        {
            InitializeComponent();
        }

        public void Bind(IEnumerable items, bool hasItems, string emptyText)
        {
            if (FilesEmptyText != null)
            {
                FilesEmptyText.Text = emptyText ?? string.Empty;
            }

            if (FilesEmptyHost != null)
            {
                FilesEmptyHost.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
            }

            if (FilesList != null)
            {
                FilesList.ItemsSource = items;
                FilesList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public FrameworkElement Host => FilesHost;
    }
}
