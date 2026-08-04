using System.Windows;
using System.Windows.Controls;
using HostsManager.Core;
using HostsManager.Services;

namespace HostsManager.Views;

public partial class BrowserPreviewDialog : Window
{
    private readonly IReadOnlyList<BrowserOverride> _overrides;

    public BrowserPreviewDialog(HostsLine line, IReadOnlyList<ChromiumBrowser> browsers)
    {
        InitializeComponent();

        _overrides = BrowserOverrideRules.FromLine(line);
        BrowserBox.ItemsSource = browsers;
        BrowserBox.SelectedIndex = 0;

        var hostname = line.PrimaryHostname?.TrimEnd('.')
            ?? throw new ArgumentException("The selected entry has no hostname.", nameof(line));
        UrlBox.Text = $"https://{hostname}/";
        MappingsText.Text = string.Join(Environment.NewLine,
            _overrides.Select(o => $"{o.Hostname}  →  {o.Target}"));

        Validate();
    }

    public ChromiumBrowser SelectedBrowser =>
        (ChromiumBrowser)BrowserBox.SelectedItem;

    public IReadOnlyList<BrowserOverride> Overrides => _overrides;

    public Uri StartUri => new(UrlBox.Text.Trim(), UriKind.Absolute);

    private void OnInputChanged(object sender, EventArgs e) => Validate();

    private void Validate()
    {
        if (OpenButton is null || ErrorText is null || UrlBox is null || BrowserBox is null) return;

        string? error = null;
        if (BrowserBox.SelectedItem is null)
            error = "Select a browser.";
        else if (!Uri.TryCreate(UrlBox.Text.Trim(), UriKind.Absolute, out var uri)
                 || uri.Scheme is not ("http" or "https"))
            error = "Enter a complete http:// or https:// URL.";

        ErrorText.Text = error ?? "";
        OpenButton.IsEnabled = error is null;
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        Validate();
        if (!OpenButton.IsEnabled) return;

        DialogResult = true;
    }
}
