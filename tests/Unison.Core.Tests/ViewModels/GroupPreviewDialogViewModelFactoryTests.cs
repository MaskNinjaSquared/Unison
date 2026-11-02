using Unison.Core.Factories;
using Unison.Core.ViewModels;
using Xunit;

namespace Unison.Core.Tests.ViewModels
{
    public sealed class GroupPreviewDialogViewModelFactoryTests
    {
        [Fact]
        public void Create_maps_args_onto_view_model()
        {
            var args = new GroupPreviewDialogArgs
            {
                Jid = "120363@g.us",
                Title = "Geral",
                AvatarUrl = "ms-appdata:///local/a.jpg",
                CreatorLabel = "Alice",
                CreationUnixSeconds = 1_700_000_000,
                MemberCount = 12,
                SubtitleTemplate = "Group created by {0} on {1}",
                MemberCountTemplate = "{0} members",
                PrimaryButtonText = "Join",
                CancelButtonText = "Cancel"
            };

            GroupPreviewDialogViewModel vm =
                new GroupPreviewDialogViewModelFactory().Create(args);

            Assert.Equal(args.Jid, vm.Jid);
            Assert.Equal("Geral", vm.Title);
            Assert.Equal(args.AvatarUrl, vm.AvatarUrl);
            Assert.Contains("Alice", vm.Subtitle);
            Assert.Equal("12 members", vm.MemberCountText);
            Assert.True(vm.HasMemberCount);
            Assert.Equal("Join", vm.PrimaryButtonText);
            Assert.Equal("Cancel", vm.CancelButtonText);
        }
    }
}
