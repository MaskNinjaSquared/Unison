// =============================================================================
// ChatDetailViewModel.DeferredSurfaces
//
// x:Load gates for heavy ChatDetail overlays (viewers, attach, reaction dock).
// Views set these open/closed; Unsupported may flip false on close to unload.
// =============================================================================

namespace Unison.Core.ViewModels
{
    public partial class ChatDetailViewModel
    {
        private bool _isImageViewerOpen;
        private bool _isVideoViewerOpen;
        private bool _isAttachMenuOpen;
        private bool _isReactionDockOpen;

        /// <summary>x:Load gate for ImageViewerOverlay.</summary>
        public bool IsImageViewerOpen
        {
            get => _isImageViewerOpen;
            set => Set(ref _isImageViewerOpen, value);
        }

        /// <summary>x:Load gate for VideoViewerOverlay.</summary>
        public bool IsVideoViewerOpen
        {
            get => _isVideoViewerOpen;
            set => Set(ref _isVideoViewerOpen, value);
        }

        /// <summary>x:Load gate for AttachMenuBar / AttachMenuScrim.</summary>
        public bool IsAttachMenuOpen
        {
            get => _isAttachMenuOpen;
            set => Set(ref _isAttachMenuOpen, value);
        }

        /// <summary>x:Load gate for ReactionEmojiDock.</summary>
        public bool IsReactionDockOpen
        {
            get => _isReactionDockOpen;
            set => Set(ref _isReactionDockOpen, value);
        }
    }
}
