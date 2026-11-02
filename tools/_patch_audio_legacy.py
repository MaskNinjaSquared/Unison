from pathlib import Path

path = Path(r"c:\unison-github-new\Unison\src\Unison.Uwp\Shell\WhatsAppLegacy\Views\ChatDetailView.xaml.cs")
text = path.read_text(encoding="utf-8")
start = text.index("        /// <summary>Play / resume / pause for a ready local audio bubble + SMTC.</summary>")
end = text.index("        /// <summary>Trash: fade timer")
replacement = r'''        /// <summary>Play / resume / pause — ViewModel + IChatAudioPlaybackService.</summary>
        public async void OnAudioPlayButtonClick(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var vm = element?.DataContext as ChatMessageViewModel;
            if (ViewModel != null)
            {
                await ViewModel.PlayOrPauseAudioAsync(vm);
            }
        }

        public void CycleAudioPlaybackSpeed(ChatMessageViewModel vm)
        {
            ViewModel?.CycleAudioPlaybackSpeed(vm);
        }

        public void PlayAudioAtSpeed(ChatMessageViewModel vm, Unison.Core.Models.AudioPlaybackSpeed speed)
        {
            if (ViewModel != null)
            {
                _ = ViewModel.PlayAudioAtSpeedAsync(vm, speed);
            }
        }

        public async void PlayOrPauseAudioFromInfo(ChatMessageViewModel vm)
        {
            if (vm == null || ViewModel == null)
            {
                return;
            }

            try
            {
                if (!vm.HasLocalAudio || vm.ShowAudioDownloadIcon || vm.NeedsAudioDownload)
                {
                    await vm.DownloadAudioAsync();
                    return;
                }

                await ViewModel.PlayOrPauseAudioAsync(vm);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(string.Format("[ChatDetailView] Playing audio from the info pane failed: {0}", ex.Message));
            }
        }

        /// <summary>Seek the active bubble via the playback service.</summary>
        public void SeekAudioPlayback(ChatMessageViewModel vm, double seconds)
        {
            ViewModel?.SeekAudioPlayback(vm, seconds);
        }

'''
path.write_text(text[:start] + replacement + text[end:], encoding="utf-8")
print("ok", path)
