using System.Windows;
using HostsManager.Core;
using HostsManager.ViewModels;

namespace HostsManager.Views;

public partial class AddEntryDialog : Window
{
    public AddEntryDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => { DomainBox.Focus(); Validate(); };
    }

    public AddEntryRequest? Result { get; private set; }

    private void OnChanged(object sender, RoutedEventArgs e) => Validate();

    /// <summary>
    /// Validates as the user types and previews the exact line that will be written,
    /// so nothing lands in the file that hasn't already been shown and checked.
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
                ? $"{ip} {string.Join(' ', hostnames)}" + (comment.Length > 0 ? $" # {comment}" : "")
                : "—";
        }

        ErrorBox.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = error ?? "";
        AddButton.IsEnabled = error is null && hostnames.Length > 0;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var hostnames = DomainBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var comment = CommentBox.Text.Trim();

        Result = new AddEntryRequest(IpBox.Text.Trim(), hostnames, comment.Length == 0 ? null : comment);
        DialogResult = true;
    }
}
