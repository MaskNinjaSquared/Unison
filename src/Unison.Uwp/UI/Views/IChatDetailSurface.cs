using System;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;

namespace Unison.Uwp.UI.Views
{
    /// <summary>
    /// Shared surface API for conversation panes:
    /// <c>Shell.WhatsApp.Views.ChatDetailView</c> (MenuFlyout attach),
    /// <c>Shell.Unison.Views.ChatDetailView</c> (tile tray + flyout),
    /// and Legacy. Visual trees stay separate; hosts/templates resolve via this contract.
    /// </summary>
    internal interface IChatDetailSurface
    {
        event EventHandler BackRequested;

        ChatDetailViewModel ViewModel { get; }

        bool HasActiveChat { get; }

        /// <summary>
        /// Hosts set this before <see cref="CompleteActiveChatLoadAsync"/> so Mobile skips
        /// the 2s materialize delay (list tap, archived, toast / deep-link — same as Legacy).
        /// Cleared when that load finishes.
        /// </summary>
        bool PreferFastTimelineLoad { get; set; }

        ChatItem ActiveChatItem { get; }

        Task SetActiveChatAsync(ChatItem chat);

        Task PrepareActiveChatAsync(ChatItem chat);

        Task CompleteActiveChatLoadAsync();

        bool TryConsumeBack();

        void OnMessageBubbleRightTapped(object sender, RightTappedRoutedEventArgs e);

        void OnMessageBubbleHolding(object sender, HoldingRoutedEventArgs e);

        void OnQuotedMessageTapped(object sender, TappedRoutedEventArgs e);

        void OnQuotedAuthorTapped(object sender, TappedRoutedEventArgs e);

        void OnGroupParticipantTapped(object sender, TappedRoutedEventArgs e);

        void OnAudioPlayButtonClick(object sender, RoutedEventArgs e);

        void CycleAudioPlaybackSpeed(ChatMessageViewModel vm);

        void PlayAudioAtSpeed(ChatMessageViewModel vm, AudioPlaybackSpeed speed);

        void OnImageOpenButtonClick(object sender, RoutedEventArgs e);

        void OnVideoOpenButtonClick(object sender, RoutedEventArgs e);

        void OnDocumentReadyContextRequested(object sender, RightTappedRoutedEventArgs e);

        void OnDocumentReadyHolding(object sender, HoldingRoutedEventArgs e);

        /// <summary>Overflow "…" on a ready document bubble — Abrir / Salvar como (flyout on demand).</summary>
        void OnDocumentOverflowClick(object sender, RoutedEventArgs e);

        void SeekAudioPlayback(ChatMessageViewModel vm, double seconds);

        void OpenInfoImage(ChatMessageViewModel vm);

        void OpenInfoVideo(ChatMessageViewModel vm);

        void PlayOrPauseAudioFromInfo(ChatMessageViewModel vm);
    }
}
