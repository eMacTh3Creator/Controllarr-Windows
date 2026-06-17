using System;
using System.Globalization;
using System.Windows.Data;

namespace Controllarr.App.Helpers
{
    /// <summary>Formats a byte count (long) as a human-readable size, e.g. "1.4 GB".</summary>
    public sealed class SizeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        {
            long bytes = ToLong(value);
            return ViewModels.MainViewModel.FormatSize(bytes);
        }

        public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;

        internal static long ToLong(object value) => value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            float f => (long)f,
            _ => 0
        };
    }

    /// <summary>Formats a byte/sec rate (long) as a human-readable speed, e.g. "2.1 MB/s".</summary>
    public sealed class SpeedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
            => ViewModels.MainViewModel.FormatSpeed(SizeConverter.ToLong(value));

        public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;
    }

    /// <summary>Formats an ETA in seconds (int) as "1h 3m" / "--".</summary>
    public sealed class EtaConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
            => ViewModels.MainViewModel.FormatEta((int)SizeConverter.ToLong(value));

        public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;
    }

    /// <summary>Formats a fractional progress (0..1 float/double) as a percentage 0..100 (double) for a ProgressBar.</summary>
    public sealed class ProgressPercentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        {
            double p = value switch
            {
                float f => f,
                double d => d,
                _ => 0
            };
            return Math.Clamp(p * 100.0, 0, 100);
        }

        public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;
    }
}
