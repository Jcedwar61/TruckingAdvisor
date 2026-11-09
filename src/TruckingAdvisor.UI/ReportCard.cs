using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.UI;

/// <summary>Builds a shareable delivery "report card" image (PNG) from a recorded trip.</summary>
public static class ReportCard
{
    public static byte[] RenderPng(DeliveryTripRecord t, string driver)
    {
        var inv = CultureInfo.InvariantCulture;
        Brush Hex(string h) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(h));
        TextBlock T(string text, double size, string color, bool bold = false) => new()
        {
            Text = text, FontSize = size, Foreground = Hex(color), FontFamily = new FontFamily("Segoe UI"),
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2)
        };

        var stats = new UniformGrid { Columns = 3, Margin = new Thickness(0, 14, 0, 14) };
        void Stat(string label, string value)
        {
            var p = new StackPanel { Margin = new Thickness(0, 6, 12, 6) };
            p.Children.Add(T(label.ToUpperInvariant(), 12, "#94A3B8"));
            p.Children.Add(T(value, 22, "#F8FAFC", bold: true));
            stats.Children.Add(p);
        }
        Stat("Distance", $"{t.DistanceMiles.ToString("N0", inv)} mi");
        Stat("Driving time", $"{(int)t.DrivingDuration.TotalHours}h {t.DrivingDuration.Minutes:D2}m");
        Stat("Fuel economy", t.AverageMpg > 0 ? $"{t.AverageMpg.ToString("F1", inv)} MPG" : "--");
        Stat("Avg / max speed", $"{t.AverageSpeedMph:F0} / {t.MaxSpeedMph:F0} mph");
        Stat("Speeding / hard brakes", $"{t.SpeedingIncidents} / {t.HarshBrakingIncidents}");
        Stat("Damage truck / trailer", $"{t.AddedTruckDamagePercent:F1}% / {t.AddedTrailerDamagePercent:F1}%");

        var panel = new StackPanel { Margin = new Thickness(36) };
        panel.Children.Add(T("TRUCKINGADVISOR • DELIVERY REPORT", 16, "#F59E0B", bold: true));
        panel.Children.Add(T(string.IsNullOrEmpty(t.Cargo) ? "Delivery" : t.Cargo, 30, "#F8FAFC", bold: true));
        panel.Children.Add(T($"{t.Origin} → {t.Destination}", 18, "#94A3B8"));
        panel.Children.Add(stats);
        panel.Children.Add(T($"{t.ScoreBreakdown.FinalScore}/100", 56, "#10B981", bold: true));
        panel.Children.Add(T(t.ScoreBreakdown.RatingLabel.ToUpperInvariant(), 20, "#F59E0B", bold: true));
        panel.Children.Add(T($"Driver {driver} • {t.StartedAt.ToLocalTime():MMM d, yyyy}{(t.StartedMidDelivery ? " • partial trip" : "")}", 13, "#94A3B8"));

        var card = new Border { Width = 900, Background = Hex("#0B0F17"), BorderBrush = Hex("#F59E0B"), BorderThickness = new Thickness(0, 6, 0, 0), Child = panel };
        card.Measure(new Size(900, double.PositiveInfinity));
        card.Arrange(new Rect(card.DesiredSize));
        card.UpdateLayout();

        var bmp = new RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth * 2), (int)Math.Ceiling(card.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bmp.Render(card);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
