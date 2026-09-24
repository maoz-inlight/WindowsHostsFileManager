using System.Windows;
using System.Windows.Controls;
using HostsManager.Core;

namespace HostsManager.Views;

public partial class PreviewAppearancePicker : System.Windows.Controls.UserControl
{
    private sealed record Choice(string Name, string? Hex)
    {
        public string Swatch => Hex ?? "#808080";
    }
    private readonly Choice[] _colors =
    [
        new("Browser default", null), new("Blue", "#2563EB"), new("Green", "#16A34A"),
        new("Orange", "#EA580C"), new("Purple", "#9333EA"), new("Red", "#DC2626"),
        new("Teal", "#0D9488"), new("Pink", "#DB2777"), new("Custom", null)
    ];
    private bool _loading;
    public event EventHandler? Changed;

    public PreviewAppearancePicker()
    {
        InitializeComponent();
        PaletteBox.ItemsSource = _colors;
        PaletteBox.SelectedIndex = 0;
    }

    public BrowserPreviewAppearance Appearance => new BrowserPreviewAppearance(NameBox.Text, HexBox.Text).Normalize();

    public void LoadAppearance(BrowserPreviewProfileMetadata? metadata)
    {
        _loading = true;
        NameBox.Text = metadata?.Name ?? "";
        var color = metadata?.Color;
        PaletteBox.SelectedItem = _colors.FirstOrDefault(c => c.Hex == color) ?? _colors[^1];
        HexBox.Text = color ?? "";
        _loading = false;
    }

    private void OnPaletteChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || HexBox is null || PaletteBox.SelectedItem is not Choice choice || choice.Name == "Custom") return;
        HexBox.Text = choice.Hex ?? "";
    }

    private void OnChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (sender == HexBox && PaletteBox is not null)
        {
            _loading = true;
            PaletteBox.SelectedItem = _colors.FirstOrDefault(c =>
                string.Equals(c.Hex ?? "", HexBox.Text, StringComparison.OrdinalIgnoreCase)) ?? _colors[^1];
            _loading = false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnCustom(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
        try { dialog.Color = System.Drawing.ColorTranslator.FromHtml(HexBox.Text); }
        catch (Exception) { dialog.Color = System.Drawing.Color.RoyalBlue; }
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            HexBox.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
    }
}
