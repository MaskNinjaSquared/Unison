using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>Channel create intro / Extended explain body (title + three Fluent rows).</summary>
    public sealed partial class CreateChannelInfoContent : UserControl
    {
        public CreateChannelInfoContent()
        {
            InitializeComponent();
        }

        // Prototype: guidelines link is visual only until the help surface is wired.
        private void GuidelinesHyperlink_Click(Hyperlink sender, HyperlinkClickEventArgs args)
        {
        }
    }
}
