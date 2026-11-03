using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// Text status composer (prototype, full-screen page): background / font cycling, emoji tray,
    /// privacy and send chrome. Publishing is not wired — Send only shows the "not available yet" dialog.
    /// </summary>
    public sealed class StatusComposerViewModel : Observable
    {
        /// <summary>Segoe MDL2 emoji face (tray closed).</summary>
        public const string GlyphEmoji = "\uE76E";

        /// <summary>Segoe MDL2 keyboard (tray open, tap returns to typing).</summary>
        public const string GlyphKeyboard = "\uE765";

        /// <summary>Gap between emoji sections so the grid realizes one before the next.</summary>
        private const int EmojiCategoryStreamGapMs = 40;

        /// <summary>Tray + ProgressRing paint before the first glyphs realize (same as chat).</summary>
        private const int EmojiOpenPaintDelayMs = 150;

        /// <summary>Primary text-status backgrounds (ARGB hex).</summary>
        public static readonly IReadOnlyList<string> BackgroundPalette = new[]
        {
            "#FF128C7E",
            "#FF5696FF",
            "#FF8294C9",
            "#FFC69FCC",
            "#FFFF7B6B",
            "#FFF0B330",
            "#FF243640",
            "#FF7E90A3",
            "#FFAE8774",
            "#FF8B6990",
            "#FF26C4DC",
            "#FF54C265"
        };

        /// <summary>Families shipped on Windows 10 desktop and Mobile.</summary>
        public static readonly IReadOnlyList<string> FontPalette = new[]
        {
            "Segoe UI",
            "Georgia",
            "Courier New",
            "Comic Sans MS",
            "Times New Roman"
        };

        private readonly IDispatcher _dispatcher;
        private readonly IDialogService _dialogs;
        private readonly IStringResources _strings;
        private readonly ISystemInfoProvider _systemInfo;
        private readonly INavigator _navigator;
        private readonly Random _random;
        private string _text = string.Empty;
        private string _backgroundColor = BackgroundPalette[0];
        private int _fontIndex;
        private bool _isEmojiTrayOpen;
        private bool _isEmojiCatalogLoading;
        private string _emojiSearchQuery = string.Empty;
        private int _emojiGeneration;

        public StatusComposerViewModel(
            IDispatcher dispatcher,
            IDialogService dialogs = null,
            IStringResources strings = null,
            ISystemInfoProvider systemInfo = null,
            INavigator navigator = null,
            Random random = null)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _dialogs = dialogs;
            _strings = strings;
            _systemInfo = systemInfo;
            _navigator = navigator;
            _random = random ?? new Random();

            BackCommand = new RelayCommand(Close);
            ToggleEmojiTrayCommand = new RelayCommand(ToggleEmojiTray);
            CycleBackgroundCommand = new RelayCommand(CycleBackground);
            CycleFontCommand = new RelayCommand(CycleFont);
            InsertEmojiCommand = new RelayCommand<string>(InsertEmoji, emoji => !string.IsNullOrWhiteSpace(emoji));
            SendCommand = new RelayCommand(() => _ = ShowSendUnavailableAsync(), () => CanSend);
            PrivacyCommand = new RelayCommand(() => _ = ShowPrivacyAsync());
        }

        public string Text
        {
            get => _text;
            set
            {
                if (!Set(ref _text, value ?? string.Empty))
                {
                    return;
                }

                OnPropertyChanged(nameof(CanSend));
                (SendCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public bool CanSend => !string.IsNullOrWhiteSpace(_text);

        /// <summary>Current page background as ARGB hex (one of <see cref="BackgroundPalette"/>).</summary>
        public string BackgroundColor
        {
            get => _backgroundColor;
            private set => Set(ref _backgroundColor, value);
        }

        /// <summary>Current text font family name (one of <see cref="FontPalette"/>).</summary>
        public string FontFamilyName => FontPalette[_fontIndex];

        /// <summary>Hardcoded until status privacy is wired.</summary>
        public string PrivacyLabel =>
            _strings?.Get("StatusComposer_PrivacyContacts", "Status: Contacts") ?? "Status: Contacts";

        public bool IsEmojiTrayOpen
        {
            get => _isEmojiTrayOpen;
            private set
            {
                if (Set(ref _isEmojiTrayOpen, value))
                {
                    OnPropertyChanged(nameof(EmojiToggleGlyph));
                }
            }
        }

        public string EmojiToggleGlyph => _isEmojiTrayOpen ? GlyphKeyboard : GlyphEmoji;

        /// <summary>True until the first emoji section is in <see cref="EmojiCategories"/>.</summary>
        public bool IsEmojiCatalogLoading
        {
            get => _isEmojiCatalogLoading;
            private set => Set(ref _isEmojiCatalogLoading, value);
        }

        public string EmojiSearchQuery
        {
            get => _emojiSearchQuery;
            set
            {
                if (Set(ref _emojiSearchQuery, value ?? string.Empty) && _isEmojiTrayOpen)
                {
                    _ = LoadEmojiCategoriesAsync();
                }
            }
        }

        public ObservableCollection<EmojiCategory> EmojiCategories { get; } =
            new ObservableCollection<EmojiCategory>();

        public ICommand BackCommand { get; }

        public ICommand ToggleEmojiTrayCommand { get; }

        public ICommand CycleBackgroundCommand { get; }

        public ICommand CycleFontCommand { get; }

        public ICommand InsertEmojiCommand { get; }

        public ICommand SendCommand { get; }

        public ICommand PrivacyCommand { get; }

        /// <summary>Fresh draft: empty text, random background, first font, tray closed.</summary>
        public void Reset()
        {
            Text = string.Empty;
            BackgroundColor = BackgroundPalette[_random.Next(BackgroundPalette.Count)];
            SetFontIndex(0);
            CloseEmojiTray();
        }

        /// <summary>Back closes the emoji tray first, then the composer. Always consumed.</summary>
        public bool TryHandleBack()
        {
            if (_isEmojiTrayOpen)
            {
                CloseEmojiTray();
                return true;
            }

            Close();
            return true;
        }

        private void Close()
        {
            CloseEmojiTray();
            _navigator?.CloseFullScreen();
        }

        private void CycleBackground()
        {
            int current = IndexOf(BackgroundPalette, _backgroundColor);
            int next = _random.Next(BackgroundPalette.Count - 1);
            if (current >= 0 && next >= current)
            {
                next++;
            }

            BackgroundColor = BackgroundPalette[next];
        }

        private void CycleFont()
        {
            SetFontIndex((_fontIndex + 1) % FontPalette.Count);
        }

        private void SetFontIndex(int index)
        {
            if (_fontIndex == index)
            {
                return;
            }

            _fontIndex = index;
            OnPropertyChanged(nameof(FontFamilyName));
        }

        private void InsertEmoji(string emoji)
        {
            if (string.IsNullOrWhiteSpace(emoji))
            {
                return;
            }

            Text = _text + emoji.Trim();
        }

        private void ToggleEmojiTray()
        {
            if (_isEmojiTrayOpen)
            {
                CloseEmojiTray();
                return;
            }

            IsEmojiTrayOpen = true;
            _ = LoadEmojiCategoriesAsync();
        }

        private void CloseEmojiTray()
        {
            if (!_isEmojiTrayOpen)
            {
                return;
            }

            _emojiGeneration++;
            IsEmojiTrayOpen = false;
            IsEmojiCatalogLoading = false;
        }

        private async Task LoadEmojiCategoriesAsync()
        {
            int generation = ++_emojiGeneration;
            bool StillWanted() => generation == _emojiGeneration && _isEmojiTrayOpen;

            await _dispatcher.RunAsync(() =>
            {
                if (!StillWanted())
                {
                    return;
                }

                IsEmojiCatalogLoading = true;
                EmojiCategories.Clear();
            }).ConfigureAwait(false);

            await Task.Delay(EmojiOpenPaintDelayMs).ConfigureAwait(false);

            DeviceCategory device = _systemInfo != null ? _systemInfo.GetDeviceCategory() : DeviceCategory.High;
            IReadOnlyList<EmojiCategory> planned = EmojiCatalog.Filter(
                EmojiCatalog.BuiltInCategoriesFor(device),
                _emojiSearchQuery);

            for (int i = 0; i < planned.Count && StillWanted(); i++)
            {
                EmojiCategory section = planned[i];
                await _dispatcher.RunAsync(() =>
                {
                    if (!StillWanted())
                    {
                        return;
                    }

                    EmojiCategories.Add(section);
                    IsEmojiCatalogLoading = false;
                }).ConfigureAwait(false);

                await Task.Delay(EmojiCategoryStreamGapMs).ConfigureAwait(false);
            }

            await _dispatcher.RunAsync(() =>
            {
                if (generation == _emojiGeneration)
                {
                    IsEmojiCatalogLoading = false;
                }
            }).ConfigureAwait(false);
        }

        private async Task ShowSendUnavailableAsync()
        {
            if (_dialogs == null)
            {
                return;
            }

            string title = _strings?.Get("StatusComposer_Title", "Text status") ?? "Text status";
            string body = _strings?.Get("StatusList_NewStatusUnavailable", "This option is not available yet.")
                ?? "This option is not available yet.";
            await ShowDialogAsync(title, body).ConfigureAwait(false);
        }

        private async Task ShowPrivacyAsync()
        {
            if (_dialogs == null)
            {
                return;
            }

            string title = _strings?.Get("StatusComposer_PrivacyTitle", "Status privacy") ?? "Status privacy";
            await ShowDialogAsync(title, string.Empty).ConfigureAwait(false);
        }

        private async Task ShowDialogAsync(string title, string body)
        {
            string close = _strings?.Get("Common_Close", "Close") ?? "Close";
            try
            {
                await _dialogs.ShowMessageAsync(title, body, close).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[StatusComposer] Dialog failed: " + ex.Message);
            }
        }

        private static int IndexOf(IReadOnlyList<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
