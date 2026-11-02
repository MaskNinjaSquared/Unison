using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;

namespace Unison.Uwp.UI.Helpers
{
    /// <summary>
    /// Flies a ghost mic glyph from the composer mic to the recording-bar mic.
    /// Does not alter recording-bar layout — only a temporary overlay on <paramref name="host"/>.
    /// </summary>
    public static class RecordingMicFlyAnimator
    {
        public static Point? CaptureOriginCenter(FrameworkElement micButton, UIElement relativeTo)
        {
            if (micButton == null || relativeTo == null)
            {
                return null;
            }

            try
            {
                GeneralTransform t = micButton.TransformToVisual(relativeTo);
                return t.TransformPoint(
                    new Point(micButton.ActualWidth / 2.0, micButton.ActualHeight / 2.0));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[RecordingMicFlyAnimator] CaptureOrigin: " + ex.Message);
                return null;
            }
        }

        public static Task PlayAsync(
            Panel host,
            Point originCenter,
            FrameworkElement targetMicIcon,
            FontFamily iconFont,
            Brush foreground,
            double fontSize = 22)
        {
            var tcs = new TaskCompletionSource<bool>();
            if (host == null || targetMicIcon == null)
            {
                tcs.TrySetResult(false);
                return tcs.Task;
            }

            try
            {
                host.UpdateLayout();
                targetMicIcon.UpdateLayout();

                GeneralTransform toHost = targetMicIcon.TransformToVisual(host);
                Point targetCenter = toHost.TransformPoint(
                    new Point(targetMicIcon.ActualWidth / 2.0, targetMicIcon.ActualHeight / 2.0));

                double half = fontSize / 2.0;
                var ghost = new TextBlock
                {
                    Text = "\uE720",
                    FontFamily = iconFont,
                    FontSize = fontSize,
                    Foreground = foreground ?? new SolidColorBrush(Windows.UI.Colors.Red),
                    IsHitTestVisible = false,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new TranslateTransform
                    {
                        X = originCenter.X - half,
                        Y = originCenter.Y - half
                    }
                };

                double savedOpacity = targetMicIcon.Opacity;
                targetMicIcon.Opacity = 0;
                host.Children.Add(ghost);

                var storyboard = new Storyboard();
                var moveX = new DoubleAnimation
                {
                    To = targetCenter.X - half,
                    Duration = TimeSpan.FromMilliseconds(280),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                var moveY = new DoubleAnimation
                {
                    To = targetCenter.Y - half,
                    Duration = TimeSpan.FromMilliseconds(280),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(moveX, ghost.RenderTransform);
                Storyboard.SetTargetProperty(moveX, "X");
                Storyboard.SetTarget(moveY, ghost.RenderTransform);
                Storyboard.SetTargetProperty(moveY, "Y");
                storyboard.Children.Add(moveX);
                storyboard.Children.Add(moveY);

                EventHandler<object> completed = null;
                completed = (s, e) =>
                {
                    storyboard.Completed -= completed;
                    try
                    {
                        host.Children.Remove(ghost);
                    }
                    catch
                    {
                    }

                    targetMicIcon.Opacity = savedOpacity;
                    tcs.TrySetResult(true);
                };
                storyboard.Completed += completed;
                storyboard.Begin();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[RecordingMicFlyAnimator] Play: " + ex.Message);
                try
                {
                    if (targetMicIcon != null)
                    {
                        targetMicIcon.Opacity = 1;
                    }
                }
                catch
                {
                }

                tcs.TrySetResult(false);
            }

            return tcs.Task;
        }
    }
}
