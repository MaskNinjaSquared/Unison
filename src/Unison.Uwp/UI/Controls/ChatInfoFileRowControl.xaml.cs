using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Document row in chat info: file glyph + name, view (cache/open), save to disk.
    /// DataContext is <c>ChatMessageViewModel</c>.
    /// </summary>
    public sealed partial class ChatInfoFileRowControl : UserControl
    {
        public ChatInfoFileRowControl()
        {
            InitializeComponent();
        }
    }
}
