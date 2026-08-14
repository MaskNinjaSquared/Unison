using System;
using System.Numerics;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Uwp.Services;
using Windows.Foundation;
using Windows.Graphics.Display;
using Windows.Foundation.Metadata;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Controls
{
    public sealed partial class MessageBubbleChrome : UserControl
    {
        private static readonly int[] SupportedScales = new[] { 100, 120, 125, 150, 160, 200, 225, 250, 300, 400, 500 };

        private static readonly Color FallbackOutgoingHighlight = Color.FromArgb(255, 16, 102, 86);
        private static readonly Color FallbackIncomingHighlight = Color.FromArgb(255, 62, 62, 62);
        private static readonly Color FallbackOutgoingFill = Color.FromArgb(255, 0, 92, 75);
        private static readonly Color FallbackIncomingFill = Color.FromArgb(255, 54, 54, 54);

        private readonly bool _isWindowsMobile;
        private SpriteVisual _bubbleVisual;
        private bool _compositionReady;
        private bool _compositionAttempted;
        private bool _isLoaded;
        private string _lastKey;

        public MessageBubbleChrome()
        {
            var systemInfo = App.Services?.GetService<ISystemInfoProvider>();
            _isWindowsMobile = systemInfo != null
                ? systemInfo.IsMobile()
                : SystemInfoProvider.DetectIsMobile();

            this.InitializeComponent();
            Loaded += MessageBubbleChrome_Loaded;
            Unloaded += MessageBubbleChrome_Unloaded;
            SizeChanged += MessageBubbleChrome_SizeChanged;
            // ActualThemeChanged is Fall Creators (16299) only — absent on W10M Creators (15063).
            // Fallback colors use ThemeResource (auto Light/Dark). Desktop composition still
            // refreshes fill via this event when available.
            if (ApiInformation.IsEventPresent("Windows.UI.Xaml.FrameworkElement", "ActualThemeChanged"))
            {
                ActualThemeChanged += MessageBubbleChrome_ActualThemeChanged;
            }
            UpdateFallbackVisual();
        }

        public static readonly DependencyProperty IsFromMeProperty =
            DependencyProperty.Register(nameof(IsFromMe), typeof(bool), typeof(MessageBubbleChrome), new PropertyMetadata(false, OnBubblePropertyChanged));

        public static readonly DependencyProperty IsRunStartProperty =
            DependencyProperty.Register(nameof(IsRunStart), typeof(bool), typeof(MessageBubbleChrome), new PropertyMetadata(true, OnBubblePropertyChanged));

        public static readonly DependencyProperty ThemeModeProperty =
            DependencyProperty.Register(nameof(ThemeMode), typeof(string), typeof(MessageBubbleChrome), new PropertyMetadata("Dark", OnBubblePropertyChanged));

        public static readonly DependencyProperty BubbleSizeProperty =
            DependencyProperty.Register(nameof(BubbleSize), typeof(Size), typeof(MessageBubbleChrome), new PropertyMetadata(default(Size)));

        public static readonly DependencyProperty ForceFallbackProperty =
            DependencyProperty.Register(nameof(ForceFallback), typeof(bool), typeof(MessageBubbleChrome), new PropertyMetadata(false, OnBubblePropertyChanged));

        public static readonly DependencyProperty ContactUriProperty =
            DependencyProperty.Register(nameof(ContactUri), typeof(string), typeof(MessageBubbleChrome), new PropertyMetadata(null));

        public static readonly DependencyProperty ShowContactProperty =
            DependencyProperty.Register(nameof(ShowContact), typeof(bool), typeof(MessageBubbleChrome), new PropertyMetadata(false));

        public bool IsFromMe
        {
            get { return (bool)GetValue(IsFromMeProperty); }
            set { SetValue(IsFromMeProperty, value); }
        }

        public bool IsRunStart
        {
            get { return (bool)GetValue(IsRunStartProperty); }
            set { SetValue(IsRunStartProperty, value); }
        }

        public string ThemeMode
        {
            get { return (string)GetValue(ThemeModeProperty); }
            set { SetValue(ThemeModeProperty, value); }
        }

        public Size BubbleSize
        {
            get { return (Size)GetValue(BubbleSizeProperty); }
            set { SetValue(BubbleSizeProperty, value); }
        }

        public bool ForceFallback
        {
            get { return (bool)GetValue(ForceFallbackProperty); }
            set { SetValue(ForceFallbackProperty, value); }
        }

        /// <summary>Group author avatar URI (photo or empty for fallback glyph).</summary>
        public string ContactUri
        {
            get { return (string)GetValue(ContactUriProperty); }
            set { SetValue(ContactUriProperty, value); }
        }

        /// <summary>When true, the host should show the author avatar beside this bubble.</summary>
        public bool ShowContact
        {
            get { return (bool)GetValue(ShowContactProperty); }
            set { SetValue(ShowContactProperty, value); }
        }

        private static void OnBubblePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as MessageBubbleChrome;
            if (control == null)
            {
                return;
            }

            control.UpdateFallbackVisual();
            control.TryConfigureComposition();
        }

        private void MessageBubbleChrome_Loaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;
            UpdateFallbackVisual();
            TryConfigureComposition();
            UpdateVisualSize();
        }

        private void MessageBubbleChrome_Unloaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = false;
            if (_bubbleVisual != null)
            {
                ElementCompositionPreview.SetElementChildVisual(CompositionHost, null);
                _bubbleVisual = null;
            }

            _compositionReady = false;
            _compositionAttempted = false;
        }

        private void MessageBubbleChrome_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            BubbleSize = e.NewSize;
            UpdateVisualSize();
        }

        private void MessageBubbleChrome_ActualThemeChanged(FrameworkElement sender, object args)
        {
            // Fallback Borders/Paths already follow ThemeResource. Refresh composition fill only.
            _lastKey = null;
            TryConfigureComposition();
        }

        private void UpdateVisualSize()
        {
            if (_bubbleVisual == null)
            {
                return;
            }

            float width = (float)Math.Max(0.0, ActualWidth);
            float height = (float)Math.Max(0.0, ActualHeight);
            _bubbleVisual.Size = new Vector2(width, height);
        }

        private void TryConfigureComposition()
        {
            if (!_isLoaded || ForceFallback)
            {
                _compositionReady = false;
                FallbackHost.Visibility = Visibility.Visible;
                if (_bubbleVisual != null)
                {
                    ElementCompositionPreview.SetElementChildVisual(CompositionHost, null);
                    _bubbleVisual = null;
                }
                return;
            }

            if (!_compositionAttempted)
            {
                _compositionAttempted = true;
                _compositionReady = CanUseCompositionBubbles();
            }

            if (!_compositionReady)
            {
                FallbackHost.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                var compositor = ElementCompositionPreview.GetElementVisual(CompositionHost).Compositor;
                if (_bubbleVisual == null)
                {
                    _bubbleVisual = compositor.CreateSpriteVisual();
                    ElementCompositionPreview.SetElementChildVisual(CompositionHost, _bubbleVisual);
                }

                string variant = IsFromMe ? "outgoing" : "incoming";
                if (IsRunStart)
                {
                    variant += " first";
                }

                int scale = GetNearestScale();
                Color fillColor = ResolveBubbleFillColor();
                string key = variant + "|" + scale + "|" + fillColor;
                if (!string.Equals(_lastKey, key, StringComparison.Ordinal))
                {
                    _lastKey = key;
                    Debug.WriteLine("[MessageBubbleChrome] variant=" + variant + " scale=" + scale + " fill=" + fillColor);
                }

                string maskUri = BuildAssetUri("mask", variant, scale);
                var maskSurface = LoadedImageSurface.StartLoadFromUri(new Uri(maskUri));
                var maskSurfaceBrush = compositor.CreateSurfaceBrush(maskSurface);
                maskSurfaceBrush.Stretch = CompositionStretch.None;

                var maskNineGrid = compositor.CreateNineGridBrush();
                maskNineGrid.Source = maskSurfaceBrush;
                ConfigureInsets(maskNineGrid, IsFromMe, IsRunStart, scale);

                var colorBrush = compositor.CreateColorBrush(fillColor);
                var maskBrush = compositor.CreateMaskBrush();
                maskBrush.Source = colorBrush;
                maskBrush.Mask = maskNineGrid;

                _bubbleVisual.Brush = maskBrush;
                FallbackHost.Visibility = Visibility.Collapsed;
                UpdateVisualSize();
            }
            catch
            {
                _compositionReady = false;
                FallbackHost.Visibility = Visibility.Visible;
                if (_bubbleVisual != null)
                {
                    ElementCompositionPreview.SetElementChildVisual(CompositionHost, null);
                    _bubbleVisual = null;
                }
            }
        }

        private static void ConfigureInsets(CompositionNineGridBrush brush, bool isFromMe, bool isRunStart, int scale)
        {
            float factor = scale / 100f;
            float left = 8f;
            float top = 6f;
            float right = 8f;
            float bottom = 8f;

            if (isRunStart)
            {
                if (isFromMe)
                {
                    right = 12f;
                }
                else
                {
                    left = 12f;
                }
            }

            brush.LeftInset = left * factor;
            brush.TopInset = top * factor;
            brush.RightInset = right * factor;
            brush.BottomInset = bottom * factor;
        }

        private bool CanUseCompositionBubbles()
        {
            // Cada bolha de composicao cria superficies e brushes proprios. No Windows
            // 10 Mobile isso aumenta bastante o uso de memoria/GPU durante a rolagem.
            // O fallback usa apenas Border/Path e e muito mais barato no aparelho.
            if (_isWindowsMobile)
            {
                return false;
            }

            return
                ApiInformation.IsTypePresent("Windows.UI.Composition.CompositionNineGridBrush") &&
                ApiInformation.IsTypePresent("Windows.UI.Composition.LoadedImageSurface") &&
                ApiInformation.IsMethodPresent("Windows.UI.Composition.Compositor", "CreateMaskBrush") &&
                ApiInformation.IsMethodPresent("Windows.UI.Xaml.Hosting.ElementCompositionPreview", "SetElementChildVisual");
        }

        private static string BuildAssetUri(string family, string variant, int scale)
        {
            string escapedVariant = Uri.EscapeDataString(variant);
            return "ms-appx:///Assets/Bubbles/" + family + "/" + escapedVariant + ".scale-" + scale + ".png";
        }

        private static int GetNearestScale()
        {
            int currentScale;
            try
            {
                currentScale = (int)DisplayInformation.GetForCurrentView().ResolutionScale;
            }
            catch
            {
                currentScale = 100;
            }

            int best = SupportedScales[0];
            double bestDiff = Math.Abs(best - currentScale);
            for (int i = 1; i < SupportedScales.Length; i++)
            {
                int candidate = SupportedScales[i];
                double diff = Math.Abs(candidate - currentScale);
                if (diff < bestDiff)
                {
                    best = candidate;
                    bestDiff = diff;
                }
            }

            return best;
        }

        private Color ResolveBubbleFillColor()
        {
            string key = IsFromMe ? "ChatDetailSentBubbleBrush" : "ChatDetailReceivedBubbleBrush";
            return ResolveThemeColor(key, IsFromMe ? FallbackOutgoingFill : FallbackIncomingFill);
        }

        /// <summary>
        /// Desktop Composition only. Prefer Light/Dark ThemeDictionaries (RequestedTheme)
        /// so Creators-era code paths don't need ActualTheme.
        /// </summary>
        private Color ResolveThemeColor(string key, Color fallback)
        {
            string themeName = Application.Current.RequestedTheme == ApplicationTheme.Light
                ? "Light"
                : "Dark";

            Color color;
            if (TryFindThemeBrushColor(Application.Current.Resources, themeName, key, out color) ||
                TryFindThemeBrushColor(Application.Current.Resources, "Default", key, out color))
            {
                return color;
            }

            try
            {
                var brush = Application.Current.Resources[key] as SolidColorBrush;
                if (brush != null)
                {
                    return brush.Color;
                }
            }
            catch
            {
            }

            return fallback;
        }

        private static bool TryFindThemeBrushColor(
            ResourceDictionary root,
            string themeName,
            string brushKey,
            out Color color)
        {
            color = default(Color);
            if (root == null || string.IsNullOrEmpty(themeName) || string.IsNullOrEmpty(brushKey))
            {
                return false;
            }

            try
            {
                if (root.ThemeDictionaries != null &&
                    root.ThemeDictionaries.ContainsKey(themeName))
                {
                    var themeDict = root.ThemeDictionaries[themeName] as ResourceDictionary;
                    if (TryGetBrushColor(themeDict, brushKey, out color))
                    {
                        return true;
                    }
                }

                if (root.MergedDictionaries != null)
                {
                    for (int i = 0; i < root.MergedDictionaries.Count; i++)
                    {
                        if (TryFindThemeBrushColor(root.MergedDictionaries[i], themeName, brushKey, out color))
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryGetBrushColor(ResourceDictionary dict, string brushKey, out Color color)
        {
            color = default(Color);
            if (dict == null || !dict.ContainsKey(brushKey))
            {
                return false;
            }

            var brush = dict[brushKey] as SolidColorBrush;
            if (brush == null)
            {
                return false;
            }

            color = brush.Color;
            return true;
        }

        /// <summary>
        /// Layout only for the ThemeResource-backed fallback (sent/received host, corners, tail).
        /// Colors stay on ThemeResource so OS Light/Dark updates without a code paint pass.
        /// </summary>
        private void UpdateFallbackVisual()
        {
            if (!ForceFallback && _compositionReady)
            {
                return;
            }

            bool isFromMe = IsFromMe;
            bool showTail = IsRunStart && !_isWindowsMobile;

            OutgoingHost.Visibility = isFromMe ? Visibility.Visible : Visibility.Collapsed;
            IncomingHost.Visibility = isFromMe ? Visibility.Collapsed : Visibility.Visible;

            var outgoingRadius = new CornerRadius(16, 16, showTail ? 4 : 16, 16);
            var incomingRadius = new CornerRadius(16, 16, 16, showTail ? 4 : 16);
            OutgoingHighlightBorder.CornerRadius = outgoingRadius;
            OutgoingFillBorder.CornerRadius = outgoingRadius;
            IncomingHighlightBorder.CornerRadius = incomingRadius;
            IncomingFillBorder.CornerRadius = incomingRadius;

            OutgoingTailContainer.Visibility = (isFromMe && showTail) ? Visibility.Visible : Visibility.Collapsed;
            IncomingTailContainer.Visibility = (!isFromMe && showTail) ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
