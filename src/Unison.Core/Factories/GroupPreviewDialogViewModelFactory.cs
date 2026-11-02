using System;
using Unison.Core.ViewModels;

namespace Unison.Core.Factories
{
    public sealed class GroupPreviewDialogViewModelFactory : IGroupPreviewDialogViewModelFactory
    {
        public GroupPreviewDialogViewModel Create(GroupPreviewDialogArgs args)
        {
            if (args == null)
            {
                throw new ArgumentNullException(nameof(args));
            }

            return new GroupPreviewDialogViewModel(args);
        }
    }
}
