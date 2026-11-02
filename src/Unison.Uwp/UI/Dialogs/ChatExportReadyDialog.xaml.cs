using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Dialogs
{
    public sealed partial class ChatExportReadyDialog : ContentDialog
    {
        public ChatExportReadyDialog()
        {
            InitializeComponent();
        }

        public void SetFile(string fileName, string infoText)
        {
            FileNameText.Text = fileName ?? string.Empty;
            InfoText.Text = infoText ?? string.Empty;
        }
    }
}
