using System;
using Windows.Foundation.Metadata;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Inline GIF bubble (Unigram-shaped): static poster until play; muted loop via
    /// <see cref="MediaElement"/>. Viewport leave always pauses; resume depends on
    /// <see cref="ResumeWhenVisible"/> (Low+ after user play; never on Unsupported).
    /// </summary>
    /// <remarks>
    /// <c>EffectiveViewportChanged</c> needs UniversalApiContract 7 (1809+). On Creators
    /// Update (15063 / MinVersion) subscribing throws and tears down the chat — gate it.
    /// </remarks>
    public sealed partial class AnimationView : UserControl
    {
        /// <summary>1809+ only; false on Windows 10 Mobile Creators Update.</summary>
        private static readonly bool SupportsEffectiveViewport =
            ApiInformation.IsEventPresent(
                "Windows.UI.Xaml.FrameworkElement",
                "EffectiveViewportChanged");

        private bool _viewportRegistered;
        private bool _inViewport = true;

        public AnimationView()
        {
            InitializeComponent();
            Loaded += AnimationView_Loaded;
            Unloaded += AnimationView_Unloaded;
        }

        public static readonly DependencyProperty SourceUriProperty =
            DependencyProperty.Register(
                nameof(SourceUri),
                typeof(string),
                typeof(AnimationView),
                new PropertyMetadata(null, OnSourceUriChanged));

        public string SourceUri
        {
            get => (string)GetValue(SourceUriProperty);
            set => SetValue(SourceUriProperty, value);
        }

        public static readonly DependencyProperty ThumbnailProperty =
            DependencyProperty.Register(
                nameof(Thumbnail),
                typeof(ImageSource),
                typeof(AnimationView),
                new PropertyMetadata(null, OnThumbnailChanged));

        public ImageSource Thumbnail
        {
            get => (ImageSource)GetValue(ThumbnailProperty);
            set => SetValue(ThumbnailProperty, value);
        }

        public static readonly DependencyProperty IsLoopingEnabledProperty =
            DependencyProperty.Register(
                nameof(IsLoopingEnabled),
                typeof(bool),
                typeof(AnimationView),
                new PropertyMetadata(true));

        public bool IsLoopingEnabled
        {
            get => (bool)GetValue(IsLoopingEnabledProperty);
            set => SetValue(IsLoopingEnabledProperty, value);
        }

        public static readonly DependencyProperty IsPlayingProperty =
            DependencyProperty.Register(
                nameof(IsPlaying),
                typeof(bool),
                typeof(AnimationView),
                new PropertyMetadata(false, OnIsPlayingChanged));

        public bool IsPlaying
        {
            get => (bool)GetValue(IsPlayingProperty);
            set => SetValue(IsPlayingProperty, value);
        }

        public static readonly DependencyProperty ResumeWhenVisibleProperty =
            DependencyProperty.Register(
                nameof(ResumeWhenVisible),
                typeof(bool),
                typeof(AnimationView),
                new PropertyMetadata(false));

        /// <summary>When true, re-entering the viewport resumes after a prior user play (Low+).</summary>
        public bool ResumeWhenVisible
        {
            get => (bool)GetValue(ResumeWhenVisibleProperty);
            set => SetValue(ResumeWhenVisibleProperty, value);
        }

        public void Play()
        {
            ApplySource(SourceUri);
            try
            {
                if (Player != null && Player.Source != null)
                {
                    Player.Play();
                    if (ThumbnailImage != null)
                    {
                        ThumbnailImage.Opacity = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AnimationView] Play: " + ex.Message);
            }
        }

        public void Pause()
        {
            try
            {
                Player?.Pause();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AnimationView] Pause: " + ex.Message);
            }

            if (ThumbnailImage != null)
            {
                ThumbnailImage.Opacity = 1;
            }
        }

        private static void OnSourceUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((AnimationView)d).ApplySource(e.NewValue as string);
        }

        private static void OnThumbnailChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (AnimationView)d;
            if (view.ThumbnailImage != null)
            {
                view.ThumbnailImage.Source = e.NewValue as ImageSource;
                if (!view.IsPlaying)
                {
                    view.ThumbnailImage.Opacity = 1;
                }
            }
        }

        private static void OnIsPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (AnimationView)d;
            if ((bool)e.NewValue)
            {
                view.Play();
            }
            else
            {
                view.Pause();
            }
        }

        private void AnimationView_Loaded(object sender, RoutedEventArgs e)
        {
            if (ThumbnailImage != null && Thumbnail != null)
            {
                ThumbnailImage.Source = Thumbnail;
                ThumbnailImage.Opacity = IsPlaying ? 0 : 1;
            }

            ApplySource(SourceUri);
            EnsureViewportWatch();
            if (IsPlaying && _inViewport)
            {
                Play();
            }
            else
            {
                Pause();
            }
        }

        private void AnimationView_Unloaded(object sender, RoutedEventArgs e)
        {
            DetachViewportWatch();
            Pause();
            try
            {
                if (Player != null)
                {
                    Player.Source = null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AnimationView] Unload: " + ex.Message);
            }
        }

        private void EnsureViewportWatch()
        {
            if (_viewportRegistered || !SupportsEffectiveViewport)
            {
                return;
            }

            EffectiveViewportChanged += AnimationView_EffectiveViewportChanged;
            _viewportRegistered = true;
        }

        private void DetachViewportWatch()
        {
            if (!_viewportRegistered)
            {
                return;
            }

            EffectiveViewportChanged -= AnimationView_EffectiveViewportChanged;
            _viewportRegistered = false;
        }

        private void AnimationView_EffectiveViewportChanged(
            FrameworkElement sender,
            EffectiveViewportChangedEventArgs args)
        {
            bool inView = args.EffectiveViewport.Width > 0.5 && args.EffectiveViewport.Height > 0.5;
            if (inView == _inViewport)
            {
                return;
            }

            _inViewport = inView;
            if (!inView)
            {
                // Always pause when leaving the scroll viewport.
                if (IsPlaying)
                {
                    IsPlaying = false;
                }
                else
                {
                    Pause();
                }

                return;
            }

            // Re-entered viewport: Low+ may resume if the user already started playback.
            if (ResumeWhenVisible && !IsPlaying)
            {
                IsPlaying = true;
            }
        }

        private void ApplySource(string uriText)
        {
            if (Player == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(uriText))
            {
                Player.Source = null;
                return;
            }

            if (!Uri.TryCreate(uriText, UriKind.Absolute, out Uri uri) &&
                !Uri.TryCreate(uriText, UriKind.RelativeOrAbsolute, out uri))
            {
                return;
            }

            try
            {
                if (Player.Source != null &&
                    string.Equals(
                        Player.Source.AbsoluteUri,
                        uri.AbsoluteUri,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Player.Source = uri;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AnimationView] Source: " + ex.Message);
            }
        }

        private void Player_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (IsPlaying && _inViewport)
            {
                Play();
            }
        }

        private void Player_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (!IsLoopingEnabled || Player == null || !IsPlaying)
            {
                return;
            }

            try
            {
                Player.Position = TimeSpan.Zero;
                Player.Play();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AnimationView] Loop: " + ex.Message);
            }
        }

        private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine(
                "[AnimationView] MediaFailed: " + (e?.ErrorMessage ?? string.Empty));
            if (ThumbnailImage != null)
            {
                ThumbnailImage.Opacity = 1;
            }

            if (IsPlaying)
            {
                IsPlaying = false;
            }
        }
    }
}
