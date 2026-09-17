using System.Windows;
using System.Windows.Controls;
using HostsManager.Core;
using HostsManager.Services;

namespace HostsManager.Views;

public partial class BrowserProfilesDialog : Window
{
    public BrowserProfilesDialog()
    {
        InitializeComponent();
        RootBox.Text = BrowserPreviewService.Profiles.Root;
        RefreshProfiles();
    }

    private void RefreshProfiles()
    {
        try { ProfilesList.ItemsSource = BrowserPreviewService.Profiles.List(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshProfiles();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeleteButton is null || SelectedPath is null) return;
        var selected = ProfilesList.SelectedItem as BrowserPreviewProfile;
        DeleteButton.IsEnabled = selected is not null;
        SelectedPath.Text = selected?.Path ?? "";
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not BrowserPreviewProfile profile) return;
        if (MessageBox.Show(this,
                $"Permanently delete cookies, sign-ins, history and cache in this preview profile?\n\n{profile.Path}\n\nThis cannot be undone.",
                "Delete preview data", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try { BrowserPreviewService.DeleteProfile(profile); }
        catch (Exception ex) { ShowError(ex); }
        RefreshProfiles();
    }

    private void ShowError(Exception ex) => MessageBox.Show(this, ex.Message,
        "Preview data", MessageBoxButton.OK, MessageBoxImage.Warning);
}
