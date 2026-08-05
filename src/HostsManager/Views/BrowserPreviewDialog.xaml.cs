using System.Windows;
using System.Windows.Controls;
using HostsManager.Core;
using HostsManager.Services;

namespace HostsManager.Views;

public sealed class BrowserStartPageOption
{
    public BrowserStartPageOption(string urlText)
    {
        UrlText = urlText;
    }

    public bool IsSelected { get; set; } = true;
    public string UrlText { get; set; }
}

public partial class BrowserPreviewDialog : Window
{
    private readonly IReadOnlyList<BrowserOverride> _overrides;
    private readonly IReadOnlyList<BrowserStartPageOption> _startPages;

    public BrowserPreviewDialog(IReadOnlyList<HostsLine> lines,
        IReadOnlyList<ChromiumBrowser> browsers)
    {
        InitializeComponent();

        _overrides = BrowserOverrideRules.FromLines(lines);
        _startPages = lines
            .Select(line => line.PrimaryHostname?.TrimEnd('.'))
            .Where(hostname => !string.IsNullOrWhiteSpace(hostname))
            .Select(hostname => $"https://{hostname}/")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(url => new BrowserStartPageOption(url))
            .ToArray();

        HeadingText.Text = lines.Count == 1
            ? "Open in an isolated browser"
            : $"Open {lines.Count} entries in an isolated browser";
        BrowserBox.ItemsSource = browsers;
        BrowserBox.SelectedIndex = 0;
        StartPagesList.ItemsSource = _startPages;
        MappingsText.Text = string.Join(Environment.NewLine,
            _overrides.Select(o => $"{o.Hostname}  →  {o.Target}"));

        Validate();
    }

    public ChromiumBrowser SelectedBrowser =>
        (ChromiumBrowser)BrowserBox.SelectedItem;

    public IReadOnlyList<BrowserOverride> Overrides => _overrides;

    public IReadOnlyList<Uri> SelectedStartUris => _startPages
        .Where(page => page.IsSelected)
        .Select(page => new Uri(page.UrlText.Trim(), UriKind.Absolute))
        .ToArray();

    private void OnInputChanged(object sender, EventArgs e) => Validate();

    private void OnStartPageChanged(object sender, RoutedEventArgs e) => Validate();

    private void Validate()
    {
        if (OpenButton is null || ErrorText is null || BrowserBox is null || _startPages is null) return;

        string? error = null;
        if (BrowserBox.SelectedItem is null)
            error = "Select a browser.";
        else if (!_startPages.Any(page => page.IsSelected))
            error = "Select at least one tab to open.";
        else if (_startPages.Where(page => page.IsSelected).Any(page =>
                     !Uri.TryCreate(page.UrlText.Trim(), UriKind.Absolute, out var uri)
                     || uri.Scheme is not ("http" or "https")))
            error = "Every selected tab needs a complete http:// or https:// URL.";

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
