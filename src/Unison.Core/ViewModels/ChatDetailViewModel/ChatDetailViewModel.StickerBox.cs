using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    public partial class ChatDetailViewModel
    {
        /// <summary>Segoe MDL2 emoji / sticker face.</summary>
        private const string StickerGlyphClosed = "\uE76E";

        /// <summary>Segoe MDL2 keyboard — shown while the sticker box is open.</summary>
        private const string StickerGlyphOpen = "\uE765";

        /// <summary>
        /// Gap between category adds so the grid can realize one section before the next
        /// (hitch when dumping the full catalog in one hop). Same on every device.
        /// </summary>
        private const int EmojiCategoryStreamGapMs = 40;

        /// <summary>
        /// Tray + ProgressRing must paint before the first glyphs realize: opening fast with a
        /// short ring beats a frozen tap. Task.Yield alone does not guarantee a rendered frame.
        /// Same on every device.
        /// </summary>
        private const int EmojiOpenPaintDelayMs = 150;

        private IEmojiRecentStore _emojiRecents;
        private bool _isStickerBoxOpen;
        private int _composerCaret;
        private string _emojiSearchQuery = string.Empty;
        private bool _emojiCategoriesDirty = true;
        private bool _isEmojiCatalogLoading;
        private bool _emojiLoadInFlight;
        private int _emojiLoadGeneration;

        /// <summary>Emoji / GIF / sticker tray under the composer.</summary>
        public bool IsStickerBoxOpen
        {
            get => _isStickerBoxOpen;
            private set
            {
                if (!Set(ref _isStickerBoxOpen, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(StickerToggleGlyph));
                (ToggleStickerBoxCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        /// <summary>Sticker face when closed; keyboard when open (tap toggles).</summary>
        public string StickerToggleGlyph =>
            IsStickerBoxOpen ? StickerGlyphOpen : StickerGlyphClosed;

        /// <summary>
        /// True until the first emoji section is in <see cref="EmojiCategories"/> (ProgressRing).
        /// Later sections keep streaming with the list already visible.
        /// </summary>
        public bool IsEmojiCatalogLoading
        {
            get => _isEmojiCatalogLoading;
            private set => Set(ref _isEmojiCatalogLoading, value);
        }

        /// <summary>
        /// Filters <see cref="EmojiCategories"/> (title or glyph substring). Bound to the
        /// themed search box above the emoji list.
        /// </summary>
        public string EmojiSearchQuery
        {
            get => _emojiSearchQuery;
            set
            {
                string next = value ?? string.Empty;
                if (!Set(ref _emojiSearchQuery, next))
                {
                    return;
                }

                if (IsStickerBoxOpen)
                {
                    _ = EnsureEmojiCategoriesAsync(force: true);
                }
                else
                {
                    _emojiCategoriesDirty = true;
                }
            }
        }

        /// <summary>Recents + built-in categories for the sequential emoji list.</summary>
        public ObservableCollection<EmojiCategory> EmojiCategories { get; } =
            new ObservableCollection<EmojiCategory>();

        public ICommand ToggleStickerBoxCommand { get; private set; }

        public ICommand InsertEmojiCommand { get; private set; }

        private void InitializeStickerBox(IEmojiRecentStore emojiRecents)
        {
            _emojiRecents = emojiRecents;
            ToggleStickerBoxCommand = new RelayCommand(
                ToggleStickerBox,
                () => CanCompose || IsStickerBoxOpen);
            InsertEmojiCommand = new RelayCommand<string>(
                InsertEmoji,
                emoji => CanCompose && !string.IsNullOrWhiteSpace(emoji));

            // Defer catalog materialization until the tray opens — chat open must stay cheap.
            if (_emojiRecents != null)
            {
                _emojiRecents.Changed += OnEmojiRecentsChanged;
            }
        }

        private void OnEmojiRecentsChanged(object sender, EventArgs e)
        {
            if (IsStickerBoxOpen)
            {
                _ = EnsureEmojiCategoriesAsync(force: true);
            }
            else
            {
                _emojiCategoriesDirty = true;
            }
        }

        public void ToggleStickerBox()
        {
            if (IsStickerBoxOpen)
            {
                CloseStickerBox();
                return;
            }

            if (!CanCompose)
            {
                return;
            }

            CloseMentionMenu();
            IsStickerBoxOpen = true;
            _ = EnsureEmojiCategoriesAsync(force: false);
        }

        public void CloseStickerBox()
        {
            if (!IsStickerBoxOpen)
            {
                return;
            }

            _emojiLoadGeneration++;
            if (!string.IsNullOrEmpty(_emojiSearchQuery))
            {
                _emojiSearchQuery = string.Empty;
                OnPropertyChanged(nameof(EmojiSearchQuery));
                _emojiCategoriesDirty = true;
            }

            IsStickerBoxOpen = false;
            IsEmojiCatalogLoading = false;
        }

        /// <summary>True when the tray consumed Back (closed without leaving the chat).</summary>
        public bool TryCloseStickerBoxOnBack()
        {
            if (!IsStickerBoxOpen)
            {
                return false;
            }

            CloseStickerBox();
            return true;
        }

        public void InsertEmoji(string emoji)
        {
            if (string.IsNullOrWhiteSpace(emoji) || !CanCompose)
            {
                return;
            }

            string glyph = emoji.Trim();
            string text = MessageText ?? string.Empty;
            int caret = _composerCaret;
            if (caret < 0 || caret > text.Length)
            {
                caret = text.Length;
            }

            MessageText = text.Insert(caret, glyph);
            MentionInsertCaret = caret + glyph.Length;
            _composerCaret = MentionInsertCaret;
            // Recents persist only after a successful send (RecordFromSentText).
            RaiseSendMessageCanExecuteChanged();
        }

        private void NoteComposerCaret(int caret)
        {
            _composerCaret = caret < 0 ? 0 : caret;
        }

        private void RecordSentEmojis(string sentText)
        {
            if (string.IsNullOrEmpty(sentText) || _emojiRecents == null)
            {
                return;
            }

            _emojiRecents.RecordFromSentText(sentText);
        }

        private async Task EnsureEmojiCategoriesAsync(bool force)
        {
            if (!force && !_emojiCategoriesDirty && EmojiCategories.Count > 0)
            {
                return;
            }

            // Bumping generation cancels any in-flight stream (its loops bail on mismatch).
            if (_emojiLoadInFlight)
            {
                _emojiCategoriesDirty = true;
            }

            int generation = ++_emojiLoadGeneration;
            _emojiLoadInFlight = true;
            IsEmojiCatalogLoading = true;

            try
            {
                await _dispatcher.RunAsync(() =>
                {
                    if (generation != _emojiLoadGeneration)
                    {
                        return;
                    }

                    EmojiCategories.Clear();
                }).ConfigureAwait(false);

                await Task.Delay(EmojiOpenPaintDelayMs).ConfigureAwait(false);
                if (generation != _emojiLoadGeneration || !IsStickerBoxOpen)
                {
                    return;
                }

                IReadOnlyList<EmojiCategory> planned = BuildEmojiCategoryPlan();
                bool firstPainted = false;

                for (int i = 0; i < planned.Count; i++)
                {
                    if (generation != _emojiLoadGeneration || !IsStickerBoxOpen)
                    {
                        return;
                    }

                    EmojiCategory section = planned[i];
                    await _dispatcher.RunAsync(() =>
                    {
                        if (generation != _emojiLoadGeneration || !IsStickerBoxOpen)
                        {
                            return;
                        }

                        EmojiCategories.Add(section);
                        if (!firstPainted)
                        {
                            IsEmojiCatalogLoading = false;
                            firstPainted = true;
                        }
                    }).ConfigureAwait(false);

                    if (i + 1 >= planned.Count)
                    {
                        break;
                    }

                    await Task.Delay(EmojiCategoryStreamGapMs).ConfigureAwait(false);
                }

                if (generation == _emojiLoadGeneration)
                {
                    _emojiCategoriesDirty = false;
                }
            }
            finally
            {
                if (generation == _emojiLoadGeneration)
                {
                    _emojiLoadInFlight = false;
                    if (IsEmojiCatalogLoading)
                    {
                        IsEmojiCatalogLoading = false;
                    }

                    // A search/recents change arrived while we streamed — run again.
                    if (_emojiCategoriesDirty && IsStickerBoxOpen)
                    {
                        _ = EnsureEmojiCategoriesAsync(force: true);
                    }
                }
            }
        }

        private IReadOnlyList<EmojiCategory> BuildEmojiCategoryPlan()
        {
            var full = new List<EmojiCategory>();
            IReadOnlyList<string> recents = _emojiRecents?.Recents;
            if (recents != null && recents.Count > 0)
            {
                full.Add(new EmojiCategory(
                    EmojiCatalog.RecentsId,
                    "Recents",
                    "\uE81C",
                    false,
                    recents));
            }

            DeviceCategory device = _systemInfo != null
                ? _systemInfo.GetDeviceCategory()
                : DeviceCategory.High;
            IReadOnlyList<EmojiCategory> builtIn = EmojiCatalog.BuiltInCategoriesFor(device);
            for (int i = 0; i < builtIn.Count; i++)
            {
                full.Add(builtIn[i]);
            }

            return EmojiCatalog.Filter(full, _emojiSearchQuery);
        }
    }
}
