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
using Wsync.Models;
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
    private DateTime _analysisStartTime = DateTime.MinValue;
    private CancellationTokenSource? _analysisCancellationToken;
    private bool _pendingSyncToFtp = false;  // Track which sync action is pending confirmation
    private DateTime _lastUiUpdateTime = DateTime.MinValue;  // Throttle UI updates
    private const int UiUpdateThrottleMs = 100;  // Update UI max once every 100ms
    private int _pendingUiUpdates = 0;  // Track updates waiting in dispatcher queue
    private const int MaxPendingUpdates = 5;  // Max queued updates before skipping

    public MainWindow()
    {
        InitializeComponent();
        _configService = new ConfigService();
        InitializeSpinner();
        RecommendationText.Text = "Select a project and click Refresh to compare files.";
        
        // Initialize analysis mode to Quick (default)
        ModeComboBox.SelectedIndex = (int)AnalysisMode.Quick;
        
        LoadProjects();
        
        // Set window title with build date/time
        var buildTime = System.IO.File.GetLastWriteTime(System.Reflection.Assembly.GetExecutingAssembly().Location);
        this.Title = $"Wsync - Build {buildTime:yyyy-MM-dd HH:mm}";
    }

    private void InitializeSpinner()
    {
        _spinnerTimer = new DispatcherTimer();
        _spinnerTimer.Interval = TimeSpan.FromMilliseconds(80);
        _spinnerTimer.Tick += SpinnerTick;
    }

    private void SpinnerTick(object? sender, EventArgs e)
    {
        SpinnerText.Text = _spinnerFrames[_spinnerIndex % _spinnerFrames.Length];
        _spinnerIndex++;
    }

    private void LoadProjects(bool attachSelectionChangedEvent = true)
    {
        // Clear existing items
        ProjectComboBox.Items.Clear();
        ProjectComboBox.SelectionChanged -= ProjectComboBox_SelectionChanged;
        
        var config = _configService.GetConfig();
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
            
            // Select first project by default
            ProjectComboBox.SelectedIndex = 0;
            
            ProjectComboBox.IsEnabled = true;
            if (attachSelectionChangedEvent)
            {
                ProjectComboBox.SelectionChanged += ProjectComboBox_SelectionChanged;
            }
            
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
        
        // Validate project configuration
        var validationError = selectedProject.Validate();
        if (validationError != null)
        {
            _spinnerTimer?.Stop();
            LoadingSpinner.Visibility = Visibility.Collapsed;
            AbortAnalysisButton.Visibility = Visibility.Collapsed;
            System.Diagnostics.Debug.WriteLine("[MainWindow] AbortAnalysisButton -> Collapsed (validation error)");
            UpdateStatus($"❌ Configuration Error: {validationError}", "", "");
            ResetButtonColors();
            SyncLeftButton.IsEnabled = true;
            SyncRightButton.IsEnabled = true;
            ProjectComboBox.IsEnabled = true;
            return;
        }

        var config = _configService.GetConfig();
        var ftpConfig = config.Ftp;
        var mode = (AnalysisMode)ModeComboBox.SelectedIndex;

        _ftpService = new FtpService(ftpConfig, selectedProject.LocalPath, selectedProject.FtpRemotePath, config.ExcludedExtensions, config.ExcludedFolders, mode);
        _ftpService.SetStatusCallback(UpdateAnalysisStatus);

        try
        {
            // Show loading state
            _spinnerIndex = 0;
            _spinnerTimer?.Start();
            LoadingSpinner.Visibility = Visibility.Visible;
            AbortAnalysisButton.Visibility = Visibility.Visible;
            AbortAnalysisButton.UpdateLayout();  // Force WPF to render the button
            System.Diagnostics.Debug.WriteLine("[MainWindow] AbortAnalysisButton set to Visible");
            UpdateStatus("Analyzing files...", "", "");
            
            _analysisStartTime = DateTime.Now;
            
            // Reset icon colors to neutral at start of analysis
            ResetIconColors();
            
            SyncLeftButton.Background = new SolidColorBrush(Color.FromRgb(200, 200, 200));
            SyncRightButton.Background = new SolidColorBrush(Color.FromRgb(200, 200, 200));
            SyncLeftButton.IsEnabled = false;
            SyncRightButton.IsEnabled = false;
            ProjectComboBox.IsEnabled = false;
            ModeComboBox.IsEnabled = false;  // Disable mode changes during analysis

            // Run analysis on thread pool to avoid blocking UI
            var recommendation = await Task.Run(RunAnalysisAsync);

            _spinnerTimer?.Stop();
            
            // Ensure button is visible for at least 1 second so user can see it and click if needed
            var analysisEndTime = DateTime.Now;
            var elapsedTime = analysisEndTime - _analysisStartTime;
            if (elapsedTime.TotalSeconds < 1.0)
            {
                await Task.Delay((int)(1000 - elapsedTime.TotalMilliseconds));
            }
            
            LoadingSpinner.Visibility = Visibility.Collapsed;
            AbortAnalysisButton.Visibility = Visibility.Collapsed;
            System.Diagnostics.Debug.WriteLine("[MainWindow] AbortAnalysisButton set to Collapsed (analysis complete)");
            _lastCheckTime = DateTime.Now;

            // Show the checked timestamp immediately after analysis completes
            // (Now handled in UpdateRecommendationText)
            
            // Update button colors and status based on recommendation
            UpdateButtonColors(recommendation);
            UpdateRecommendationText(recommendation);

            SyncLeftButton.IsEnabled = true;
            SyncRightButton.IsEnabled = true;
            ProjectComboBox.IsEnabled = true;
            ModeComboBox.IsEnabled = true;  // Re-enable mode selection
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Analysis was canceled");
            _spinnerTimer?.Stop();
            LoadingSpinner.Visibility = Visibility.Collapsed;
            AbortAnalysisButton.Visibility = Visibility.Collapsed;
            System.Diagnostics.Debug.WriteLine("[MainWindow] AbortAnalysisButton -> Collapsed (canceled)");
            UpdateStatus("Analysis canceled", "", "");
            SyncLeftButton.IsEnabled = true;
            SyncRightButton.IsEnabled = true;
            ProjectComboBox.IsEnabled = true;
            ModeComboBox.IsEnabled = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Error analyzing sync status: {ex.Message}");
            _spinnerTimer?.Stop();
            LoadingSpinner.Visibility = Visibility.Collapsed;
            AbortAnalysisButton.Visibility = Visibility.Collapsed;
            System.Diagnostics.Debug.WriteLine("[MainWindow] AbortAnalysisButton -> Collapsed (exception)");
            UpdateStatus($"Error: {ex.Message}", "", "");
            ResetButtonColors();
            SyncLeftButton.IsEnabled = true;
            SyncRightButton.IsEnabled = true;
            ProjectComboBox.IsEnabled = true;
            ModeComboBox.IsEnabled = true;
        }
    }

    private void UpdateAnalysisStatus(string message)
    {
        // Skip if too many updates are already queued
        if (_pendingUiUpdates >= MaxPendingUpdates)
        {
            return;
        }
        
        _pendingUiUpdates++;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _pendingUiUpdates--;
            UpdateAnalysisStatusDispatcher(message);
        }));
    }

    private void UpdateAnalysisStatusDispatcher(string message)
    {
        // Throttle UI updates to avoid blocking the UI thread
        var now = DateTime.Now;
        if ((now - _lastUiUpdateTime).TotalMilliseconds < UiUpdateThrottleMs)
        {
            return;  // Skip this update, it's too soon
        }
        _lastUiUpdateTime = now;
        
        // Keep only last 50 lines in the UI
        const int MaxLines = 50;
        
        if (RecommendationText.Text.Length > 0)
        {
            RecommendationText.Text += Environment.NewLine + message;
        }
        else
        {
            RecommendationText.Text = message;
        }
        
        // Trim to last 50 lines
        var lines = RecommendationText.Text.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
        if (lines.Length > MaxLines)
        {
            RecommendationText.Text = string.Join(Environment.NewLine, lines.TakeLast(MaxLines));
        }
        
        // Auto-scroll to bottom to show latest message
        RecommendationText.CaretIndex = RecommendationText.Text.Length;
        RecommendationText.ScrollToEnd();
    }

    private void UpdateStatus(string message, string recommendation, string advice)
    {
        Dispatcher.Invoke(UpdateStatusDispatcher, message, advice);
    }

    private void UpdateStatusDispatcher(string message, string advice)
    {
        RecommendationText.Text = message;
        if (!string.IsNullOrEmpty(advice))
        {
            RecommendationText.Text += $"\n\n{advice}";
        }
    }

    private void UpdateRecommendationText(SyncRecommendation recommendation)
    {
        // Clear the live logs now that analysis is complete
        var checkTime = _lastCheckTime.ToString("yyyy-MM-dd HH:mm:ss");
        var result = _ftpService?.LastComparisonResult;
        
        // Calculate elapsed time
        var elapsedTime = _lastCheckTime - _analysisStartTime;
        var elapsedStr = $"{elapsedTime.Minutes}'{elapsedTime.Seconds:D2}";
        
        // Update icon colors based on recommendation
        UpdateIconColors(recommendation);
        
        // If there's an error, show it prominently
        if (!string.IsNullOrEmpty(result?.Error))
        {
            RecommendationText.Text = $"❌ {result.Error}";
            RecommendationText.FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
            return;
        }

        var fileListText = BuildFileListText(result);
        var header = $"Checked on {checkTime} in {elapsedStr}\n\n";
        
            // Detect if this is git mode for font selection
            bool isGitMode = (result?.NewerLocalFiles.Count + result?.NewerRemoteFiles.Count > 0) &&
                            result?.LocalOnlyFiles.Count == 0 &&
                            result?.RemoteOnlyFiles.Count == 0 &&
                            ((result?.NewerLocalFiles.FirstOrDefault()?.Contains("(newer)") ?? false) ||
                             (result?.NewerLocalFiles.FirstOrDefault()?.Contains("(older)") ?? false) ||
                             (result?.NewerLocalFiles.FirstOrDefault()?.Contains("(same)") ?? false));        if (isGitMode)
        {
            RecommendationText.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        }
        else
        {
            RecommendationText.FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        }
        
        switch (recommendation)
        {
            case SyncRecommendation.SyncToLocal:
                RecommendationText.Text = $"{header}✓ RECOMMENDATION: Sync to Local (FTP → Desktop)\n\n{fileListText}\n\nSee full log in wsync.log";
                break;

            case SyncRecommendation.SyncToFtp:
                RecommendationText.Text = $"{header}✓ RECOMMENDATION: Sync to FTP (Desktop → FTP)\n\n{fileListText}\n\nSee full log in wsync.log";
                break;

            case SyncRecommendation.BothNewer:
                RecommendationText.Text = $"{header}⚠ CONFLICT: Files on both sides have updates\n\n{fileListText}\n\nSee full log in wsync.log";
                break;

            case SyncRecommendation.InSync:
                RecommendationText.Text = $"{header}✓ All files are synchronized\n\nNo sync needed.\n\nSee full log in wsync.log";
                break;

            case SyncRecommendation.Unknown:
                RecommendationText.Text = $"{header}⚠ Could not determine sync direction\n\nPlease check your configuration.\n\nSee full log in wsync.log";
                break;
        }
    }

    private string BuildFileListText(ComparisonResult? result)
    {
        if (result == null) return "";

        var sb = new System.Text.StringBuilder();
        
        // Check if this is a git mode comparison (commits only)
        // Git mode only has entries in NewerLocalFiles or NewerRemoteFiles with commit info
        bool isGitMode = (result.NewerLocalFiles.Count + result.NewerRemoteFiles.Count > 0) &&
                        result.LocalOnlyFiles.Count == 0 &&
                        result.RemoteOnlyFiles.Count == 0 &&
                        ((result.NewerLocalFiles.FirstOrDefault()?.Contains("(newer)") ?? false) ||
                         (result.NewerLocalFiles.FirstOrDefault()?.Contains("(older)") ?? false) ||
                         (result.NewerLocalFiles.FirstOrDefault()?.Contains("(same)") ?? false));

        if (isGitMode)
        {
            // Git mode: show commit comparison only
            if (result.NewerLocalFiles.Count > 0)
            {
                foreach (var commit in result.NewerLocalFiles)
                {
                    sb.AppendLine(commit);
                }
            }

            if (result.NewerRemoteFiles.Count > 0)
            {
                foreach (var commit in result.NewerRemoteFiles)
                {
                    sb.AppendLine(commit);
                }
            }

            if (result.Recommendation == SyncRecommendation.InSync)
            {
                sb.AppendLine("Both repositories have the same latest commit.");
            }
        }
        else
        {
            // File mode: show file counts and details
            
            // FILES NEWER LOCALLY
            var fnlLabel = $"FILES NEWER LOCALLY";
            if (result.IsQuickModeEarlyDecision && result.NewerLocalFiles.Count >= 3)
                fnlLabel += $" (at least {result.NewerLocalFiles.Count})";
            else
                fnlLabel += $" ({result.NewerLocalFiles.Count})";
            sb.AppendLine($"{fnlLabel}:");
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
            var fterLabel = $"FILES NEWER ON FTP";
            if (result.IsQuickModeEarlyDecision && result.NewerRemoteFiles.Count >= 3)
                fterLabel += $" (at least {result.NewerRemoteFiles.Count})";
            else
                fterLabel += $" ({result.NewerRemoteFiles.Count})";
            sb.AppendLine($"{fterLabel}:");
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
        // Save current selections before reloading
        int savedProjectIndex = ProjectComboBox.SelectedIndex;
        int savedModeIndex = ModeComboBox.SelectedIndex;
        
        // Reload config and projects
        _configService.LoadConfig();
        
        // Temporarily detach both SelectionChanged events to prevent them from triggering analysis
        ProjectComboBox.SelectionChanged -= ProjectComboBox_SelectionChanged;
        ModeComboBox.SelectionChanged -= ModeComboBox_SelectionChanged;
        
        // LoadProjects with attachSelectionChangedEvent=false so it doesn't re-attach
        LoadProjects(attachSelectionChangedEvent: false);
        
        // Restore the previous selections if they still exist
        if (savedProjectIndex >= 0 && savedProjectIndex < ProjectComboBox.Items.Count)
        {
            ProjectComboBox.SelectedIndex = savedProjectIndex;
        }
        
        // Re-attach the SelectionChanged event
        ProjectComboBox.SelectionChanged += ProjectComboBox_SelectionChanged;
        
        if (savedModeIndex >= 0 && savedModeIndex < ModeComboBox.Items.Count)
        {
            ModeComboBox.SelectedIndex = savedModeIndex;
        }
        
        // Re-attach the Mode SelectionChanged event
        ModeComboBox.SelectionChanged += ModeComboBox_SelectionChanged;
        
        // Then analyze the currently selected project (only once, not triggered by selection change)
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
            var config = _configService.GetConfig();
            var ftpConfig = config.Ftp;
            var mode = (AnalysisMode)ModeComboBox.SelectedIndex;

            _ftpService = new FtpService(ftpConfig, selectedProject.LocalPath, selectedProject.FtpRemotePath, config.ExcludedExtensions, config.ExcludedFolders, mode);
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

            // Show completion message (include timestamp)
            UpdateStatus($"Sync completed successfully! {DateTime.Now:yyyy-MM-dd HH:mm}", "", "");
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

    private void UpdateIconColors(SyncRecommendation recommendation)
    {
        // Reset to neutral gray
        var neutralColor = System.Windows.Media.Color.FromRgb(51, 51, 51);  // #333
        var greenColor = System.Windows.Media.Color.FromRgb(34, 139, 34);   // Forest green
        var redColor = System.Windows.Media.Color.FromRgb(220, 20, 60);     // Crimson red
        
        var neutralBrush = new System.Windows.Media.SolidColorBrush(neutralColor);
        var greenBrush = new System.Windows.Media.SolidColorBrush(greenColor);
        var redBrush = new System.Windows.Media.SolidColorBrush(redColor);
        
        switch (recommendation)
        {
            case SyncRecommendation.SyncToLocal:
                // Remote (FTP) is newer, so cloud is green and desktop is red
                DesktopIcon.Foreground = redBrush;
                CloudIcon.Foreground = greenBrush;
                break;
            
            case SyncRecommendation.SyncToFtp:
                // Local (Desktop) is newer, so desktop is green and cloud is red
                DesktopIcon.Foreground = greenBrush;
                CloudIcon.Foreground = redBrush;
                break;
            
            case SyncRecommendation.InSync:
                // Both are in sync - both green
                DesktopIcon.Foreground = greenBrush;
                CloudIcon.Foreground = greenBrush;
                break;
            
            case SyncRecommendation.BothNewer:
            case SyncRecommendation.Unknown:
            default:
                // Reset to neutral for conflicts or unknown states
                DesktopIcon.Foreground = neutralBrush;
                CloudIcon.Foreground = neutralBrush;
                break;
        }
    }

    private void ResetIconColors()
    {
        var neutralColor = System.Windows.Media.Color.FromRgb(51, 51, 51);  // #333
        var neutralBrush = new System.Windows.Media.SolidColorBrush(neutralColor);
        DesktopIcon.Foreground = neutralBrush;
        CloudIcon.Foreground = neutralBrush;
    }

    private void ModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Automatically analyze when mode changes
        if (ModeComboBox.SelectedIndex >= 0)
        {
            _ = AnalyzeSyncStatusAsync();
        }
    }

    private async Task<SyncRecommendation> RunAnalysisAsync()
    {
        if (_ftpService != null)
        {
            return await _ftpService.GetSyncRecommendationAsync(_analysisCancellationToken?.Token ?? CancellationToken.None);
        }
        return SyncRecommendation.Unknown;
    }
}
