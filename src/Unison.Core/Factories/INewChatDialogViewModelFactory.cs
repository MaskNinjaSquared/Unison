using Unison.Core.ViewModels;

namespace Unison.Core.Factories
{
    /// <summary>
    /// Creates a fresh <see cref="NewChatDialogViewModel"/> for each New Chat dialog.
    /// Transient state (phone / error) must not be reused across dialog openings.
    /// </summary>
    public interface INewChatDialogViewModelFactory
    {
        /// <summary>Build a clean New Chat form ViewModel.</summary>
        NewChatDialogViewModel Create();
    }
}
