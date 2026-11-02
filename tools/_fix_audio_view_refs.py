from pathlib import Path
import re

paths = [
    Path(r"c:\unison-github-new\Unison\src\Unison.Uwp\Shell\WhatsApp\Views\ChatDetailView.xaml.cs"),
    Path(r"c:\unison-github-new\Unison\src\Unison.Uwp\Shell\Unison\Views\ChatDetailView.xaml.cs"),
    Path(r"c:\unison-github-new\Unison\src\Unison.Uwp\UI\Views\ChatDetailView.xaml.cs"),
]

for path in paths:
    text = path.read_text(encoding="utf-8")
    original = text

    # Remove voice routing field + assignment
    text = re.sub(
        r"\s*private readonly IVoicePlaybackRoutingService _voiceRouting;\r?\n",
        "\n",
        text,
    )
    text = re.sub(
        r"\s*_voiceRouting = App\.Services\?\.GetService<IVoicePlaybackRoutingService>\(\);\r?\n",
        "\n",
        text,
    )

    # Remove playing/player/timer fields
    text = re.sub(
        r"\s*private ChatMessage _playingAudioMessage;\r?\n",
        "\n",
        text,
    )
    text = re.sub(
        r"\s*private ChatMessageViewModel _playingAudioVm;\r?\n",
        "\n",
        text,
    )
    text = re.sub(
        r"\s*private MediaPlayer _audioMediaPlayer;\r?\n",
        "\n",
        text,
    )
    text = re.sub(
        r"\s*private DispatcherTimer _audioPositionTimer;\r?\n",
        "\n",
        text,
    )

    # Unloaded cleanup
    text = text.replace(
        """            StopAudioPlayback();
            StopDateSeparatorTimer();
            _voiceRouting?.DetachPlayer();
            TearDownAudioPlayer();
""",
        """            ViewModel?.DetachAudioPlaybackUi();
            StopDateSeparatorTimer();
""",
    )

    # Video open stop
    text = text.replace(
        "            StopAudioPlayback();\r\n",
        "            ViewModel?.StopAudioPlayback();\r\n",
    )
    text = text.replace(
        "            StopAudioPlayback();\n",
        "            ViewModel?.StopAudioPlayback();\n",
    )

    # Inject AttachUi near end of Loaded — look for StartDateSeparatorTimer in Loaded
    if "AttachAudioPlaybackUi(AudioPlayer)" not in text:
        text = text.replace(
            "            StartDateSeparatorTimer();\r\n        }",
            "            StartDateSeparatorTimer();\r\n            ViewModel?.AttachAudioPlaybackUi(AudioPlayer);\r\n        }",
        )
        text = text.replace(
            "            StartDateSeparatorTimer();\n        }",
            "            StartDateSeparatorTimer();\n            ViewModel?.AttachAudioPlaybackUi(AudioPlayer);\n        }",
        )

    # Fix ResolveAudioSmtcMetadata references if methods were deleted
    if "ResolveAudioSmtcMetadata(" in text and "private void ResolveAudioSmtcMetadata" not in text:
        text = text.replace(
            "ResolveAudioSmtcMetadata(messageVm?.Model, out title, out artist);",
            "ResolveVideoOrAudioSmtcMetadata(messageVm?.Model, out title, out artist);",
        )
        helper = r'''
        private void ResolveVideoOrAudioSmtcMetadata(ChatMessage message, out string title, out string artist)
        {
            var chat = ViewModel?.ActiveChat ?? _activeChat;
            bool isGroup = chat != null &&
                (chat.IsGroup ||
                 (!string.IsNullOrWhiteSpace(chat.JID) &&
                  chat.JID.IndexOf("@g.us", StringComparison.OrdinalIgnoreCase) >= 0));

            artist = "Chat";
            if (message != null)
            {
                if (message.IsFromMe)
                {
                    artist = LocalizedStrings.Get("Chat_SelfFallbackName", "You");
                }
                else if (!string.IsNullOrWhiteSpace(message.SenderName))
                {
                    artist = message.SenderName.Trim();
                }
                else if (!string.IsNullOrWhiteSpace(chat?.Name))
                {
                    artist = chat.Name.Trim();
                }
            }

            title = isGroup
                ? (!string.IsNullOrWhiteSpace(chat?.Name) ? chat.Name.Trim() : "Group")
                : "Unison";
        }

'''
        # Insert before ResolveVideoSmtcMetadata's closing / after the method that calls it
        anchor = "        private Tuple<string, string> ResolveVideoSmtcMetadata"
        if anchor in text and "ResolveVideoOrAudioSmtcMetadata" not in text:
            # put helper right before ResolveVideoSmtcMetadata
            text = text.replace(anchor, helper + anchor)

    if text != original:
        path.write_text(text, encoding="utf-8")
        print("fixed", path.name)
    else:
        print("no change", path.name)
