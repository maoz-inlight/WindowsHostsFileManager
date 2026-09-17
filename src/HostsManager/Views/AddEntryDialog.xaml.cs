using System.Windows;
using HostsManager.Core;
using HostsManager.ViewModels;

namespace HostsManager.Views;

public partial class AddEntryDialog : Window
{
    private readonly HostsLine? _entry;

    public AddEntryDialog(HostsLine? entry = null)
    {
        _entry = entry;
        InitializeComponent();
        if (entry is not null)
        {
            Title = "Edit entry";
            AddButton.Content = "Apply";
            DomainBox.Text = string.Join(' ', entry.Hostnames);
            IpBox.Text = entry.Ip;
            CommentBox.Text = entry.InlineComment ?? "";
        }
        Loaded += (_, _) => { DomainBox.Focus(); Validate(); };
    }

    public AddEntryRequest? Result { get; private set; }

    private void OnChanged(object sender, RoutedEventArgs e) => Validate();

    /// <summary>
    /// Validates as the user types and previews the mapping. Editing preserves
    /// existing separators, so the saved whitespace can differ from this preview.
    /// </summary>
    private void Validate()
    {
        if (AddButton is null) return;

        var hostnames = DomainBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var ip = IpBox.Text.Trim();
        var comment = CommentBox.Text.Trim();

        string? error = null;

        if (hostnames.Length == 0)
        {
            error = null; // Nothing typed yet — don't scold before there's input.
            PreviewText.Text = "The line to be added appears here.";
        }
        else
        {
            foreach (var host in hostnames)
            {
                var check = HostsValidator.ValidateHostname(host);
                if (!check.IsValid) { error = check.Error; break; }
            }

            error ??= HostsValidator.ValidateIp(ip).Error;
            error ??= HostsValidator.ValidateComment(comment).Error;

            PreviewText.Text = error is null
                ? (_entry is { IsEnabled: false } ? _entry.DisablePrefix : "") + $"{ip} {string.Join(' ', hostnames)}" + (comment.Length > 0 ? $" # {comment}" : "")
                : "—";
        }

        ErrorBox.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = error ?? "";
        AddButton.IsEnabled = error is null && hostnames.Length > 0;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        Validate();
        if (!AddButton.IsEnabled) return;
        var hostnames = DomainBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var comment = CommentBox.Text.Trim();

        Result = new AddEntryRequest(IpBox.Text.Trim(), hostnames, comment.Length == 0 ? null : comment);
        DialogResult = true;
    }
}
