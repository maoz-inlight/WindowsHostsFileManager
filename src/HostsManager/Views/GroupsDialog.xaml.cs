using System.Windows;
using System.Windows.Controls;
using HostsManager.Services;
using HostsManager.ViewModels;

namespace HostsManager.Views;

public partial class GroupsDialog : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IReadOnlyList<EntryViewModel> _selection;

    public GroupsDialog(MainViewModel viewModel, IEnumerable<EntryViewModel> selection)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _selection = selection.Where(entry => entry.CanToggle).Distinct().ToArray();
        SelectionText.Text = _selection.Count switch
        {
            0 => "No editable entries selected. Select rows in the main window to assign them.",
            1 => "1 editable entry selected.",
            _ => $"{_selection.Count} editable entries selected.",
        };
        RefreshGroups();
    }

    private GroupSummary? SelectedGroup => GroupsGrid.SelectedItem as GroupSummary;

    private void RefreshGroups(string? selectName = null)
    {
        selectName ??= SelectedGroup?.Name;
        var groups = _viewModel.GetGroupSummaries();
        GroupsGrid.ItemsSource = groups;
        GroupsGrid.SelectedItem = groups.FirstOrDefault(group =>
            string.Equals(group.Name, selectName, StringComparison.OrdinalIgnoreCase));
        if (GroupsGrid.SelectedItem is null && groups.Count > 0) GroupsGrid.SelectedIndex = 0;
        UpdateButtons();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        if (NewButton is null) return;
        var hasSelection = _selection.Count > 0;
        var hasGroup = SelectedGroup is not null;
        NewButton.IsEnabled = hasSelection;
        AssignButton.IsEnabled = hasSelection && hasGroup;
        RemoveButton.IsEnabled = hasSelection && _selection.Any(entry => entry.GroupName is not null);
        EnableButton.IsEnabled = hasGroup && SelectedGroup!.EnabledCount < SelectedGroup.EntryCount;
        DisableButton.IsEnabled = hasGroup && SelectedGroup!.EnabledCount > 0;
        RenameButton.IsEnabled = hasGroup;
        DeleteButton.IsEnabled = hasGroup;
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        var dialog = CreateNameDialog("New group", "Group name");
        if (dialog.ShowDialog() != true) return;
        Run(() => _viewModel.AssignEntriesToGroup(_selection, dialog.GroupName), dialog.GroupName);
    }

    private void OnAssign(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is not { } group) return;
        Run(() => _viewModel.AssignEntriesToGroup(_selection, group.Name), group.Name);
    }

    private void OnRemove(object sender, RoutedEventArgs e) =>
        Run(() => _viewModel.RemoveEntriesFromGroups(_selection));

    private void OnEnable(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is not { } group) return;
        Run(() => _viewModel.SetGroupEnabled(group.Name, true), group.Name);
    }

    private void OnDisable(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is not { } group) return;
        Run(() => _viewModel.SetGroupEnabled(group.Name, false), group.Name);
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is not { } group) return;
        var dialog = CreateNameDialog("Rename group", "New group name", group.Name);
        if (dialog.ShowDialog() != true) return;
        Run(() => _viewModel.RenameGroup(group.Name, dialog.GroupName), dialog.GroupName);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is not { } group) return;
        var answer = MessageBox.Show(this,
            $"Delete the group '{group.Name}'?\n\nIts {group.CountText} will be kept and become ungrouped.",
            "Delete group", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        Run(() => _viewModel.DeleteGroup(group.Name));
    }

    private GroupNameDialog CreateNameDialog(string title, string prompt, string? currentName = null)
    {
        var dialog = new GroupNameDialog(title, prompt,
            _viewModel.GetGroupSummaries().Select(group => group.Name), currentName) { Owner = this };
        ThemeManager.Track(dialog);
        return dialog;
    }

    private void Run(Action action, string? selectName = null)
    {
        try
        {
            action();
            RefreshGroups(selectName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not change groups",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
