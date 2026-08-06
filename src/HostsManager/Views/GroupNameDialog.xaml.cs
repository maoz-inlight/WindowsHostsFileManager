using System.Windows;
using HostsManager.Core;

namespace HostsManager.Views;

public partial class GroupNameDialog : Window
{
    private readonly IReadOnlyCollection<string> _existingNames;
    private readonly string? _currentName;

    public GroupNameDialog(string title, string prompt, IEnumerable<string> existingNames, string? currentName = null)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        _existingNames = existingNames.ToArray();
        _currentName = currentName;
        NameBox.Text = currentName ?? "";
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
            Validate();
        };
    }

    public string GroupName { get; private set; } = "";

    private void OnChanged(object sender, RoutedEventArgs e) => Validate();

    private void Validate()
    {
        if (AcceptButton is null) return;

        string? error = null;
        string? normalized = null;
        try
        {
            normalized = HostsGroups.NormalizeName(NameBox.Text);
        }
        catch (ArgumentException ex)
        {
            error = string.IsNullOrWhiteSpace(NameBox.Text) ? null : ex.Message;
        }

        if (normalized is not null && _existingNames.Any(name =>
                !string.Equals(name, _currentName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase)))
            error = $"A group named '{normalized}' already exists.";

        ErrorBox.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = error ?? "";
        AcceptButton.IsEnabled = normalized is not null && error is null;
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        GroupName = HostsGroups.NormalizeName(NameBox.Text);
        DialogResult = true;
    }
}
