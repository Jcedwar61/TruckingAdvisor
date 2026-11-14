using System.Globalization;
using System.Windows.Data;
using TruckingAdvisor.Shared;
using TruckingAdvisor.Shared.Models;

namespace TruckingAdvisor.UI;

/// <summary>Trip list line: distance in the chosen units, plus the game's net income when it reported one.</summary>
public sealed class TripFactsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DeliveryTripRecord t) return "";
        string text = Units.DistText(t.DistanceMiles);
        if (t.PaidRevenue > 0 || t.HasCosts) text += $"  •  net {Units.Money(t.NetIncome, t.GameId)}";
        return text;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
