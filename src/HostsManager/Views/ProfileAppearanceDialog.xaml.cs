using System.Windows;
using HostsManager.Core;
using HostsManager.Services;

namespace HostsManager.Views;

public partial class ProfileAppearanceDialog : Window
{
    private readonly BrowserPreviewProfile _profile;
    public ProfileAppearanceDialog(BrowserPreviewProfile profile)
    {
        InitializeComponent();
        _profile = profile;
        ProfileLabel.Text = profile.DisplayName;
        Picker.LoadAppearance(profile.Metadata);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            BrowserPreviewService.SaveAppearance(_profile, Picker.Appearance);
            DialogResult = true;
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
    }
}
