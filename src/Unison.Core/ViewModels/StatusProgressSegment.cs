using System;
using Unison.Core.Helpers;

namespace Unison.Core.ViewModels
{
    /// <summary>One progress segment in the Status viewer (fill 0–1, white bar).</summary>
    public sealed class StatusProgressSegment : Observable
    {
        private double _fill;
        private double _trackWidth = 24;

        /// <summary>0 = empty, 1 = complete. Current item animates; past items stay at 1.</summary>
        public double Fill
        {
            get => _fill;
            set
            {
                if (Set(ref _fill, value))
                {
                    OnPropertyChanged(nameof(FillWidth));
                }
            }
        }

        /// <summary>Pixel width of the track (set by the view from available width).</summary>
        public double TrackWidth
        {
            get => _trackWidth;
            set
            {
                if (Set(ref _trackWidth, value))
                {
                    OnPropertyChanged(nameof(FillWidth));
                }
            }
        }

        public double FillWidth => Math.Max(0, TrackWidth * Fill);

        public void NotifyFillWidth()
        {
            OnPropertyChanged(nameof(FillWidth));
        }
    }
}
