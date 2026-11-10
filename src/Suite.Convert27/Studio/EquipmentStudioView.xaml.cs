using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Suite.Convert27.Studio;

public partial class EquipmentStudioView : UserControl
{
    readonly EquipmentStudioViewModel _vm;
    bool _loaded;

    public EquipmentStudioView(SuiteSettings settings)
    {
        InitializeComponent();
        _vm = new EquipmentStudioViewModel(settings) { Post = a => Dispatcher.InvokeAsync(a) };
        DataContext = _vm;
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            _vm.RefreshMods();
            await _vm.LoadAsync();
        };
    }

    void BrowseBuild_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "NHL 24 build to read and edit (a backup is made before every apply)" };
        if (dialog.ShowDialog() == true) _vm.GameDir = dialog.FolderName;
    }

    async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Discard all unapplied edits and reload both databases?", "Reload equipment", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            await _vm.LoadAsync();
    }

    void Review_Click(object sender, RoutedEventArgs e) => _vm.Review();
    void Duplicate_Click(object sender, RoutedEventArgs e) => _vm.DuplicateSelected();

    async void ResetColors_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Replace the staged equipment colors with the verified numbered NHL 27 home/away colors for this mod?\n\nThis stays in memory until you choose Apply.",
                "Restore NHL 27 colors", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            await _vm.ResetNhl27ColorsAsync();
    }

    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        _vm.Review();
        if (!_vm.Preflight.StartsWith("Validation passed", StringComparison.Ordinal)) return;
        if (MessageBox.Show($"{_vm.Preflight}\n\nTarget build:\n{_vm.GameDir}\n\nApply this equipment edit now?",
                "Apply equipment edit", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _vm.ApplyAsync();
    }
}
