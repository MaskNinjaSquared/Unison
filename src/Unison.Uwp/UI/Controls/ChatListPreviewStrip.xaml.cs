using Unison.Core.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Chat-list subtitle: media chip (via <c>ChatPreviewKindTemplateSelector</c>) + preview text.
    /// Kind/Text are DPs bound with <c>x:Bind</c> in XAML (no imperative Apply*).
    /// </summary>
    public sealed partial class ChatListPreviewStrip : UserControl
    {
        public static readonly DependencyProperty KindProperty =
            DependencyProperty.Register(
                nameof(Kind),
                typeof(ChatPreviewKind),
                typeof(ChatListPreviewStrip),
                new PropertyMetadata(ChatPreviewKind.Text));

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(
                nameof(Text),
                typeof(string),
                typeof(ChatListPreviewStrip),
                new PropertyMetadata(string.Empty));

        public ChatListPreviewStrip()
        {
            InitializeComponent();
        }

        public ChatPreviewKind Kind
        {
            get { return (ChatPreviewKind)GetValue(KindProperty); }
            set { SetValue(KindProperty, value); }
        }

        public string Text
        {
            get { return (string)GetValue(TextProperty); }
            set { SetValue(TextProperty, value); }
        }
    }
}
