using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wispclip.Models;
using Wispclip.Services;

namespace Wispclip.Views;

public partial class LibraryView : UserControl
{
    public event Action<ClipInfo>? PlayRequested;
    private ClipViewModel? _selected;
    private List<ClipViewModel> _allClips = new();
    private ICollectionView? _view;
    private string? _kindFilter;

    public LibraryView()
    {
        InitializeComponent();
    }

    public void Refresh()
    {
        List<ClipInfo> clips;
        try { clips = App.Library.Scan(); }
        catch (Exception ex)
        {
            Log.Write("library", $"scan failed: {ex.Message}");
            return;
        }

        string? selectedPath = _selected?.Clip.Path;
        _allClips = clips.Select(c => new ClipViewModel(c)).ToList();
        _selected = _allClips.FirstOrDefault(vm =>
            string.Equals(vm.Clip.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
        if (_selected != null) _selected.IsSelected = true;

        _view = CollectionViewSource.GetDefaultView(_allClips);
        ClipsList.ItemsSource = _view;
        LibraryStatus.Text = "";
        UpdateFilterCounts();
        ApplyFilterAndSort();
        ApplyCardWidth(CardsScroll.ViewportWidth);

        foreach (var vm in _allClips)
            _ = PopulateAsync(vm);
    }

    // ------------------------------------------------------------------ responsive card grid

    private const double CardDesiredWidth = 280;
    private const double CardMinWidth = 220;
    private const double CardMaxWidth = 340;
    private const double CardGutter = 16;

    /// <summary>
    /// Picks a column count close to <see cref="CardDesiredWidth"/> for the current viewport,
    /// then stretches cards to exactly fill it — so a row never falls one column short and
    /// leaves a whole extra column's worth of space (and the scrollbar) stranded to the right.
    /// </summary>
    private void CardsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange == 0) return;
        ApplyCardWidth(((ScrollViewer)sender).ViewportWidth);
    }

    private void ApplyCardWidth(double viewportWidth)
    {
        if (viewportWidth <= 0) return;
        int columns = Math.Max(1, (int)Math.Round(viewportWidth / (CardDesiredWidth + CardGutter)));
        double width = Math.Clamp(viewportWidth / columns - CardGutter, CardMinWidth, CardMaxWidth);
        ClipsList.Tag = width;
    }

    // ------------------------------------------------------------------ filtering & sorting

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilterAndSort();

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilterAndSort();

    private void KindFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, FilterReplay)) _kindFilter = "Replay";
        else if (ReferenceEquals(sender, FilterRecording)) _kindFilter = "Recording";
        else if (ReferenceEquals(sender, FilterEdited)) _kindFilter = "Edited";
        else _kindFilter = null; // "All"
        ApplyFilterAndSort();
    }

    private void ApplyFilterAndSort()
    {
        if (_view == null) return; // fires from XAML during InitializeComponent, before Refresh() ever ran

        _view.Filter = FilterClip;
        _view.SortDescriptions.Clear();
        _view.SortDescriptions.Add(SortCombo.SelectedIndex switch
        {
            1 => new SortDescription(nameof(ClipViewModel.CreatedAt), ListSortDirection.Ascending),
            2 => new SortDescription(nameof(ClipViewModel.SizeBytes), ListSortDirection.Descending),
            3 => new SortDescription(nameof(ClipViewModel.Name), ListSortDirection.Ascending),
            _ => new SortDescription(nameof(ClipViewModel.CreatedAt), ListSortDirection.Descending),
        });
        _view.Refresh();
        UpdateResultsText();
    }

    private bool FilterClip(object obj)
    {
        if (obj is not ClipViewModel vm) return false;
        if (_kindFilter != null && !string.Equals(vm.Kind, _kindFilter, StringComparison.OrdinalIgnoreCase))
            return false;
        var query = SearchBox.Text;
        if (!string.IsNullOrWhiteSpace(query) && vm.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        return true;
    }

    private void UpdateFilterCounts()
    {
        FilterAll.Tag = _allClips.Count.ToString();
        FilterReplay.Tag = _allClips.Count(c => c.Kind == "Replay").ToString();
        FilterRecording.Tag = _allClips.Count(c => c.Kind == "Recording").ToString();
        FilterEdited.Tag = _allClips.Count(c => c.Kind == "Edited").ToString();
    }

    private void UpdateResultsText()
    {
        int total = _allClips.Count;
        int shown = _view?.Cast<object>().Count() ?? total;

        CountText.Text = total == 0
            ? "Replays and recordings will appear here."
            : shown == total
                ? $"{total} clip{(total == 1 ? "" : "s")}  ·  Click to edit, right-click for more"
                : $"{shown} of {total} clip{(total == 1 ? "" : "s")}  ·  Click to edit, right-click for more";

        bool noMatches = total > 0 && shown == 0;
        EmptyState.Visibility = total == 0 || noMatches ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = noMatches ? "No matches" : "No clips yet";
        EmptyHint.Text = noMatches
            ? "Nothing matches the current search or filter."
            : $"Press {App.Settings.Current.Hotkeys.SaveReplay} while the replay buffer is armed\n" +
              $"or {App.Settings.Current.Hotkeys.ToggleRecording} to start a recording.";
    }

    private async Task PopulateAsync(ClipViewModel vm)
    {
        try
        {
            var thumb = vm.Clip.ThumbnailPath ?? await App.Library.EnsureThumbnailAsync(vm.Clip);
            if (thumb != null)
            {
                vm.Clip.ThumbnailPath = thumb;
                vm.Thumb = await Task.Run(() => LoadImage(thumb));
            }
            if (vm.Clip.DurationSeconds == null)
            {
                vm.Clip.DurationSeconds = await App.Library.GetDurationAsync(vm.Clip.Path);
                vm.RefreshTexts();
            }
        }
        catch (Exception ex)
        {
            Log.Write("library", $"populate failed for {vm.Clip.Name}: {ex.Message}");
        }
    }

    private static ImageSource LoadImage(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.UriSource = new Uri(path);
        bmp.DecodePixelWidth = 480;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    // ------------------------------------------------------------------ interactions

    private static ClipViewModel? VmFrom(object sender) => sender switch
    {
        FrameworkElement fe => fe.DataContext as ClipViewModel,
        _ => null,
    };

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (VmFrom(sender) is { } vm)
        {
            OpenEditor(vm);
            e.Handled = true;
        }
    }

    private void Menu_Play(object sender, RoutedEventArgs e)
    {
        if (VmFrom(sender) is { } vm)
            OpenEditor(vm);
    }

    /// <summary>Opens the card's context menu anchored to the overflow button, so a single
    /// affordance replaces what used to be three separate inline action buttons.</summary>
    private void Overflow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { } fe && VmFrom(fe) is { } vm)
        {
            SelectClip(vm);
            // The thumbnail container is also a Border, so we can't stop at the first
            // Border ancestor — walk up until we find the one that actually owns the menu.
            if (FindCardContextMenu(fe) is { } menu)
            {
                menu.PlacementTarget = fe;
                menu.IsOpen = true;
            }
        }
        e.Handled = true;
    }

    private static ContextMenu? FindCardContextMenu(DependencyObject node)
    {
        while (node != null)
        {
            if (node is FrameworkElement { ContextMenu: { } menu }) return menu;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private void SelectClip(ClipViewModel vm)
    {
        if (ReferenceEquals(_selected, vm)) return;
        if (_selected != null) _selected.IsSelected = false;
        _selected = vm;
        vm.IsSelected = true;
        LibraryStatus.Text = $"{vm.Kind} selected";
    }

    private void OpenEditor(ClipViewModel vm)
    {
        SelectClip(vm);
        Log.Write("library", $"open requested: {vm.Clip.Path}");
        PlayRequested?.Invoke(vm.Clip);
    }

    private void Menu_Reveal(object sender, RoutedEventArgs e)
    {
        if (VmFrom(sender) is { } vm)
            Process.Start("explorer.exe", $"/select,\"{vm.Clip.Path}\"");
    }

    private void Menu_CopyPath(object sender, RoutedEventArgs e)
    {
        if (VmFrom(sender) is not { } vm) return;
        Clipboard.SetText(vm.Clip.Path);
        LibraryStatus.Text = "File path copied";
    }

    private void Menu_Rename(object sender, RoutedEventArgs e)
    {
        if (VmFrom(sender) is { } vm) Rename(vm);
    }

    private void Rename(ClipViewModel vm)
    {
        var dialog = new RenameWindow(vm.Clip.Name) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Result))
        {
            try
            {
                App.Library.Rename(vm.Clip, dialog.Result.Trim());
                Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Rename failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void Menu_Delete(object sender, RoutedEventArgs e)
    {
        if (VmFrom(sender) is { } vm) Delete(vm);
    }

    private void Delete(ClipViewModel vm)
    {
        var answer = MessageBox.Show($"Delete \"{vm.Clip.Name}\" permanently?", "Delete clip",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            App.Library.Delete(vm.Clip);
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not delete the clip: {ex.Message}\n(Close the player if it's open.)",
                "Delete failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = App.Settings.Current.OutputDirectory;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
}

public class ClipViewModel : INotifyPropertyChanged
{
    public ClipInfo Clip { get; }

    public ClipViewModel(ClipInfo clip) => Clip = clip;

    public string Name => Clip.Name;
    public DateTime CreatedAt => Clip.CreatedAt;
    public long SizeBytes => Clip.SizeBytes;
    public string DurationText => Clip.DurationSeconds is { } d ? Ui.FormatDuration(d) : "…";
    public string Meta => $"{Ui.FormatSize(Clip.SizeBytes)} · {Clip.CreatedAt:MMM d, HH:mm}";
    public string Kind
    {
        get
        {
            string name = Clip.Name;
            if (EditProjectStore.HasSidecar(Clip.Path) ||
                name.Contains("[edit]", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("[cut]", StringComparison.OrdinalIgnoreCase))
                return "Edited";
            if (name.StartsWith("Replay", StringComparison.OrdinalIgnoreCase))
                return "Replay";
            if (name.StartsWith("Recording", StringComparison.OrdinalIgnoreCase))
                return "Recording";
            return "Clip";
        }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; Notify(nameof(IsSelected)); }
    }

    private ImageSource? _thumb;
    public ImageSource? Thumb
    {
        get => _thumb;
        set { _thumb = value; Notify(nameof(Thumb)); }
    }

    public void RefreshTexts()
    {
        Notify(nameof(DurationText));
        Notify(nameof(Meta));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
