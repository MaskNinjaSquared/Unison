using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    public partial class ChatDetailViewModel
    {
        private bool _isChatBackgroundPickerOpen;
        private bool _isSolidColorPickerOpen;
        private ChatBackgroundKind _pickerSelectedKind = ChatBackgroundKind.Default;
        private string _pickerCustomPath;
        private string _pickerSolidColorHex;

        public bool IsChatBackgroundPickerOpen
        {
            get { return _isChatBackgroundPickerOpen; }
            private set
            {
                if (_isChatBackgroundPickerOpen == value)
                {
                    return;
                }

                _isChatBackgroundPickerOpen = value;
                OnPropertyChanged();
            }
        }

        public bool IsSolidColorPickerOpen
        {
            get { return _isSolidColorPickerOpen; }
            private set
            {
                if (_isSolidColorPickerOpen == value)
                {
                    return;
                }

                _isSolidColorPickerOpen = value;
                OnPropertyChanged();
            }
        }

        public ChatBackgroundKind PickerSelectedKind
        {
            get { return _pickerSelectedKind; }
            private set
            {
                if (_pickerSelectedKind == value)
                {
                    return;
                }

                _pickerSelectedKind = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PickerDoodlesOn));
                OnPropertyChanged(nameof(IsPickerDoodlesEnabled));
                OnPropertyChanged(nameof(IsPickerSolidColorEnabled));
                RefreshBackgroundOptionSelection();
            }
        }

        public string PickerCustomPath
        {
            get { return _pickerCustomPath; }
            private set
            {
                if (string.Equals(_pickerCustomPath, value, StringComparison.Ordinal))
                {
                    return;
                }

                _pickerCustomPath = value;
                OnPropertyChanged();
            }
        }

        public string PickerSolidColorHex
        {
            get { return _pickerSolidColorHex; }
            private set
            {
                if (string.Equals(_pickerSolidColorHex, value, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _pickerSolidColorHex = value;
                OnPropertyChanged();
                RefreshSolidColorSelection();
            }
        }

        /// <summary>
        /// WhatsApp doodles toggle — only meaningful for the Default family.
        /// Off when a preset/custom wallpaper is selected.
        /// </summary>
        public bool PickerDoodlesOn
        {
            get { return ChatBackgroundCatalog.UsesWhatsAppDoodles(PickerSelectedKind); }
        }

        /// <summary>Toggle is interactive only while Default / DefaultNoDoodles is selected.</summary>
        public bool IsPickerDoodlesEnabled
        {
            get { return ChatBackgroundCatalog.IsDefaultFamily(PickerSelectedKind); }
        }

        /// <summary>Solid-color row/overlay only for the Default family.</summary>
        public bool IsPickerSolidColorEnabled
        {
            get { return ChatBackgroundCatalog.IsDefaultFamily(PickerSelectedKind); }
        }

        public ObservableCollection<ChatBackgroundOption> BackgroundOptions { get; } =
            new ObservableCollection<ChatBackgroundOption>();

        public ObservableCollection<ChatBackgroundSolidColorOption> SolidColorOptions { get; } =
            new ObservableCollection<ChatBackgroundSolidColorOption>();

        public ICommand OpenChatBackgroundPickerCommand { get; private set; }

        public ICommand CloseChatBackgroundPickerCommand { get; private set; }

        public ICommand SelectChatBackgroundOptionCommand { get; private set; }

        public ICommand SetChatBackgroundDoodlesCommand { get; private set; }

        public ICommand OpenSolidColorPickerCommand { get; private set; }

        public ICommand CloseSolidColorPickerCommand { get; private set; }

        public ICommand SelectSolidColorOptionCommand { get; private set; }

        private void InitBackgroundPickerCommands()
        {
            OpenChatBackgroundPickerCommand = new RelayCommand(
                OpenChatBackgroundPicker,
                () => ActiveChat != null);
            CloseChatBackgroundPickerCommand = new RelayCommand(CloseChatBackgroundPicker);
            SelectChatBackgroundOptionCommand = new RelayCommand<ChatBackgroundOption>(
                option => _ = SelectBackgroundOptionAsync(option));
            SetChatBackgroundDoodlesCommand = new RelayCommand<bool?>(
                on => _ = SetBackgroundDoodlesAsync(on == true));
            OpenSolidColorPickerCommand = new RelayCommand(
                OpenSolidColorPicker,
                () => IsPickerSolidColorEnabled);
            CloseSolidColorPickerCommand = new RelayCommand(CloseSolidColorPicker);
            SelectSolidColorOptionCommand = new RelayCommand<ChatBackgroundSolidColorOption>(
                option => _ = SelectSolidColorOptionAsync(option));
        }

        public void OpenChatBackgroundPicker()
        {
            if (ActiveChat == null)
            {
                return;
            }

            EnsureBackgroundOptions();
            EnsureSolidColorOptions();
            PickerSelectedKind = ActiveChat.BackgroundKind;
            PickerCustomPath = ActiveChat.BackgroundPath;
            PickerSolidColorHex = ActiveChat.BackgroundSolidColorHex;
            RefreshBackgroundOptionSelection();
            RefreshSolidColorSelection();
            IsSolidColorPickerOpen = false;
            CloseChatDetailInfo();
            IsChatBackgroundPickerOpen = true;
        }

        public void CloseChatBackgroundPicker()
        {
            IsSolidColorPickerOpen = false;
            IsChatBackgroundPickerOpen = false;
        }

        public void OpenSolidColorPicker()
        {
            if (!IsPickerSolidColorEnabled)
            {
                return;
            }

            EnsureSolidColorOptions();
            RefreshSolidColorSelection();
            IsSolidColorPickerOpen = true;
        }

        public void CloseSolidColorPicker()
        {
            IsSolidColorPickerOpen = false;
        }

        public async Task SelectBackgroundOptionAsync(ChatBackgroundOption option)
        {
            if (option == null || ActiveChat == null || _chatStore == null)
            {
                return;
            }

            if (option.Kind == ChatBackgroundKind.Custom)
            {
                return;
            }

            // Gallery "Default" always restores doodles on; keep current solid color.
            ChatBackgroundKind kind = option.Kind == ChatBackgroundKind.DefaultNoDoodles
                ? ChatBackgroundKind.Default
                : option.Kind;
            string solid = ChatBackgroundCatalog.IsDefaultFamily(kind)
                ? PickerSolidColorHex
                : null;
            await ApplyBackgroundAsync(kind, null, solid).ConfigureAwait(true);
        }

        /// <summary>
        /// Host copied a wallpaper into LocalFolder; <paramref name="relativePath"/> is relative
        /// to that folder (e.g. ChatBackgrounds/{jid}/wall.jpg).
        /// </summary>
        public async Task ApplyCustomBackgroundPathAsync(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || ActiveChat == null || _chatStore == null)
            {
                return;
            }

            await ApplyBackgroundAsync(ChatBackgroundKind.Custom, relativePath.Trim(), null)
                .ConfigureAwait(true);
        }

        /// <summary>
        /// Doodles toggle: only persists when the chat is in the Default family.
        /// Choosing Cubes/Owls/Damask/Custom leaves doodles off until Default is selected again.
        /// </summary>
        public async Task SetBackgroundDoodlesAsync(bool doodlesOn)
        {
            if (ActiveChat == null || _chatStore == null)
            {
                return;
            }

            if (!ChatBackgroundCatalog.IsDefaultFamily(PickerSelectedKind) &&
                !ChatBackgroundCatalog.IsDefaultFamily(ActiveChat.BackgroundKind))
            {
                return;
            }

            ChatBackgroundKind kind = doodlesOn
                ? ChatBackgroundKind.Default
                : ChatBackgroundKind.DefaultNoDoodles;
            await ApplyBackgroundAsync(kind, null, PickerSolidColorHex).ConfigureAwait(true);
        }

        public async Task SelectSolidColorOptionAsync(ChatBackgroundSolidColorOption option)
        {
            if (option == null || ActiveChat == null || _chatStore == null)
            {
                return;
            }

            if (!IsPickerSolidColorEnabled)
            {
                return;
            }

            ChatBackgroundKind kind = ChatBackgroundCatalog.IsDefaultFamily(PickerSelectedKind)
                ? PickerSelectedKind
                : ChatBackgroundKind.Default;
            string hex = option.IsThemeDefault ? null : option.Hex;
            await ApplyBackgroundAsync(kind, null, hex).ConfigureAwait(true);
        }

        private async Task ApplyBackgroundAsync(
            ChatBackgroundKind kind,
            string path,
            string solidColorHex)
        {
            ChatItem chat = ActiveChat;
            if (chat == null || _chatStore == null)
            {
                return;
            }

            if (!ChatBackgroundCatalog.IsDefaultFamily(kind))
            {
                solidColorHex = null;
            }

            await _chatStore
                .SetBackgroundAsync(chat.JID ?? chat.Id, kind, path, solidColorHex)
                .ConfigureAwait(true);
            chat.BackgroundKind = kind;
            chat.BackgroundPath = kind == ChatBackgroundKind.Custom ? path : null;
            chat.BackgroundSolidColorHex = solidColorHex;
            PickerSelectedKind = kind;
            PickerCustomPath = chat.BackgroundPath;
            PickerSolidColorHex = chat.BackgroundSolidColorHex;
            (OpenSolidColorPickerCommand as RelayCommand)?.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(ActiveChat));
        }

        private void EnsureBackgroundOptions()
        {
            if (BackgroundOptions.Count > 0)
            {
                return;
            }

            BackgroundOptions.Add(new ChatBackgroundOption
            {
                Kind = ChatBackgroundKind.Default,
                Title = "Default",
                ThumbUri = ChatBackgroundCatalog.DefaultAssetUri
            });
            BackgroundOptions.Add(new ChatBackgroundOption
            {
                Kind = ChatBackgroundKind.Cubes,
                Title = "Cubes",
                ThumbUri = ChatBackgroundCatalog.CubesAssetUri
            });
            BackgroundOptions.Add(new ChatBackgroundOption
            {
                Kind = ChatBackgroundKind.Owls,
                Title = "Owls",
                ThumbUri = ChatBackgroundCatalog.OwlsAssetUri
            });
            BackgroundOptions.Add(new ChatBackgroundOption
            {
                Kind = ChatBackgroundKind.Damask,
                Title = "Damask",
                ThumbUri = ChatBackgroundCatalog.DamaskAssetUri
            });
        }

        private void EnsureSolidColorOptions()
        {
            if (SolidColorOptions.Count > 0)
            {
                return;
            }

            // First swatch: theme underfill (light/dark ChatDetailWallpaperBackgroundBrush); persists null.
            SolidColorOptions.Add(new ChatBackgroundSolidColorOption
            {
                Hex = null,
                Title = _strings != null
                    ? _strings.Get("ChatBackground_SolidColorTransparent", "Transparent")
                    : "Transparent",
                IsThemeDefault = true
            });

            for (int i = 0; i < ChatBackgroundCatalog.Wp8SolidColors.Count; i++)
            {
                (string hex, string title) = ChatBackgroundCatalog.Wp8SolidColors[i];
                SolidColorOptions.Add(new ChatBackgroundSolidColorOption
                {
                    Hex = hex,
                    Title = title,
                    IsThemeDefault = false
                });
            }
        }

        /// <summary>
        /// Marks the gallery thumb that matches the active kind. Default family
        /// (doodles on/off) both highlight the Default tile.
        /// </summary>
        private void RefreshBackgroundOptionSelection()
        {
            if (BackgroundOptions.Count == 0)
            {
                return;
            }

            for (int i = 0; i < BackgroundOptions.Count; i++)
            {
                ChatBackgroundOption option = BackgroundOptions[i];
                if (option == null)
                {
                    continue;
                }

                bool selected;
                if (ChatBackgroundCatalog.IsDefaultFamily(PickerSelectedKind))
                {
                    selected = option.Kind == ChatBackgroundKind.Default;
                }
                else
                {
                    selected = option.Kind == PickerSelectedKind;
                }

                option.IsSelected = selected;
            }
        }

        private void RefreshSolidColorSelection()
        {
            if (SolidColorOptions.Count == 0)
            {
                return;
            }

            for (int i = 0; i < SolidColorOptions.Count; i++)
            {
                ChatBackgroundSolidColorOption option = SolidColorOptions[i];
                if (option == null)
                {
                    continue;
                }

                bool selected;
                if (string.IsNullOrWhiteSpace(PickerSolidColorHex))
                {
                    selected = option.IsThemeDefault;
                }
                else
                {
                    selected = !option.IsThemeDefault &&
                               string.Equals(
                                   option.Hex,
                                   PickerSolidColorHex,
                                   StringComparison.OrdinalIgnoreCase);
                }

                option.IsSelected = selected;
            }
        }
    }
}
