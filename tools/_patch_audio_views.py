from pathlib import Path

REPLACEMENT = r'''        /// <summary>Play / resume / pause — ViewModel + IChatAudioPlaybackService.</summary>
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

START_MARKERS = [
    "        /// <summary>Play / resume / pause for a ready local audio bubble + SMTC.</summary>",
    "        /// <summary>Play / resume / pause for a ready local audio bubble.</summary>",
]

# End markers that follow the audio block in each shell
END_MARKERS = [
    "        /// <summary>Trash: fade timer",
    "        private async void CancelRecordingButton_Click",
    "        private void MessageInput_TextChanged",
    "        private void Composer",
]

paths = [
    Path(r"c:\unison-github-new\Unison\src\Unison.Uwp\Shell\WhatsApp\Views\ChatDetailView.xaml.cs"),
    Path(r"c:\unison-github-new\Unison\src\Unison.Uwp\Shell\Unison\Views\ChatDetailView.xaml.cs"),
    Path(r"c:\unison-github-new\Unison\src\Unison.Uwp\UI\Views\ChatDetailView.xaml.cs"),
]

for path in paths:
    text = path.read_text(encoding="utf-8")
    start = None
    for m in START_MARKERS:
        if m in text:
            start = text.index(m)
            break
    if start is None:
        # already patched?
        if "CycleAudioPlaybackSpeed(ChatMessageViewModel vm)" in text:
            print("skip already", path.name)
            continue
        print("NO START", path)
        continue
    end = None
    for m in END_MARKERS:
        idx = text.find(m, start + 10)
        if idx != -1:
            end = idx
            break
    if end is None:
        print("NO END", path)
        continue
    path.write_text(text[:start] + REPLACEMENT + text[end:], encoding="utf-8")
    print("patched", path.name, "removed", end - start, "chars")
