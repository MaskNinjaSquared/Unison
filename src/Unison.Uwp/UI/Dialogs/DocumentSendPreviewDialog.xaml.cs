using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Dialogs
{
    /// <summary>
    /// Document send confirmation: file row (icon + name) like chat-info Files, plus caption.
    /// </summary>
    public sealed partial class DocumentSendPreviewDialog : ContentDialog
    {
        public DocumentSendPreviewDialog()
        {
            InitializeComponent();
        }

        public string Caption => CaptionBox?.Text;

        public void SetPreview(string fileName, string infoText, string initialCaption = null)
        {
            FileNameText.Text = string.IsNullOrWhiteSpace(fileName) ? "Document" : fileName.Trim();
            InfoText.Text = infoText ?? string.Empty;
            CaptionBox.Text = initialCaption ?? string.Empty;
        }
    }
}
