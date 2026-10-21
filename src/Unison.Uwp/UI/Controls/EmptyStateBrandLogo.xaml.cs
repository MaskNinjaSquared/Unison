using System;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Empty-pane Unison mark (transparent tile glyph). On Low and High, plays a short
    /// jump every few seconds while visible; Unsupported stays static.
    /// </summary>
    public sealed partial class EmptyStateBrandLogo : UserControl
    {
        private static readonly TimeSpan IdleBetweenJumps = TimeSpan.FromSeconds(4.5);

        private readonly DispatcherTimer _idleTimer = new DispatcherTimer();
        private Storyboard _jumpBoard;
        private bool _allowsMotion;
        private bool _isLoaded;

        public EmptyStateBrandLogo()
        {
            InitializeComponent();
            _idleTimer.Interval = IdleBetweenJumps;
            _idleTimer.Tick += IdleTimer_Tick;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += OnSizeChanged;
            RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityPropertyChanged);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;
            _allowsMotion = ResolveAllowsMotion();
            EnsureJumpBoard();
            SyncTimer();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = false;
            StopTimer();
            StopJump();
        }

        private static void OnVisibilityPropertyChanged(DependencyObject sender, DependencyProperty dp)
        {
            (sender as EmptyStateBrandLogo)?.SyncTimer();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            SyncTimer();
        }

        private static bool ResolveAllowsMotion()
        {
            try
            {
                ISystemInfoProvider info = App.Services?.GetService<ISystemInfoProvider>();
                if (info == null)
                {
                    return false;
                }

                // Low and High only — 512 MB Unsupported skips the Storyboard entirely.
                return info.GetDeviceCategory() >= DeviceCategory.Low;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void EnsureJumpBoard()
        {
            if (_jumpBoard != null || LogoTranslate == null)
            {
                return;
            }

            var anim = new DoubleAnimationUsingKeyFrames
            {
                EnableDependentAnimation = true
            };
            anim.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
                Value = 0
            });
            anim.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)),
                Value = -14,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
            anim.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(480)),
                Value = 0,
                EasingFunction = new BounceEase
                {
                    EasingMode = EasingMode.EaseOut,
                    Bounces = 2,
                    Bounciness = 3
                }
            });
            Storyboard.SetTarget(anim, LogoTranslate);
            Storyboard.SetTargetProperty(anim, "Y");

            _jumpBoard = new Storyboard();
            _jumpBoard.Children.Add(anim);
            _jumpBoard.Completed += JumpBoard_Completed;
        }

        private void IdleTimer_Tick(object sender, object e)
        {
            if (!CanAnimateNow())
            {
                StopTimer();
                return;
            }

            StopTimer();
            _jumpBoard?.Begin();
        }

        private bool CanAnimateNow()
        {
            // Parent EmptyStateGrid Collapsed still leaves this Visibility=Visible; size goes to 0.
            return _allowsMotion &&
                   _isLoaded &&
                   Visibility == Visibility.Visible &&
                   ActualWidth > 1 &&
                   ActualHeight > 1;
        }

        private void JumpBoard_Completed(object sender, object e)
        {
            if (LogoTranslate != null)
            {
                LogoTranslate.Y = 0;
            }

            SyncTimer();
        }

        private void SyncTimer()
        {
            if (CanAnimateNow() &&
                (_jumpBoard == null || _jumpBoard.GetCurrentState() != ClockState.Active))
            {
                if (!_idleTimer.IsEnabled)
                {
                    _idleTimer.Start();
                }

                return;
            }

            StopTimer();
        }

        private void StopTimer()
        {
            if (_idleTimer.IsEnabled)
            {
                _idleTimer.Stop();
            }
        }

        private void StopJump()
        {
            if (_jumpBoard == null)
            {
                return;
            }

            _jumpBoard.Stop();
            if (LogoTranslate != null)
            {
                LogoTranslate.Y = 0;
            }
        }
    }
}
