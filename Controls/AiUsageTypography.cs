using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using VNotch.Services;

namespace VNotch.Controls;

internal static class AiUsageTypography
{
    internal static string PlanName(string? plan)
    {
        string name = plan?.Trim() ?? "";
        return name.ToLowerInvariant() switch
        {
            "plus" => "Plus", "pro" => "Pro", "max" => "Max", "free" => "Free",
            "team" => "Team", "teams" => "Teams", "business" => "Business", "enterprise" => "Enterprise",
            _ => name.Length == 0 ? "" : char.ToUpperInvariant(name[0]) + name[1..]
        };
    }

    internal static void EmphasizePercent(TextBlock text, string label, double percent, double numberSize)
    {
        string number = percent.ToString("0.#", Loc.GetCulture()) + "%";
        int index = label.IndexOf(number, StringComparison.Ordinal);
        if (index < 0) return;
        text.Inlines.Clear();
        if (index > 0) text.Inlines.Add(new Run(label[..index]));
        text.Inlines.Add(new Run(number) { FontSize = numberSize });
        if (index + number.Length < label.Length) text.Inlines.Add(new Run(label[(index + number.Length)..]));
        Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
    }

    internal static void Apply(FrameworkElement root, Thickness? textPadding = null)
    {
        TextElement.SetFontFamily(root, AppFonts.Get());
        TextElement.SetFontWeight(root, FontWeights.Bold);
        System.Windows.Media.TextOptions.SetTextFormattingMode(root, System.Windows.Media.TextFormattingMode.Display);
        System.Windows.Media.TextOptions.SetTextRenderingMode(root, System.Windows.Media.TextRenderingMode.Grayscale);
        System.Windows.Media.TextOptions.SetTextHintingMode(root, System.Windows.Media.TextHintingMode.Fixed);
        var textStyle = CreateStyle(root, typeof(TextBlock));
        // Keep text pixel-snapped on transparent notch surfaces so small glyphs stay crisp.
        textStyle.Setters.Add(new Setter(FrameworkElement.UseLayoutRoundingProperty, true));
        textStyle.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));
        textStyle.Setters.Add(new Setter(System.Windows.Media.TextOptions.TextFormattingModeProperty, System.Windows.Media.TextFormattingMode.Display));
        textStyle.Setters.Add(new Setter(System.Windows.Media.TextOptions.TextRenderingModeProperty, System.Windows.Media.TextRenderingMode.Grayscale));
        textStyle.Setters.Add(new Setter(System.Windows.Media.TextOptions.TextHintingModeProperty, System.Windows.Media.TextHintingMode.Fixed));
        if (textPadding is { } padding)
        {
            textStyle.Setters.Add(new Setter(TextBlock.PaddingProperty, padding));
            textStyle.Setters.Add(new Setter(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.MaxHeight));
        }
        textStyle.Seal();
        root.Resources[typeof(TextBlock)] = textStyle;

        var toolTipStyle = CreateStyle(root, typeof(ToolTip));
        toolTipStyle.Seal();
        root.Resources[typeof(ToolTip)] = toolTipStyle;
    }

    private static Style CreateStyle(FrameworkElement root, Type type)
    {
        var baseStyle = root.TryFindResource(type) is Style candidate && candidate.TargetType?.IsAssignableFrom(type) == true
            ? candidate
            : null;
        var style = new Style(type, baseStyle);
        style.Setters.Add(new Setter(TextElement.FontFamilyProperty, AppFonts.Get()));
        style.Setters.Add(new Setter(TextElement.FontWeightProperty, FontWeights.Bold));
        return style;
    }
}
