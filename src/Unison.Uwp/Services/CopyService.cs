using Unison.Core.Contracts;
using Windows.ApplicationModel.DataTransfer;

namespace Unison.Uwp.Services
{
    public sealed class CopyService : ICopyService
    {
        public void CopyText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            try
            {
                Clipboard.Flush();
            }
            catch
            {
                // Flush keeps the text after the app suspends. SetContent already
                // published it for the current session when Flush is refused.
            }
        }
    }
}
