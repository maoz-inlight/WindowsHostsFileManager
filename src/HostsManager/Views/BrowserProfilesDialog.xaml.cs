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

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeleteButton is null || SelectedPath is null) return;
        var selected = ProfilesList.SelectedItem as BrowserPreviewProfile;
        DeleteButton.IsEnabled = selected is not null;
        AppearanceButton.IsEnabled = selected is not null;
        CloseBackgroundButton.IsEnabled = selected is not null;
        SelectedPath.Text = selected is null ? "Select a profile to inspect its mappings, options, dates and disk usage." : "Reading profile details…";
        if (selected is null) return;
        string details;
        try { details = await Task.Run(() => BrowserPreviewService.Profiles.Describe(selected)); }
        catch (Exception ex) { details = $"Could not read profile details: {ex.Message}\n{selected.Path}"; }
        if (Equals(ProfilesList.SelectedItem, selected)) SelectedPath.Text = details;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not BrowserPreviewProfile profile) return;
        if (MessageBox.Show(this,
                $"Permanently delete cookies, sign-ins, history and cache in this preview profile?\n\n{profile.DisplayName}\n{profile.Path}\n\nThis cannot be undone.",
                "Delete preview data", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try { BrowserPreviewService.DeleteProfile(profile); }
        catch (Exception ex) { ShowError(ex); }
        RefreshProfiles();
    }

    private void OnAppearance(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not BrowserPreviewProfile profile) return;
        var dialog = new ProfileAppearanceDialog(profile) { Owner = this };
        ThemeManager.Track(dialog);
        if (dialog.ShowDialog() != true) return;
        RefreshProfiles();
        ProfilesList.SelectedItem = ProfilesList.Items.Cast<BrowserPreviewProfile>().FirstOrDefault(p => p.Path == profile.Path);
    }

    private async void OnCloseBackground(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not BrowserPreviewProfile profile) return;
        if (MessageBox.Show(this,
            $"Stop background browser processes for this preview profile? Background work in this profile will end. Open preview windows must be closed first.\n\n{profile.DisplayName}\n{profile.Path}",
            "Close background preview", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        CloseBackgroundButton.IsEnabled = false;
        try
        {
            await Task.Run(() => BrowserPreviewService.CloseBackgroundPreview(profile));
            MessageBox.Show(this, "This profile has no remaining background browser process. You can now change its color or delete its data.",
                "Preview stopped", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { CloseBackgroundButton.IsEnabled = ProfilesList.SelectedItem is not null; }
    }

    private void ShowError(Exception ex) => MessageBox.Show(this, ex.Message,
        "Preview data", MessageBoxButton.OK, MessageBoxImage.Warning);
}
