using System.Windows.Controls;

namespace TruckingAdvisor.UI;

/// <summary>One choice in an enum picker, shown with a friendly spaced label ("Old School Trucker").</summary>
public sealed record EnumOption<T>(T Value, string Label) where T : struct, Enum
{
    public override string ToString() => Label;
}

/// <summary>Combo boxes over enums: labelled options instead of raw PascalCase enum names.</summary>
public static class EnumPicker
{
    public static void Fill<T>(ComboBox box, Func<T, string>? label = null) where T : struct, Enum
    {
        box.DisplayMemberPath = nameof(EnumOption<T>.Label);
        box.ItemsSource = Enum.GetValues<T>().Select(v => new EnumOption<T>(v, label?.Invoke(v) ?? Spaced(v.ToString()))).ToList();
    }

    public static void Select<T>(ComboBox box, T value) where T : struct, Enum =>
        box.SelectedItem = box.ItemsSource?.Cast<EnumOption<T>>().FirstOrDefault(o => Equals(o.Value, value));

    public static bool TryGet<T>(ComboBox box, out T value) where T : struct, Enum
    {
        if (box.SelectedItem is EnumOption<T> option) { value = option.Value; return true; }
        value = default;
        return false;
    }

    public static string Spaced(string pascal) =>
        string.Concat(pascal.Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + ch : ch.ToString()));
}
