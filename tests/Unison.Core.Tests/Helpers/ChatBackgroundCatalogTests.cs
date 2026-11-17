using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ChatBackgroundCatalogTests
    {
        [Fact]
        public void Default_resolves_to_stock_colored_asset()
        {
            Assert.Equal(
                ChatBackgroundCatalog.DefaultAssetUri,
                ChatBackgroundCatalog.ResolveImageUri(ChatBackgroundKind.Default, null));
            Assert.Null(ChatBackgroundCatalog.TryGetPresetUri(ChatBackgroundKind.Default));
        }

        [Fact]
        public void Presets_have_appx_uris()
        {
            Assert.Equal(ChatBackgroundCatalog.CubesAssetUri, ChatBackgroundCatalog.TryGetPresetUri(ChatBackgroundKind.Cubes));
            Assert.Equal(ChatBackgroundCatalog.OwlsAssetUri, ChatBackgroundCatalog.TryGetPresetUri(ChatBackgroundKind.Owls));
            Assert.Equal(ChatBackgroundCatalog.DamaskAssetUri, ChatBackgroundCatalog.TryGetPresetUri(ChatBackgroundKind.Damask));
        }

        [Fact]
        public void Custom_uses_host_display_uri_or_falls_back_to_default()
        {
            Assert.Equal(
                "ms-appdata:///local/ChatBackgrounds/x/wall.jpg",
                ChatBackgroundCatalog.ResolveImageUri(
                    ChatBackgroundKind.Custom,
                    "ms-appdata:///local/ChatBackgrounds/x/wall.jpg"));
            Assert.Equal(
                ChatBackgroundCatalog.DefaultAssetUri,
                ChatBackgroundCatalog.ResolveImageUri(ChatBackgroundKind.Custom, null));
        }

        [Fact]
        public void ChatItem_BackgroundImageUri_maps_custom_relative_path()
        {
            var chat = new ChatItem
            {
                BackgroundKind = ChatBackgroundKind.Custom,
                BackgroundPath = "ChatBackgrounds\\abc\\wall.jpg"
            };

            Assert.Equal("ms-appdata:///local/ChatBackgrounds/abc/wall.jpg", chat.BackgroundImageUri);
        }
    }
}
