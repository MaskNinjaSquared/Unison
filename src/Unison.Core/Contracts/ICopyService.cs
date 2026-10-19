namespace Unison.Core.Contracts
{
    /// <summary>
    /// Puts plain text on the system clipboard. The platform owns the clipboard.
    /// </summary>
    public interface ICopyService
    {
        void CopyText(string text);
    }
}
