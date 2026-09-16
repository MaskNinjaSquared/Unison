using System;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;

namespace Unison.Uwp.UI.Views
{
    /// <summary>
    /// Shared surface API for WhatsApp (<see cref="ChatDetailView"/>) and
    /// Unison (<c>Shell.Unison.Views.ChatDetailView</c>) conversation panes.
    /// Visual trees stay separate (W10M); hosts/templates resolve via this contract.
    /// </summary>
    internal interface IChatDetailSurface
    {
        event EventHandler BackRequested;

        ChatDetailViewModel ViewModel { get; }

        bool HasActiveChat { get; }

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

        void OnImageOpenButtonClick(object sender, RoutedEventArgs e);

        void OnVideoOpenButtonClick(object sender, RoutedEventArgs e);

        void OnDocumentReadyContextRequested(object sender, RightTappedRoutedEventArgs e);

        void OnDocumentReadyHolding(object sender, HoldingRoutedEventArgs e);

        void SeekAudioPlayback(ChatMessageViewModel vm, double seconds);

        void OpenInfoImage(ChatMessageViewModel vm);

        void OpenInfoVideo(ChatMessageViewModel vm);

        void PlayOrPauseAudioFromInfo(ChatMessageViewModel vm);
    }
}
