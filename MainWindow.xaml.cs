using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace StarMap3D;

public partial class MainWindow : Window
{
    bool _ready;
    bool _syncing;
    readonly DispatcherTimer _distanceTimer;

    public MainWindow()
    {
        InitializeComponent();

        SlZoom.Minimum = Math.Log(StarMapView.MinZoom);
        SlZoom.Maximum = Math.Log(StarMapView.MaxZoom);

        SlDistance.Minimum = StarData.MinLy;
        SlDistance.Maximum = StarData.MaxDownloadLy;
        SlDistance.Value = StarData.DefaultLy;
        Map.MaxDisplayLy = StarData.DefaultLy;

        _distanceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _distanceTimer.Tick += (_, _) => { _distanceTimer.Stop(); ApplyDistance(); };

        Map.ViewChanged += (_, _) => SyncControls();
        Map.HoverChanged += (_, _) => UpdateInfo();
        Map.SelectionChanged += (_, _) => UpdateInfo();

        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.FocusedElement is ComboBox or ComboBoxItem) return;
            if (Map.HandleKey(e.Key)) e.Handled = true;
        };

        _ready = true;
        ApplyOptions();
        SyncControls();
        DistanceText.Text = $"{SlDistance.Value:0.#} ly";
        Loaded += async (_, _) => await LoadDataAsync(false);
    }

    // ------------------------------------------------------------------ data

    async System.Threading.Tasks.Task LoadDataAsync(bool refresh, string? localHygPath = null)
    {
        BtnRefresh.IsEnabled = false;
        var progress = new Progress<string>(s => StatusText.Text = s);
        StatusText.Text = "Loading star data...";
        try
        {
            var result = await StarData.LoadAsync(refresh, progress, localHygPath, SlDistance.Value);
            ShowResult(result);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load data: " + ex.Message;
        }
        finally
        {
            BtnRefresh.IsEnabled = true;
        }
    }

    /// <summary>Re-filters the already-downloaded catalogues to the slider's current value. No network access.</summary>
    void ApplyDistance()
    {
        try
        {
            ShowResult(StarData.ApplyDistance(SlDistance.Value));
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to apply the new distance: " + ex.Message;
        }
    }

    void ShowResult(LoadResult result)
    {
        Map.SetStars(result.Stars);
        StatusText.Text = result.Status;

        int hosts = result.Stars.Count(s => s.Planets > 0);
        int planets = result.Stars.Sum(s => s.Planets);
        CountText.Text = $"{result.Stars.Count:N0} stars within {SlDistance.Value:0.#} ly (including the Sun)\n" +
                         $"{hosts:N0} stars with planets, {planets:N0} known planets";
    }

    void Distance_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        DistanceText.Text = $"{SlDistance.Value:0.#} ly";
        Map.MaxDisplayLy = SlDistance.Value;
        _distanceTimer.Stop();
        _distanceTimer.Start();
    }

    void LoadCsv_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select a HYG-format star catalogue (CSV with ra, dec, dist columns)",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == true) _ = LoadDataAsync(false, dlg.FileName);
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadDataAsync(true);

    // ------------------------------------------------------------------ view controls

    void SyncControls()
    {
        if (!_ready) return;
        _syncing = true;
        SlYaw.Value = Map.Yaw;
        SlPitch.Value = Map.Pitch;
        SlRoll.Value = Map.Roll;
        SlZoom.Value = Math.Log(Map.Zoom);
        YawText.Text = $"{Map.Yaw:0}°";
        PitchText.Text = $"{Map.Pitch:0}°";
        RollText.Text = $"{Map.Roll:0}°";
        ZoomText.Text = $"{Map.Zoom:0.00}×";
        var (x, y, z) = Map.Target;
        TargetText.Text = string.Format(CultureInfo.InvariantCulture,
            "Look-at point (ly): x {0:0.0}, y {1:0.0}, z {2:0.0}", x, y, z);
        _syncing = false;
    }

    void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _syncing) return;
        if (sender == SlYaw) Map.Yaw = SlYaw.Value;
        else if (sender == SlPitch) Map.Pitch = SlPitch.Value;
        else if (sender == SlRoll) Map.Roll = SlRoll.Value;
        else if (sender == SlZoom) Map.Zoom = Math.Exp(SlZoom.Value);
    }

    void ResetView_Click(object sender, RoutedEventArgs e) => Map.ResetView();
    void CenterSun_Click(object sender, RoutedEventArgs e) => Map.CenterOnSun();
    void TopView_Click(object sender, RoutedEventArgs e) => Map.SetOrientation(0, 90, 0);
    void SideView_Click(object sender, RoutedEventArgs e) => Map.SetOrientation(0, 0, 0);

    void Options_Changed(object sender, RoutedEventArgs e) => ApplyOptions();

    void ApplyOptions()
    {
        if (!_ready) return;
        Map.ShowGrid = ChkGrid.IsChecked == true;
        Map.ShowAxes = ChkAxes.IsChecked == true;
        Map.DropLines = ChkDrop.IsChecked == true;
        Map.HostsOnly = ChkHosts.IsChecked == true;
        Map.Labels = CmbLabels.SelectedIndex switch
        {
            0 => LabelMode.Off,
            2 => LabelMode.AllNamed,
            _ => LabelMode.PlanetHosts,
        };
    }

    // ------------------------------------------------------------------ info panel

    void UpdateInfo()
    {
        var s = Map.Hovered ?? Map.Selected;
        if (s == null)
        {
            InfoText.Text = "Hover over a star, or click one to select it. Double-click a star to centre the view on it.";
            return;
        }

        var inv = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.IsNullOrEmpty(s.Name) ? "(unnamed star)" : s.Name);
        if (!string.IsNullOrEmpty(s.Designations)) sb.AppendLine(s.Designations);
        sb.AppendLine(s.DistLy < 0.001 ? "Distance: 0 (home)" : string.Format(inv, "Distance: {0:0.00} ly ({1:0.00} pc)", s.DistLy, s.DistPc));
        if (!string.IsNullOrEmpty(s.Spectral)) sb.AppendLine("Spectral type: " + s.Spectral);
        else sb.AppendLine("Colour class: " + Star.ClassNames[s.ColorClass]);
        if (!double.IsNaN(s.Mag)) sb.AppendLine(string.Format(inv, "Apparent mag: {0:0.0}", s.Mag));
        if (!double.IsNaN(s.AbsMag)) sb.AppendLine(string.Format(inv, "Absolute mag: {0:0.0}", s.AbsMag));
        sb.AppendLine(string.Format(inv, "Position (ly): {0:0.0}, {1:0.0}, {2:0.0}", s.X, s.Y, s.Z));
        if (s.Planets > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{s.Planets} known planet{(s.Planets == 1 ? "" : "s")}:");
            sb.Append(string.Join(", ", s.PlanetNames));
        }
        InfoText.Text = sb.ToString().TrimEnd();
    }
}
