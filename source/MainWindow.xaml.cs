using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Wsync.Services;

namespace Wsync;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly ConfigService _configService;
    private FtpService? _ftpService;
    private DispatcherTimer? _spinnerTimer;
    private int _spinnerIndex = 0;
    private readonly string[] _spinnerFrames = { "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
    private DateTime _lastCheckTime = DateTime.MinValue;
    private CancellationTokenSource? _analysisCancellationToken;
    private bool _pendingSyncToFtp = false;  // Track which sync action is pending confirmation

    public MainWindow()
    {
        InitializeComponent();
        _configService = new ConfigService();
        InitializeSpinner();
        RecommendationText.Text = "Select a project and click Refresh to compare files.";
        LoadProjects();
    }

    private void InitializeSpinner()
    {
        _spinnerTimer = new DispatcherTimer();
        _spinnerTimer.Interval = TimeSpan.FromMilliseconds(80);
        _spinnerTimer.Tick += (s, e) =>
        {
            SpinnerText.Text = _spinnerFrames[_spinnerIndex % _spinnerFrames.Length];
            _spinnerIndex++;
        };
    }

    private void LoadProjects()
    {
        // Remember currently selected project
        string previouslySelectedProject = null;
        if (ProjectComboBox.SelectedIndex >= 0 && ProjectComboBox.SelectedIndex < ProjectComboBox.Items.Count)
        {
            var selectedItem = ProjectComboBox.Items[ProjectComboBox.SelectedIndex];
            if (selectedItem is string && selectedItem.ToString() != "(No projects configured)")
            {
                previouslySelectedProject = selectedItem.ToString();
            }
        }
        
        // Clear existing items
        ProjectComboBox.Items.Clear();
        ProjectComboBox.SelectionChanged -= ProjectComboBox_SelectionChanged;
        
        var projects = _configService.GetProjects();
        System.Diagnostics.Debug.WriteLine($"[MainWindow] LoadProjects called, found {projects.Count} projects");
        
        if (projects.Count == 0)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] No projects, disabling combo box");
            ProjectComboBox.Items.Add("(No projects configured)");
            ProjectComboBox.SelectedIndex = 0;
            ProjectComboBox.IsEnabled = false;
            UpdateStatus("No projects configured", "", "");
        }
        else
        {
            foreach (var project in projects)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] Adding project: {project.Name}");
                ProjectComboBox.Items.Add(project.Name);
            }
            
            // Restore previously selected project, or select first one
            if (!string.IsNullOrEmpty(previouslySelectedProject) && ProjectComboBox.Items.Contains(previouslySelectedProject))
            {
                ProjectComboBox.SelectedItem = previouslySelectedProject;
            }
            else
            {
                ProjectComboBox.SelectedIndex = 0;
            }
            
            ProjectComboBox.IsEnabled = true;
            ProjectComboBox.SelectionChanged += ProjectComboBox_SelectionChanged;
            
            UpdateStatus("Ready. Select a project to analyze.", "", "");
        }
    }

    private async void ProjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Update the path displays for the selected project
        UpdatePathDisplays();
        
        // Automatically analyze when project selection changes
        await AnalyzeSyncStatusAsync();
    }

    private void UpdatePathDisplays()
    {
        if (ProjectComboBox.SelectedIndex < 0 || ProjectComboBox.SelectedIndex >= _configService.GetProjects().Count)
        {
            LocalPathText.Text = "";
            RemotePathText.Text = "";
            return;
        }

        var selectedProject = _configService.GetProjects()[ProjectComboBox.SelectedIndex];
        LocalPathText.Text = selectedProject.LocalPath;
        RemotePathText.Text = selectedProject.FtpRemotePath;
    }

    private async Task AnalyzeSyncStatusAsync()
    {
        if (ProjectComboBox.SelectedIndex < 0 || ProjectComboBox.SelectedIndex >= _configService.GetProjects().Count)
            return;

        // Cancel any previous analysis
        _analysisCancellationToken?.Cancel();
        _analysisCancellationToken = new CancellationTokenSource();

        var selectedProject = _configService.GetProjects()[ProjectComboBox.SelectedIndex];
        var ftpConfig = _configService.GetConfig().Ftp;
        var fastMode = FastModeCheckBox.IsChecked ?? false;

        _ftpService = new FtpService(ftpConfig, selectedProject.LocalPath, selectedProject.FtpRemotePath, fastMode);
        _ftpService.SetStatusCallback(UpdateAnalysisStatus);

        try
        {
            // Show loading state
            _spinnerIndex = 0;
            _spinnerTimer?.Start();
            LoadingSpinner.Visibility = Visibility.Visible;
            AbortAnalysisButton.Visibility = Visibility.Visible;
            UpdateStatus("Analyzing files...", "", "");
            
            SyncLeftButton.Background = new SolidColorBrush(Color.FromRgb(200, 200, 200));
            SyncRightButton.Background = new SolidColorBrush(Color.FromRgb(200, 200, 200));
            SyncLeftButton.IsEnabled = false;
            SyncRightButton.IsEnabled = false;
            ProjectComboBox.IsEnabled = false;

            // Run analysis on thread pool to avoid blocking UI
            var recommendation = await Task.Run(async () => await _ftpService.GetSyncRecommendationAsync(_analysisCancellationToken.Token));

            _spinnerTimer?.Stop();
            LoadingSpinner.Visibility = Visibility.Collapsed;
            AbortAnalysisButton.Visibility = Visibility.Collapsed;
            _lastCheckTime = DateTime.Now;

            // Show the checked timestamp immediately after analysis completes
            // (Now handled in UpdateRecommendationText)
            
            // Update button colors and status based on recommendation
            UpdateButtonColors(recommendation);
            UpdateRecommendationText(recommendation);

            SyncLeftButton.IsEnabled = true;
            SyncRightButton.IsEnabled = true;
            ProjectComboBox.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Analysis was canceled");
            _spinnerTimer?.Stop();
            LoadingSpinner.Visibility = Visibility.Collapsed;
            AbortAnalysisButton.Visibility = Visibility.Collapsed;
            UpdateStatus("Analysis canceled", "", "");
            SyncLeftButton.IsEnabled = true;
            SyncRightButton.IsEnabled = true;
            ProjectComboBox.IsEnabled = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Error analyzing sync status: {ex.Message}");
            _spinnerTimer?.Stop();
            LoadingSpinner.Visibility = Visibility.Collapsed;
            AbortAnalysisButton.Visibility = Visibility.Collapsed;
            UpdateStatus($"Error: {ex.Message}", "", "");
            ResetButtonColors();
            SyncLeftButton.IsEnabled = true;
            SyncRightButton.IsEnabled = true;
            ProjectComboBox.IsEnabled = true;
        }
    }

    private void UpdateAnalysisStatus(string message)
    {
        Dispatcher.Invoke(() =>
        {
            // Append new message to existing status, scrolling to bottom
            if (RecommendationText.Text.Length > 0)
            {
                RecommendationText.Text += Environment.NewLine + message;
            }
            else
            {
                RecommendationText.Text = message;
            }
            
            // Auto-scroll to bottom to show latest message
            RecommendationText.CaretIndex = RecommendationText.Text.Length;
            RecommendationText.ScrollToEnd();
        });
    }

    private void UpdateStatus(string message, string recommendation, string advice)
    {
        Dispatcher.Invoke(() =>
        {
            RecommendationText.Text = message;
            if (!string.IsNullOrEmpty(advice))
            {
                RecommendationText.Text += $"\n\n{advice}";
            }
        });
    }

    private void UpdateRecommendationText(SyncRecommendation recommendation)
    {
        // Clear the live logs now that analysis is complete
        var checkTime = _lastCheckTime.ToString("yyyy-MM-dd HH:mm:ss");
        var result = _ftpService?.LastComparisonResult;
        
        // If there's an error, show it prominently
        if (!string.IsNullOrEmpty(result?.Error))
        {
            RecommendationText.Text = $"❌ {result.Error}";
            return;
        }

        var fileListText = BuildFileListText(result);
        var header = $"Checked on {checkTime}\n\n";
        
        switch (recommendation)
        {
            case SyncRecommendation.SyncToLocal:
                RecommendationText.Text = $"{header}✓ RECOMMENDATION: Sync to Local (FTP → Desktop)\n\n{fileListText}";
                break;

            case SyncRecommendation.SyncToFtp:
                RecommendationText.Text = $"{header}✓ RECOMMENDATION: Sync to FTP (Desktop → FTP)\n\n{fileListText}";
                break;

            case SyncRecommendation.BothNewer:
                RecommendationText.Text = $"{header}⚠ CONFLICT: Files on both sides have updates\n\n{fileListText}";
                break;

            case SyncRecommendation.InSync:
                RecommendationText.Text = $"{header}✓ All files are synchronized\n\nNo sync needed.";
                break;

            case SyncRecommendation.Unknown:
                RecommendationText.Text = $"{header}⚠ Could not determine sync direction\n\nPlease check your configuration.";
                break;
        }
    }

    private string BuildFileListText(ComparisonResult? result)
    {
        if (result == null) return "";

        var sb = new System.Text.StringBuilder();

        // FILES NEWER LOCALLY
        sb.AppendLine($"FILES NEWER LOCALLY ({result.NewerLocalFiles.Count}):");
        if (result.NewerLocalFiles.Count > 0)
        {
            foreach (var file in result.NewerLocalFiles.Take(10))
            {
                sb.AppendLine($"  • {file}");
            }
            if (result.NewerLocalFiles.Count > 10)
                sb.AppendLine($"  ... and {result.NewerLocalFiles.Count - 10} more");
        }
        else
        {
            sb.AppendLine("  (No files)");
        }
        sb.AppendLine();

        // FILES NEWER ON FTP
        sb.AppendLine($"FILES NEWER ON FTP ({result.NewerRemoteFiles.Count}):");
        if (result.NewerRemoteFiles.Count > 0)
        {
            foreach (var file in result.NewerRemoteFiles.Take(10))
            {
                sb.AppendLine($"  • {file}");
            }
            if (result.NewerRemoteFiles.Count > 10)
                sb.AppendLine($"  ... and {result.NewerRemoteFiles.Count - 10} more");
        }
        else
        {
            sb.AppendLine("  (No files)");
        }
        sb.AppendLine();

        // FILES ONLY PRESENT LOCALLY
        sb.AppendLine($"FILES ONLY PRESENT LOCALLY ({result.LocalOnlyFiles.Count}):");
        if (result.LocalOnlyFiles.Count > 0)
        {
            foreach (var file in result.LocalOnlyFiles.Take(10))
            {
                sb.AppendLine($"  • {file}");
            }
            if (result.LocalOnlyFiles.Count > 10)
                sb.AppendLine($"  ... and {result.LocalOnlyFiles.Count - 10} more");
        }
        else
        {
            sb.AppendLine("  (No files)");
        }
        sb.AppendLine();

        // FILES ONLY PRESENT ON FTP
        sb.AppendLine($"FILES ONLY PRESENT ON FTP ({result.RemoteOnlyFiles.Count}):");
        if (result.RemoteOnlyFiles.Count > 0)
        {
            foreach (var file in result.RemoteOnlyFiles.Take(10))
            {
                sb.AppendLine($"  • {file}");
            }
            if (result.RemoteOnlyFiles.Count > 10)
                sb.AppendLine($"  ... and {result.RemoteOnlyFiles.Count - 10} more");
        }
        else
        {
            sb.AppendLine("  (No files)");
        }

        return sb.ToString();
    }

    private void UpdateButtonColors(SyncRecommendation recommendation)
    {
        var white = new SolidColorBrush(Color.FromRgb(255, 255, 255));
        var green = new SolidColorBrush(Color.FromRgb(76, 175, 80));
        var red = new SolidColorBrush(Color.FromRgb(244, 67, 54));
        var lightGray = new SolidColorBrush(Color.FromRgb(200, 200, 200));

        switch (recommendation)
        {
            case SyncRecommendation.SyncToLocal:
                // Remote files are newer - recommend left arrow (green), right arrow (red)
                SyncLeftButton.Background = green;
                SyncLeftButton.Foreground = new SolidColorBrush(Colors.White);
                SyncRightButton.Background = red;
                SyncRightButton.Foreground = new SolidColorBrush(Colors.White);
                SyncLeftButton.ToolTip = "✓ Files from FTP are newer - RECOMMENDED";
                SyncRightButton.ToolTip = "⚠ Files on FTP are older";
                break;

            case SyncRecommendation.SyncToFtp:
                // Local files are newer - recommend right arrow (green), left arrow (red)
                SyncLeftButton.Background = red;
                SyncLeftButton.Foreground = new SolidColorBrush(Colors.White);
                SyncRightButton.Background = green;
                SyncRightButton.Foreground = new SolidColorBrush(Colors.White);
                SyncLeftButton.ToolTip = "⚠ Files on FTP are newer";
                SyncRightButton.ToolTip = "✓ Files locally are newer - RECOMMENDED";
                break;

            case SyncRecommendation.BothNewer:
                // Both have newer files - show both as yellow/orange
                var orange = new SolidColorBrush(Color.FromRgb(255, 152, 0));
                SyncLeftButton.Background = orange;
                SyncLeftButton.Foreground = new SolidColorBrush(Colors.White);
                SyncRightButton.Background = orange;
                SyncRightButton.Foreground = new SolidColorBrush(Colors.White);
                SyncLeftButton.ToolTip = "⚠ Conflict: files on both sides have updates";
                SyncRightButton.ToolTip = "⚠ Conflict: files on both sides have updates";
                break;

            case SyncRecommendation.InSync:
                // Files are in sync - both white
                SyncLeftButton.Background = white;
                SyncLeftButton.Foreground = new SolidColorBrush(Color.FromRgb(51, 51, 51));
                SyncRightButton.Background = white;
                SyncRightButton.Foreground = new SolidColorBrush(Color.FromRgb(51, 51, 51));
                SyncLeftButton.ToolTip = "✓ Files are in sync";
                SyncRightButton.ToolTip = "✓ Files are in sync";
                break;

            case SyncRecommendation.Unknown:
            default:
                ResetButtonColors();
                break;
        }
    }

    private void ResetButtonColors()
    {
        var white = new SolidColorBrush(Color.FromRgb(255, 255, 255));
        var darkGray = new SolidColorBrush(Color.FromRgb(51, 51, 51));

        SyncLeftButton.Background = white;
        SyncLeftButton.Foreground = darkGray;
        SyncLeftButton.ToolTip = "Sync from FTP to Desktop";

        SyncRightButton.Background = white;
        SyncRightButton.Foreground = darkGray;
        SyncRightButton.ToolTip = "Sync from Desktop to FTP";
    }

    private async void SyncLeftButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingSyncToFtp = false;
        ConfirmationMessage.Text = "Download files from FTP to Desktop?";
        ConfirmationOverlay.Visibility = Visibility.Visible;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        // Reload config and projects
        _configService.LoadConfig();
        LoadProjects();
        
        // Then analyze the currently selected project
        _ = AnalyzeSyncStatusAsync();
    }

    private void AbortAnalysisButton_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("[MainWindow] Abort button clicked");
        _analysisCancellationToken?.Cancel();
    }

    private async void SyncRightButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingSyncToFtp = true;
        ConfirmationMessage.Text = "Upload files from Desktop to FTP?";
        ConfirmationOverlay.Visibility = Visibility.Visible;
    }

    private async void ConfirmYesButton_Click(object sender, RoutedEventArgs e)
    {
        ConfirmationOverlay.Visibility = Visibility.Collapsed;
        await PerformSyncAsync(isSyncToFtp: _pendingSyncToFtp);
    }

    private void ConfirmNoButton_Click(object sender, RoutedEventArgs e)
    {
        ConfirmationOverlay.Visibility = Visibility.Collapsed;
    }

    private async Task PerformSyncAsync(bool isSyncToFtp)
    {
        // Initialize FtpService if not already done
        if (_ftpService == null)
        {
            if (ProjectComboBox.SelectedIndex < 0 || ProjectComboBox.SelectedIndex >= _configService.GetProjects().Count)
            {
                UpdateStatus("Please select a project first", "", "");
                return;
            }

            var selectedProject = _configService.GetProjects()[ProjectComboBox.SelectedIndex];
            var ftpConfig = _configService.GetConfig().Ftp;
            var fastMode = FastModeCheckBox.IsChecked ?? false;

            _ftpService = new FtpService(ftpConfig, selectedProject.LocalPath, selectedProject.FtpRemotePath, fastMode);
            _ftpService.SetStatusCallback(UpdateAnalysisStatus);
            
            // Need to analyze first to populate LastComparisonResult
            try
            {
                _spinnerIndex = 0;
                _spinnerTimer?.Start();
                LoadingSpinner.Visibility = Visibility.Visible;
                UpdateStatus("Analyzing files...", "", "");
                
                SyncLeftButton.IsEnabled = false;
                SyncRightButton.IsEnabled = false;
                ProjectComboBox.IsEnabled = false;
                
                _analysisCancellationToken?.Cancel();
                _analysisCancellationToken = new CancellationTokenSource();
                
                var recommendation = await _ftpService.GetSyncRecommendationAsync(_analysisCancellationToken.Token);
                
                // Don't show results, just proceed to sync
                _spinnerTimer?.Stop();
                LoadingSpinner.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException)
            {
                _spinnerTimer?.Stop();
                LoadingSpinner.Visibility = Visibility.Collapsed;
                UpdateStatus("Analysis canceled", "", "");
                SyncLeftButton.IsEnabled = true;
                SyncRightButton.IsEnabled = true;
                ProjectComboBox.IsEnabled = true;
                return;
            }
            catch (Exception ex)
            {
                _spinnerTimer?.Stop();
                LoadingSpinner.Visibility = Visibility.Collapsed;
                UpdateStatus($"Analysis failed: {ex.Message}", "", "");
                SyncLeftButton.IsEnabled = true;
                SyncRightButton.IsEnabled = true;
                ProjectComboBox.IsEnabled = true;
                return;
            }
        }

        try
        {
            // Show loading state
            _spinnerIndex = 0;
            _spinnerTimer?.Start();
            LoadingSpinner.Visibility = Visibility.Visible;
            RecommendationText.Text = isSyncToFtp ? "Uploading files..." : "Downloading files...";
            
            SyncLeftButton.IsEnabled = false;
            SyncRightButton.IsEnabled = false;

            // Perform the sync
            if (isSyncToFtp)
            {
                await _ftpService.SyncToFtpAsync(UpdateAnalysisStatus);
            }
            else
            {
                await _ftpService.SyncToLocalAsync(UpdateAnalysisStatus);
            }

            _spinnerTimer?.Stop();
            LoadingSpinner.Visibility = Visibility.Collapsed;

            // Show completion message
            UpdateStatus($"Sync completed successfully!", "", "");
            SyncLeftButton.IsEnabled = true;
            SyncRightButton.IsEnabled = true;
            ProjectComboBox.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _spinnerTimer?.Stop();
            LoadingSpinner.Visibility = Visibility.Collapsed;
            UpdateStatus($"Sync failed: {ex.Message}", "", "");
            SyncLeftButton.IsEnabled = true;
            SyncRightButton.IsEnabled = true;
        }
    }
}
