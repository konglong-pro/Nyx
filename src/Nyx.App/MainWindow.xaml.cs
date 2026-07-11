using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Nyx.Core.Execution;
using Nyx.Core.Preflight;
using Nyx.Core.Reporting;
using Nyx.Core.Windows;

namespace Nyx.App;

public partial class MainWindow : Window
{
    private static readonly string ApplicationVersion = GetApplicationVersion();
    private readonly PreflightService _preflightService;
    private readonly IShredTargetProcessor _targetProcessor;
    private readonly ShredReportExporter _reportExporter = new(ApplicationVersion);
    private readonly IReadOnlyList<string> _startupPaths;
    private WorkflowState _state = WorkflowState.Ready;
    private PreflightReport? _report;
    private ShredBatchResult? _batchResult;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _stopAfterCurrentCancellation;
    private readonly Stopwatch _shredStopwatch = new();
    private int _failureCount;
    private bool _closeWhenSafe;
    private bool _allowClose;

    public MainWindow()
        : this([])
    {
    }

    internal MainWindow(IReadOnlyList<string> startupPaths)
    {
        InitializeComponent();

        ArgumentNullException.ThrowIfNull(startupPaths);
        _startupPaths = startupPaths;
        var executablePath = Environment.ProcessPath
            ?? Assembly.GetExecutingAssembly().Location;
        _preflightService = new PreflightService(
            new WindowsTargetInventory(),
            PathSafetyPolicy.CreateForCurrentMachine(executablePath));
        _targetProcessor = new WindowsShredTargetProcessor();

        SetState(WorkflowState.Ready);
        Loaded += MainWindow_Loaded;
    }

    internal void ReceiveForwardedPaths(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() => _ = HandleForwardedPathsAsync(paths)));
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_startupPaths.Count > 0)
        {
            await InspectSelectionAsync(_startupPaths, mergeWithReview: false);
        }
    }

    private async Task HandleForwardedPathsAsync(IReadOnlyList<string> paths)
    {
        BringWindowToFront();
        if (paths.Count == 0)
        {
            return;
        }

        if (_state is not (
            WorkflowState.Ready
            or WorkflowState.Review
            or WorkflowState.Blocked))
        {
            ShowNotice(
                $"Nyx is busy. {paths.Count} forwarded path(s) were not added.");
            return;
        }

        await InspectSelectionAsync(
            paths,
            mergeWithReview: _state is WorkflowState.Review or WorkflowState.Blocked);
    }

    private async void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            AddToRecent = false,
            CheckFileExists = true,
            DereferenceLinks = false,
            Multiselect = true,
            Title = "Choose files to shred",
        };
        if (dialog.ShowDialog(this) == true)
        {
            await InspectSelectionAsync(
                dialog.FileNames,
                mergeWithReview: _state is WorkflowState.Review or WorkflowState.Blocked);
        }
    }

    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            AddToRecent = false,
            DereferenceLinks = false,
            Multiselect = true,
            Title = "Choose folders to shred",
        };
        if (dialog.ShowDialog(this) == true)
        {
            await InspectSelectionAsync(
                dialog.FolderNames,
                mergeWithReview: _state is WorkflowState.Review or WorkflowState.Blocked);
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = IsAcceptingSelection
            && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!IsAcceptingSelection)
        {
            ShowNotice("Nyx is busy. Dropped paths were not added.");
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            await InspectSelectionAsync(
                paths,
                mergeWithReview: _state is WorkflowState.Review or WorkflowState.Blocked);
        }
    }

    private async Task InspectSelectionAsync(
        IReadOnlyList<string> paths,
        bool mergeWithReview)
    {
        var previousReport = _report;
        var cancellation = new CancellationTokenSource();
        ReplaceOperationCancellation(cancellation);
        HideNotice();
        SetState(WorkflowState.Checking);
        await Dispatcher.Yield(DispatcherPriority.Render);

        try
        {
            var sourcePaths = await Task.Run(
                () => NormalizeSelection(
                    paths,
                    mergeWithReview ? previousReport?.SourcePaths : null,
                    cancellation.Token),
                cancellation.Token);
            if (sourcePaths.Length == 0)
            {
                _report = previousReport;
                SetStateForExistingReport();
                ShowNotice("No paths were selected.");
                return;
            }

            var report = await _preflightService.InspectAsync(
                sourcePaths,
                cancellation.Token);
            _report = report;
            PopulateReview(report);
            SetState(report.CanStart ? WorkflowState.Review : WorkflowState.Blocked);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _report = previousReport;
            if (_closeWhenSafe)
            {
                FinishDeferredClose();
                return;
            }

            SetStateForExistingReport();
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            _report = previousReport;
            SetStateForExistingReport();
            ShowNotice($"The check could not be completed: {exception.Message}");
        }
        finally
        {
            ClearOperationCancellation(cancellation);
        }
    }

    private static string[] NormalizeSelection(
        IReadOnlyList<string> paths,
        IReadOnlyList<string>? existingPaths,
        CancellationToken cancellationToken)
    {
        var normalized = new List<string>((existingPaths?.Count ?? 0) + paths.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in (existingPaths ?? []).Concat(paths))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = PathInput.NormalizeOrOriginal(path);
            if (seen.Add(candidate))
            {
                normalized.Add(candidate);
            }
        }

        return normalized.ToArray();
    }

    private void PopulateReview(PreflightReport report)
    {
        ReviewHeadingText.Text = report.CanStart
            ? "Review before shredding"
            : "This batch is blocked";
        ReviewDescriptionText.Text = report.CanStart
            ? "Review every expanded item and warning. No data has been changed."
            : "Nyx will not modify any item in this batch. Clear the batch and choose a different selection.";
        ReviewCountText.Text = report.Targets.Count.ToString(CultureInfo.CurrentCulture);
        ReviewSizeText.Text = FormatBytes(report.TotalBytes);
        ReviewFileSystemText.Text = JoinDistinct(
            report.Targets.Select(static target => target.Snapshot.FileSystem.ToString()));
        ReviewMediumText.Text = JoinDistinct(
            report.Targets.Select(static target => target.Snapshot.Medium.ToString()));
        ReviewAssuranceText.Text = GetBatchAssurance(report);
        IssueList.ItemsSource = report.Issues.Count == 0
            ? [new IssueRow("No warnings or blockers.")]
            : report.Issues.Select(static issue => new IssueRow(
                $"[{(issue.Severity == PreflightSeverity.Blocking ? "BLOCKING" : "WARNING")}] {issue.Message}"))
                .ToArray();
        TargetList.ItemsSource = report.Targets
            .Select(static target => new TargetRow(
                target.Path,
                target.Snapshot.Kind.ToString(),
                FormatBytes(GetTargetBytes(target)),
                target.Assurance.ToString()))
            .ToArray();
        ContinueButton.IsEnabled = report.CanStart;
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null || !_report.CanStart)
        {
            return;
        }

        ConfirmationSummaryText.Text =
            $"{_report.Targets.Count:N0} item(s), {FormatBytes(_report.TotalBytes)}, assurance: {GetBatchAssurance(_report)}.";
        TypedConfirmationPanel.Visibility = _report.RequiresTypedConfirmation
            ? Visibility.Visible
            : Visibility.Collapsed;
        TypedConfirmationBox.Clear();
        UpdateConfirmButton();
        SetState(WorkflowState.Confirmation);
    }

    private void BackToReviewButton_Click(object sender, RoutedEventArgs e)
    {
        SetStateForExistingReport();
    }

    private void TypedConfirmationBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateConfirmButton();
    }

    private async void ConfirmShredButton_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null
            || !_report.CanStart
            || (_report.RequiresTypedConfirmation
                && !string.Equals(
                    TypedConfirmationBox.Text,
                    "SHRED",
                    StringComparison.Ordinal)))
        {
            return;
        }

        await RevalidateAndShredAsync(_report);
    }

    private async Task RevalidateAndShredAsync(PreflightReport confirmedReport)
    {
        var cancellation = new CancellationTokenSource();
        ReplaceOperationCancellation(cancellation);
        HideNotice();
        SetState(WorkflowState.Revalidating);
        await Dispatcher.Yield(DispatcherPriority.Render);

        try
        {
            var revalidation = await _preflightService.RevalidateAsync(
                confirmedReport,
                cancellation.Token);
            if (!revalidation.IsUnchanged)
            {
                var issues = revalidation.Issue is null
                    ? revalidation.CurrentReport.Issues
                    : revalidation.CurrentReport.Issues
                        .Append(revalidation.Issue)
                        .ToArray();
                _report = new PreflightReport(
                    revalidation.CurrentReport.SourcePaths,
                    revalidation.CurrentReport.Targets,
                    issues);
                PopulateReview(_report);
                SetState(WorkflowState.Blocked);
                ShowNotice("The batch changed after confirmation. Nothing was modified.");
                return;
            }

            _report = revalidation.CurrentReport;
            await ShredAsync(_report);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (_closeWhenSafe)
            {
                FinishDeferredClose();
                return;
            }

            SetStateForExistingReport();
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            SetStateForExistingReport();
            ShowNotice($"Revalidation failed. Nothing was started: {exception.Message}");
        }
        finally
        {
            ClearOperationCancellation(cancellation);
        }
    }

    private async Task ShredAsync(PreflightReport report)
    {
        _failureCount = 0;
        FailureCountText.Text = "0";
        CurrentItemText.Text = "Preparing…";
        OverallProgressBar.Maximum = Math.Max(1, report.TotalBytes);
        OverallProgressBar.Value = 0;
        ProgressSummaryText.Text = $"0 B of {FormatBytes(report.TotalBytes)}";
        SpeedText.Text = "—";
        EtaText.Text = "—";
        StopButton.IsEnabled = true;
        StopButton.Content = "_Stop After Current File";

        var stopCancellation = new CancellationTokenSource();
        _stopAfterCurrentCancellation = stopCancellation;
        var progress = new Progress<ShredProgress>(UpdateProgress);
        var observer = new ObservingTargetProcessor(
            _targetProcessor,
            report,
            progress,
            ItemCompleted);
        var engine = new ShredEngine(observer);
        var startedAt = DateTimeOffset.UtcNow;
        _shredStopwatch.Restart();
        SetState(WorkflowState.Shredding);
        await Dispatcher.Yield(DispatcherPriority.Render);

        try
        {
            _batchResult = await engine.ExecuteAsync(
                report,
                progress: null,
                stopCancellation.Token);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            _batchResult = BuildAbortedResult(
                report,
                observer.CompletedResults,
                startedAt);
            ShowNotice(
                $"Nyx stopped after an unexpected execution error. Verify affected items manually: {exception.Message}");
        }
        finally
        {
            _shredStopwatch.Stop();
            if (ReferenceEquals(_stopAfterCurrentCancellation, stopCancellation))
            {
                _stopAfterCurrentCancellation = null;
            }

            stopCancellation.Dispose();
        }

        PopulateResults(_batchResult);
        SetState(WorkflowState.Results);
        if (_closeWhenSafe)
        {
            FinishDeferredClose();
        }
    }

    private void UpdateProgress(ShredProgress progress)
    {
        CurrentItemText.Text =
            $"Item {progress.ItemIndex:N0} of {progress.ItemCount:N0} — {progress.Path}";
        var processed = Math.Clamp(
            progress.TotalBytesProcessed,
            0,
            Math.Max(1, progress.TotalBytes));
        OverallProgressBar.Maximum = Math.Max(1, progress.TotalBytes);
        OverallProgressBar.Value = processed;
        ProgressSummaryText.Text =
            $"{FormatBytes(progress.TotalBytesProcessed)} of {FormatBytes(progress.TotalBytes)}";

        var elapsedSeconds = _shredStopwatch.Elapsed.TotalSeconds;
        if (elapsedSeconds <= 0.25 || progress.TotalBytesProcessed <= 0)
        {
            return;
        }

        var bytesPerSecond = progress.TotalBytesProcessed / elapsedSeconds;
        SpeedText.Text = $"{FormatBytes((long)bytesPerSecond)}/s";
        var remainingBytes = Math.Max(0, progress.TotalBytes - progress.TotalBytesProcessed);
        EtaText.Text = bytesPerSecond <= 0
            ? "—"
            : FormatDuration(TimeSpan.FromSeconds(remainingBytes / bytesPerSecond));
    }

    private void ItemCompleted(ShredItemResult result)
    {
        if (IsSuccessful(result.Status))
        {
            return;
        }

        var count = Interlocked.Increment(ref _failureCount);
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => FailureCountText.Text =
                count.ToString(CultureInfo.CurrentCulture)));
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        RequestStopAfterCurrent();
    }

    private void RequestStopAfterCurrent()
    {
        if (_state != WorkflowState.Shredding
            || _stopAfterCurrentCancellation is null
            || _stopAfterCurrentCancellation.IsCancellationRequested)
        {
            return;
        }

        _stopAfterCurrentCancellation.Cancel();
        StopButton.IsEnabled = false;
        StopButton.Content = "Stopping after current file…";
        ShowNotice("Stop requested. The current file and all of its streams will finish first.");
    }

    private void PopulateResults(ShredBatchResult batch)
    {
        ResultsList.ItemsSource = batch.Items
            .Select(static result => new ResultRow(
                result.CurrentPath ?? result.Target.Path,
                result.Status.ToString(),
                result.FailureStage == ShredFailureStage.None
                    ? "—"
                    : result.FailureStage.ToString(),
                FormatBytes(result.BytesAcceptedByOperatingSystem),
                result.NativeErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "—"))
            .ToArray();
        var grouped = batch.Items
            .GroupBy(static result => result.Status)
            .Select(static group => $"{group.Key}: {group.Count():N0}");
        ResultsSummaryText.Text =
            $"{batch.Status} — {string.Join("; ", grouped)}";
        RetryButton.IsEnabled = GetRetryPaths(batch).Length > 0;
        IncludeFullPathsCheckBox.IsChecked = false;
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_batchResult is null)
        {
            return;
        }

        var paths = GetRetryPaths(_batchResult);
        if (paths.Length == 0)
        {
            ShowNotice("No remaining items are available to retry.");
            return;
        }

        _batchResult = null;
        _report = null;
        await InspectSelectionAsync(paths, mergeWithReview: false);
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_batchResult is null)
        {
            return;
        }

        var includeFullPaths = IncludeFullPathsCheckBox.IsChecked == true;
        if (includeFullPaths
            && MessageBox.Show(
                this,
                "Full paths can reveal user names, project names, and folder structure. Include them in this report?",
                "Include sensitive paths?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            AddToRecent = false,
            DefaultExt = ".json",
            FileName = $"nyx-report-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            Filter = "JSON report (*.json)|*.json",
            OverwritePrompt = true,
            Title = "Export Nyx report",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await using var destination = new FileStream(
                dialog.FileName,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous);
            await _reportExporter.ExportAsync(
                _batchResult,
                destination,
                includeFullPaths,
                CancellationToken.None);
            ShowNotice(includeFullPaths
                ? "Report exported with full paths."
                : "Redacted report exported without paths.");
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            ShowNotice($"The report could not be exported: {exception.Message}");
        }
    }

    private void NewBatchButton_Click(object sender, RoutedEventArgs e)
    {
        ResetToReady();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        ResetToReady();
    }

    private void ResetToReady()
    {
        _report = null;
        _batchResult = null;
        TargetList.ItemsSource = null;
        IssueList.ItemsSource = null;
        ResultsList.ItemsSource = null;
        HideNotice();
        SetState(WorkflowState.Ready);
    }

    private void CancelOperationButton_Click(object sender, RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        if (_state == WorkflowState.Shredding)
        {
            e.Cancel = true;
            _closeWhenSafe = true;
            RequestStopAfterCurrent();
            ShowNotice("Nyx will close after the current file and all of its streams finish.");
            return;
        }

        if (_state is WorkflowState.Checking or WorkflowState.Revalidating)
        {
            e.Cancel = true;
            _closeWhenSafe = true;
            _operationCancellation?.Cancel();
        }
    }

    private void SetState(WorkflowState state)
    {
        _state = state;
        ReadyPanel.Visibility = Visibility.Collapsed;
        CheckingPanel.Visibility = Visibility.Collapsed;
        ReviewPanel.Visibility = Visibility.Collapsed;
        ConfirmationPanel.Visibility = Visibility.Collapsed;
        RevalidatingPanel.Visibility = Visibility.Collapsed;
        ShreddingPanel.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Collapsed;

        switch (state)
        {
            case WorkflowState.Ready:
                ReadyPanel.Visibility = Visibility.Visible;
                StateText.Text = "Ready";
                break;
            case WorkflowState.Checking:
                CheckingPanel.Visibility = Visibility.Visible;
                StateText.Text = "Checking";
                break;
            case WorkflowState.Blocked:
                ReviewPanel.Visibility = Visibility.Visible;
                StateText.Text = "Blocked";
                break;
            case WorkflowState.Review:
                ReviewPanel.Visibility = Visibility.Visible;
                StateText.Text = "Review";
                break;
            case WorkflowState.Confirmation:
                ConfirmationPanel.Visibility = Visibility.Visible;
                StateText.Text = "Confirmation";
                break;
            case WorkflowState.Revalidating:
                RevalidatingPanel.Visibility = Visibility.Visible;
                StateText.Text = "Revalidating";
                break;
            case WorkflowState.Shredding:
                ShreddingPanel.Visibility = Visibility.Visible;
                StateText.Text = "Shredding";
                break;
            case WorkflowState.Results:
                ResultsPanel.Visibility = Visibility.Visible;
                StateText.Text = "Results";
                break;
            default:
                throw new InvalidOperationException("Unknown workflow state.");
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(FocusCurrentState));
    }

    private void FocusCurrentState()
    {
        switch (_state)
        {
            case WorkflowState.Ready:
                AddFilesButton.Focus();
                break;
            case WorkflowState.Checking:
                CancelCheckButton.Focus();
                break;
            case WorkflowState.Blocked:
                TargetList.Focus();
                break;
            case WorkflowState.Review:
                ContinueButton.Focus();
                break;
            case WorkflowState.Confirmation when
                TypedConfirmationPanel.Visibility == Visibility.Visible:
                TypedConfirmationBox.Focus();
                break;
            case WorkflowState.Confirmation:
                ConfirmShredButton.Focus();
                break;
            case WorkflowState.Shredding:
                StopButton.Focus();
                break;
            case WorkflowState.Results:
                ResultsList.Focus();
                break;
        }
    }

    private void SetStateForExistingReport()
    {
        if (_report is null)
        {
            SetState(WorkflowState.Ready);
            return;
        }

        PopulateReview(_report);
        SetState(_report.CanStart ? WorkflowState.Review : WorkflowState.Blocked);
    }

    private void UpdateConfirmButton()
    {
        ConfirmShredButton.IsEnabled = _report is not null
            && _report.CanStart
            && (!_report.RequiresTypedConfirmation
                || string.Equals(
                    TypedConfirmationBox.Text,
                    "SHRED",
                    StringComparison.Ordinal));
    }

    private void ReplaceOperationCancellation(CancellationTokenSource cancellation)
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = cancellation;
    }

    private void ClearOperationCancellation(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_operationCancellation, cancellation))
        {
            _operationCancellation = null;
        }

        cancellation.Dispose();
    }

    private void FinishDeferredClose()
    {
        _allowClose = true;
        Close();
    }

    private void BringWindowToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void ShowNotice(string message)
    {
        NoticeText.Text = message;
        NoticeBorder.Visibility = Visibility.Visible;
    }

    private void HideNotice()
    {
        NoticeBorder.Visibility = Visibility.Collapsed;
        NoticeText.Text = string.Empty;
    }

    private bool IsAcceptingSelection => _state is
        WorkflowState.Ready
        or WorkflowState.Review
        or WorkflowState.Blocked;

    private static ShredBatchResult BuildAbortedResult(
        PreflightReport report,
        IReadOnlyList<ShredItemResult> completed,
        DateTimeOffset startedAt)
    {
        var completedIds = completed
            .Select(static item => (item.Target.VolumeId, item.Target.FileId))
            .ToHashSet();
        var notStarted = report.Targets
            .Where(target => !completedIds.Contains(
                (target.Snapshot.VolumeId, target.Snapshot.FileId)))
            .Select(static target => new ShredItemResult(
                target.Snapshot,
                ShredItemStatus.NotStarted,
                0,
                null,
                false));
        return new ShredBatchResult(
            ShredBatchStatus.Aborted,
            startedAt,
            DateTimeOffset.UtcNow,
            completed.Concat(notStarted).ToArray());
    }

    private static string[] GetRetryPaths(ShredBatchResult batch)
    {
        return batch.Items
            .Where(static result => !IsSuccessful(result.Status))
            .Select(static result => result.CurrentPath ?? result.Target.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsSuccessful(ShredItemStatus status) => status is
        ShredItemStatus.Shredded
        or ShredItemStatus.DeletedWithoutNameRandomization
        or ShredItemStatus.LinkRemoved;

    private static long GetTargetBytes(PreflightTarget target)
    {
        return target.Snapshot.Streams.Count > 0
            ? target.Snapshot.Streams.Sum(static stream => stream.Length)
            : target.Snapshot.Length;
    }

    private static string GetBatchAssurance(PreflightReport report)
    {
        if (!report.CanStart)
        {
            return "Blocked";
        }

        if (report.Targets.Any(static target =>
                target.Assurance == AssuranceLevel.Limited))
        {
            return "Limited";
        }

        if (report.Targets.All(static target =>
                target.Assurance == AssuranceLevel.LinkOnly))
        {
            return "Link only";
        }

        if (report.Targets.Any(static target =>
                target.Assurance == AssuranceLevel.LinkOnly))
        {
            return "Mixed";
        }

        return "Best-effort";
    }

    private static string JoinDistinct(IEnumerable<string> values)
    {
        var text = string.Join(", ", values.Distinct(StringComparer.Ordinal));
        return string.IsNullOrEmpty(text) ? "—" : text;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:N0} {units[unit]}"
            : $"{value:N1} {units[unit]}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromSeconds(1))
        {
            return "Less than a second";
        }

        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.CurrentCulture)
            : duration.ToString(@"m\:ss", CultureInfo.CurrentCulture);
    }

    private static string GetApplicationVersion()
    {
        var informationalVersion = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return string.IsNullOrWhiteSpace(informationalVersion)
            ? "0.1.0-beta.1"
            : informationalVersion.Split('+', 2)[0];
    }

    private enum WorkflowState
    {
        Ready,
        Checking,
        Blocked,
        Review,
        Confirmation,
        Revalidating,
        Shredding,
        Results,
    }

    private sealed record IssueRow(string DisplayText);

    private sealed record TargetRow(
        string Path,
        string Kind,
        string Size,
        string Assurance);

    private sealed record ResultRow(
        string Path,
        string Status,
        string FailureStage,
        string BytesWritten,
        string ErrorCode);

    private sealed class ObservingTargetProcessor(
        IShredTargetProcessor inner,
        PreflightReport report,
        IProgress<ShredProgress> progress,
        Action<ShredItemResult> itemCompleted) : IShredTargetProcessor
    {
        private readonly List<ShredItemResult> _completedResults = [];
        private long _completedBytes;
        private int _itemIndex;

        public IReadOnlyList<ShredItemResult> CompletedResults
        {
            get
            {
                lock (_completedResults)
                {
                    return _completedResults.ToArray();
                }
            }
        }

        public async Task<ShredItemResult> ProcessAsync(
            PreflightTarget target,
            IProgress<ShredProgress>? ignoredProgress)
        {
            var itemIndex = Interlocked.Increment(ref _itemIndex);
            var completedBeforeItem = Interlocked.Read(ref _completedBytes);
            var itemBytes = GetTargetBytes(target);
            progress.Report(new ShredProgress(
                target.Path,
                itemIndex,
                report.Targets.Count,
                0,
                itemBytes,
                Math.Clamp(completedBeforeItem, 0, report.TotalBytes),
                report.TotalBytes));
            void ReportCurrent(ShredProgress current) =>
                progress.Report(current with
                {
                    ItemIndex = itemIndex,
                    ItemCount = report.Targets.Count,
                    TotalBytesProcessed = Math.Clamp(
                        completedBeforeItem + current.ItemBytesProcessed,
                        0,
                        report.TotalBytes),
                    TotalBytes = report.TotalBytes,
                });
            var itemProgress = new ThrottledProgress<ShredProgress>(
                ReportCurrent,
                static current => current.ItemBytesProcessed >= current.ItemBytesTotal);
            var result = await inner.ProcessAsync(target, itemProgress)
                .ConfigureAwait(false);
            ReportCurrent(new ShredProgress(
                target.Path,
                itemIndex,
                report.Targets.Count,
                result.BytesAcceptedByOperatingSystem,
                itemBytes,
                completedBeforeItem + result.BytesAcceptedByOperatingSystem,
                report.TotalBytes));
            Interlocked.Add(
                ref _completedBytes,
                result.BytesAcceptedByOperatingSystem);
            lock (_completedResults)
            {
                _completedResults.Add(result);
            }

            itemCompleted(result);
            return result;
        }
    }

    private sealed class ThrottledProgress<T>(
        Action<T> report,
        Func<T, bool> isTerminal) : IProgress<T>
    {
        private static readonly long MinimumIntervalTicks = Math.Max(1, Stopwatch.Frequency / 20);
        private long _lastReportTimestamp;

        public void Report(T value)
        {
            var now = Stopwatch.GetTimestamp();
            if (!isTerminal(value)
                && _lastReportTimestamp != 0
                && now - _lastReportTimestamp < MinimumIntervalTicks)
            {
                return;
            }

            _lastReportTimestamp = now;
            report(value);
        }
    }
}
