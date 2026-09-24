using System.Windows;
using System.Windows.Controls;
using HostsManager.Core;
using HostsManager.Services;

namespace HostsManager.Views;

public sealed class BrowserFlagOption(string label, string description, string flag, string? edgeFlag = null)
{
    public string Label { get; } = label;
    public string Description { get; } = description;
    public string Flag { get; } = flag;
    public string? EdgeFlag { get; } = edgeFlag;
    public bool IsSelected { get; set; }
    public string ForBrowser(ChromiumBrowserKind kind) =>
        kind == ChromiumBrowserKind.Edge ? EdgeFlag ?? Flag : Flag;
}

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
    private bool _initializing = true;
    private bool _appearanceLoaded;
    public BrowserPreviewAppearance Appearance => AppearancePicker.Appearance;
    private readonly BrowserFlagOption[] _flagOptions =
    {
        new("Ignore certificate errors", "Open HTTPS sites with invalid or self-signed certificates.",
            "--ignore-certificate-errors"),
        new("Disable web security (CORS)", "Allow cross-origin requests normally blocked by the browser.",
            "--disable-web-security"),
        new("Allow insecure content", "Allow HTTP content to run inside HTTPS pages.",
            "--allow-running-insecure-content"),
        new("Open DevTools", "Open developer tools automatically for each tab.",
            "--auto-open-devtools-for-tabs"),
        new("Private browsing", "Use Incognito in Chrome or InPrivate in Edge.",
            "--incognito", "--inprivate"),
        new("Disable extensions", "Start the browser with extensions disabled.",
            "--disable-extensions"),
    };

    public BrowserPreviewDialog(IReadOnlyList<HostsLine> lines,
        IReadOnlyList<ChromiumBrowser> browsers, string additionalFlags = "")
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
        BrowserBox.SelectedItem = browsers.FirstOrDefault(browser =>
            browser.Kind == BrowserPreviewPreferences.LastSelected) ?? browsers.FirstOrDefault();
        StartPagesList.ItemsSource = _startPages;
        var savedFlags = BrowserPreviewFlags.Parse(additionalFlags).ToList();
        foreach (var option in _flagOptions)
        {
            option.IsSelected = savedFlags.Remove(option.Flag);
            if (option.EdgeFlag is { } edgeFlag)
                option.IsSelected |= savedFlags.Remove(edgeFlag);
        }
        FlagOptionsList.ItemsSource = _flagOptions;
        FlagsBox.Text = string.Join(Environment.NewLine, savedFlags);
        MappingsText.Text = string.Join(Environment.NewLine,
            _overrides.Select(o => $"{o.Hostname}  →  {o.Target}"));

        _initializing = false;
        AppearancePicker.Changed += (_, _) => Validate();
        Validate();
    }

    public ChromiumBrowser SelectedBrowser =>
        (ChromiumBrowser)BrowserBox.SelectedItem;

    public IReadOnlyList<BrowserOverride> Overrides => _overrides;

    public string AdditionalFlags => string.Join(Environment.NewLine,
        BrowserPreviewFlags.Combine(FlagsBox.Text, _flagOptions
            .Where(option => option.IsSelected)
            .Select(option => option.ForBrowser(SelectedBrowser.Kind))));

    public IReadOnlyList<Uri> SelectedStartUris => _startPages
        .Where(page => page.IsSelected)
        .Select(page => new Uri(page.UrlText.Trim(), UriKind.Absolute))
        .ToArray();

    private void OnInputChanged(object sender, EventArgs e)
    {
        if (!_initializing && BrowserBox.SelectedItem is ChromiumBrowser browser)
            BrowserPreviewPreferences.Remember(browser.Kind);
        Validate();
    }

    private void OnManageProfiles(object sender, RoutedEventArgs e)
    {
        var dialog = new BrowserProfilesDialog { Owner = this };
        ThemeManager.Track(dialog);
        dialog.ShowDialog();
    }

    private void OnStartPageChanged(object sender, RoutedEventArgs e) => Validate();

    private void Validate()
    {
        if (_initializing || OpenButton is null || ErrorText is null || BrowserBox is null || _startPages is null) return;

        string? error = null;
        if (BrowserBox.SelectedItem is null)
            error = "Select a browser.";
        else if (!_startPages.Any(page => page.IsSelected))
            error = "Select at least one tab to open.";
        else if (_startPages.Where(page => page.IsSelected).Any(page =>
                     !Uri.TryCreate(page.UrlText.Trim(), UriKind.Absolute, out var uri)
                     || uri.Scheme is not ("http" or "https")))
            error = "Every selected tab needs a complete http:// or https:// URL.";

        if (error is null)
        {
            try
            {
                BrowserPreviewFlags.Parse(AdditionalFlags);
                if (!_appearanceLoaded)
                {
                    var profile = BrowserPreviewService.GetProfile(SelectedBrowser, _overrides, AdditionalFlags);
                    _appearanceLoaded = true;
                    AppearancePicker.LoadAppearance(profile.Metadata);
                }
                _ = Appearance;
            }
            catch (ArgumentException ex) { error = ex.Message; }
        }

        ErrorText.Text = error ?? "";
        OpenButton.IsEnabled = error is null;
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        Validate();
        if (!OpenButton.IsEnabled) return;

        BrowserPreviewPreferences.Remember(SelectedBrowser.Kind);
        DialogResult = true;
    }
}
