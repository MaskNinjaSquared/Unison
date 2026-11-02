using Unison.Core.ViewModels;

namespace Unison.Core.Factories
{
    /// <summary>
    /// Creates a fresh <see cref="GroupPreviewDialogViewModel"/> per dialog open.
    /// </summary>
    public interface IGroupPreviewDialogViewModelFactory
    {
        GroupPreviewDialogViewModel Create(GroupPreviewDialogArgs args);
    }
}
