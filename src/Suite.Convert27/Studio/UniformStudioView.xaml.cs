using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Suite.Convert27.Studio;

/// <summary>Uniform Studio page: card grid of ORIGINAL vs NEW textures for one uniform. Everything stays in the main window.</summary>
public partial class UniformStudioView : UserControl
{
    readonly UniformStudioViewModel _vm;
    StudioCard? _choosing;

    public UniformStudioView(SuiteSettings settings)
    {
        InitializeComponent();
        _vm = new UniformStudioViewModel(settings) { Post = a => Dispatcher.InvokeAsync(a) };
        DataContext = _vm;
        Loaded += async (_, _) => { if (_vm.Teams.Count == 0) await _vm.LoadAsync(); };
        Zoom.ValueChanged += (_, _) => ApplyZoom();
    }

    static StudioCard? CardOf(object sender) => (sender as FrameworkElement)?.DataContext as StudioCard;

    void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "NHL 24 game folder (NHL24PC build or extracted CUSA37933)" };
        if (d.ShowDialog() == true) _vm.GameDir = d.FolderName;
    }

    void BrowseReplacements_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Folder with replacement textures (DDS / PNG)" };
        if (d.ShowDialog() == true) _vm.ReplacementDir = d.FolderName;
    }

    async void AutoMatch_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Re-match every card, including ones you set by hand?", "Auto-match", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _vm.ResetAuto();
        await Task.CompletedTask;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        try { _vm.Save(); }
        catch (Exception ex) { _vm.Status = "Not saved: " + ex.Message; }
    }

    void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } c) { c.Manual = true; _vm.Refresh(); }
    }

    void Choose_Click(object sender, RoutedEventArgs e) { if (CardOf(sender) is { } c) OpenChooser(c); }
    void New_Click(object sender, MouseButtonEventArgs e) { if (CardOf(sender) is { } c) OpenChooser(c); }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } c) return;
        var d = new OpenFileDialog { Filter = "Textures (*.dds;*.png)|*.dds;*.png|All images|*.dds;*.png;*.jpg;*.tga", InitialDirectory = Directory.Exists(_vm.ReplacementDir) ? _vm.ReplacementDir : "" };
        if (d.ShowDialog() == true) _vm.Browse(c, d.FileName);
    }

    void Card_Drop(object sender, DragEventArgs e)
    {
        if (CardOf(sender) is { } c && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.FirstOrDefault(ImageIO.CanLoad) is { } f)
            _vm.Browse(c, f);
    }

    void Original_Click(object sender, MouseButtonEventArgs e)
    {
        if (CardOf(sender) is { OriginalPng: { } png } c) Inspect($"ORIGINAL · {c.Leaf} · {c.Details}", png);
    }

    void OpenChooser(StudioCard card)
    {
        _choosing = card;
        ChooseTitle.Text = $"Replacement for {card.Leaf}";
        ChoiceList.ItemsSource = _vm.CandidatesFor(card);
        Show(ChoosePanel);
    }

    void Choice_Pick(object sender, MouseButtonEventArgs e) => PickSelected();
    void Choice_Key(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) PickSelected(); else if (e.Key == Key.Escape) Hide(); }

    void PickSelected()
    {
        if (_choosing is null || ChoiceList.SelectedItem is not Candidate c) return;
        _vm.Choose(_choosing, c.File, c.Score > 0 ? c.Score : 1, c.Score > 0 ? $"chosen by hand ({c.Reason})" : "chosen by hand");
        Hide();
    }

    void KeepStock_Click(object sender, RoutedEventArgs e)
    {
        if (_choosing is not null) _vm.Choose(_choosing, null);
        Hide();
    }

    void Inspect(string title, string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            InspectImage.Source = bmp;
            InspectTitle.Text = $"{title} · {bmp.PixelWidth}×{bmp.PixelHeight}";
            Zoom.Value = 1;
            Show(InspectPanel);
            ApplyZoom();
        }
        catch (Exception ex) { _vm.Status = "Cannot open image: " + ex.Message; }
    }

    // Zoom 1 = fit the panel (aspect ratio kept); higher values show detail with scrolling.
    void ApplyZoom()
    {
        if (InspectImage.Source is not BitmapSource b || InspectScroll.ActualWidth <= 0) return;
        var fit = Math.Min(InspectScroll.ActualWidth / b.PixelWidth, InspectScroll.ActualHeight / b.PixelHeight);
        InspectImage.Width = b.PixelWidth * fit * Zoom.Value;
        InspectImage.Height = b.PixelHeight * fit * Zoom.Value;
    }

    void Show(FrameworkElement panel)
    {
        ChoosePanel.Visibility = panel == ChoosePanel ? Visibility.Visible : Visibility.Collapsed;
        InspectPanel.Visibility = panel == InspectPanel ? Visibility.Visible : Visibility.Collapsed;
        Overlay.Visibility = Visibility.Visible;
        if (panel == InspectPanel) Dispatcher.InvokeAsync(ApplyZoom, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void Hide() { Overlay.Visibility = Visibility.Collapsed; _choosing = null; }
    void CloseOverlay_Click(object sender, RoutedEventArgs e) => Hide();
    void Overlay_Click(object sender, MouseButtonEventArgs e) { if (e.OriginalSource == Overlay) Hide(); }
    void Panel_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
