using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nyx.App.Tests;

[TestClass]
public sealed class UiLayoutTests
{
    private static readonly string[] PanelNames =
    [
        "ReadyPanel", "CheckingPanel", "ReviewPanel", "ConfirmationPanel",
        "RevalidatingPanel", "ShreddingPanel", "ResultsPanel",
    ];

    public TestContext TestContext { get; set; } = null!;

    [STATestMethod]
    public void WorkflowPanelsRenderAtStandardAndMinimumSizesWithoutShowingWindow()
    {
        // Initialize only the XAML resources: do not run App.OnStartup, show a window,
        // contact the running Nyx instance, or invoke a workflow operation.
        using var application = new global::Nyx.App.App();
        application.InitializeComponent();
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var window = new MainWindow();
            // Initialize native/system theme resources without making the window visible.
            _ = new WindowInteropHelper(window).EnsureHandle();
            window.ApplyTemplate();
            Assert.IsFalse(window.IsVisible);
            Assert.IsInstanceOfType<FrameworkElement>(window.Content);
            var content = (FrameworkElement)window.Content;

            // A never-shown Window hides its descendants. Detach its actual content
            // for offscreen rendering and preserve the inherited window typography.
            window.Content = null;
            var preview = new Border
            {
                Child = content,
                Background = window.Background,
                Resources = window.Resources,
            };
            TextElement.SetForeground(preview, window.Foreground);
            TextElement.SetFontFamily(preview, window.FontFamily);
            TextElement.SetFontSize(preview, window.FontSize);
            PopulatePreview(window);

            var outputDirectory = Path.Combine(FindRepositoryRoot(), "artifacts", "ui-preview");
            Directory.CreateDirectory(outputDirectory);
            var sizes = new[]
            {
                (Name: "standard", Size: new Size(window.Width, window.Height)),
                (Name: "minimum", Size: new Size(window.MinWidth, window.MinHeight)),
            };
            foreach (var (sizeName, size) in sizes)
            {
                foreach (var panelName in PanelNames)
                {
                    foreach (var candidate in PanelNames)
                    {
                        Find<FrameworkElement>(window, candidate).Visibility = candidate == panelName
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    }

                    Find<TextBlock>(window, "StateText").Text = panelName[..^"Panel".Length];
                    preview.Measure(size);
                    preview.Arrange(new Rect(size));
                    preview.UpdateLayout();
                    var controls = GetCriticalControls(panelName);
                    foreach (var controlName in controls)
                    {
                        AssertFitsPreview(Find<FrameworkElement>(window, controlName), preview, size,
                            $"{sizeName}/{panelName}/{controlName}");
                    }

                    Assert.IsFalse(window.IsLoaded, "Offscreen previews must never show or load the window.");
                    Assert.IsTrue(preview.IsMeasureValid);
                    Assert.IsTrue(preview.IsArrangeValid);
                    var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                    image.Render(preview);
                    // The original Windows theme can use a native compositor backdrop;
                    // verify managed content without requiring an opaque WPF background.
                    var stride = (int)size.Width * 4;
                    var pixels = new byte[stride * (int)size.Height];
                    image.CopyPixels(pixels, stride, 0);
                    var hasPaintedContent = false;
                    for (var offset = 3; offset < pixels.Length; offset += 4)
                    {
                        if (pixels[offset] > 0)
                        {
                            hasPaintedContent = true;
                            break;
                        }
                    }

                    Assert.IsTrue(hasPaintedContent, "Actual workflow controls must be rendered.");

                    var path = Path.Combine(outputDirectory, $"{sizeName}-{panelName[..^"Panel".Length].ToLowerInvariant()}.png");
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using (var output = File.Create(path))
                    {
                        encoder.Save(output);
                    }

                    TestContext.AddResultFile(path);
                }
            }
        }
        finally
        {
            application.Shutdown();
        }
    }

    private static void PopulatePreview(MainWindow window)
    {
        Find<TextBlock>(window, "ReviewHeadingText").Text = "Review before shredding";
        Find<TextBlock>(window, "ReviewDescriptionText").Text = "Preview data only. No filesystem targets were selected or checked.";
        Find<TextBlock>(window, "ReviewCountText").Text = "48";
        Find<TextBlock>(window, "ReviewSizeText").Text = "128 MiB";
        Find<TextBlock>(window, "ReviewFileSystemText").Text = "NTFS";
        Find<TextBlock>(window, "ReviewMediumText").Text = "SolidState";
        Find<TextBlock>(window, "ReviewAssuranceText").Text = "BestEffort";
        Find<ListBox>(window, "IssueList").ItemsSource = new[]
        {
            new PreviewIssue("[WARNING] Storage may retain earlier copies of data."),
            new PreviewIssue("[WARNING] Backups and cloud history require separate attention."),
        };
        Find<ListView>(window, "TargetList").ItemsSource = Enumerable.Range(1, 48)
            .Select(static index => new PreviewTarget(
                $"Preview item {index:D2} — a long synthetic label for path overflow", "File", "2.67 MiB", "BestEffort"))
            .ToArray();
        Find<TextBlock>(window, "ConfirmationSummaryText").Text = "48 item(s), 128 MiB, assurance: BestEffort.";
        Find<FrameworkElement>(window, "TypedConfirmationPanel").Visibility = Visibility.Visible;
        Find<TextBox>(window, "TypedConfirmationBox").Text = "SHRE";
        Find<TextBlock>(window, "CurrentItemText").Text = "Preview item 17 — long synthetic filename to check truncation";
        Find<ProgressBar>(window, "OverallProgressBar").Value = 0.36;
        Find<TextBlock>(window, "ProgressSummaryText").Text = "46 MiB of 128 MiB";
        Find<TextBlock>(window, "SpeedText").Text = "12.8 MiB/s";
        Find<TextBlock>(window, "EtaText").Text = "00:00:06";
        Find<TextBlock>(window, "FailureCountText").Text = "1";
        Find<TextBlock>(window, "ResultsSummaryText").Text = "46 shredded · 1 failed · 1 not started";
        Find<ListView>(window, "ResultsList").ItemsSource = new[]
        {
            new PreviewResult("Preview completed item", "Shredded", "—", "2.67 MiB", "—"),
            new PreviewResult("Preview partial failure requiring attention", "OverwrittenNotDeleted", "Delete", "2.67 MiB", "5"),
            new PreviewResult("Preview remaining item", "NotStarted", "—", "0 B", "—"),
        };
    }

    private static string[] GetCriticalControls(string panelName) => panelName switch
    {
        "ReadyPanel" => ["AddFilesButton", "AddFolderButton"],
        "CheckingPanel" => ["CancelCheckButton"],
        "ReviewPanel" => ["TargetList", "ContinueButton"],
        "ConfirmationPanel" => ["TypedConfirmationBox", "ConfirmShredButton"],
        "RevalidatingPanel" => ["RevalidatingPanel"],
        "ShreddingPanel" => ["OverallProgressBar", "StopButton"],
        "ResultsPanel" => ["ResultsList", "RetryButton", "ExportButton", "NewBatchButton"],
        _ => throw new AssertFailedException($"Unknown preview panel: {panelName}"),
    };

    private static void AssertFitsPreview(FrameworkElement control, FrameworkElement preview, Size size, string context)
    {
        Assert.AreEqual(Visibility.Visible, control.Visibility, context);
        Assert.IsGreaterThan(0d, control.ActualWidth, context);
        Assert.IsGreaterThan(0d, control.ActualHeight, context);
        var bounds = control.TransformToAncestor(preview).TransformBounds(new Rect(control.RenderSize));
        Assert.IsGreaterThanOrEqualTo(-1d, bounds.Left, context);
        Assert.IsGreaterThanOrEqualTo(-1d, bounds.Top, context);
        Assert.IsLessThanOrEqualTo(size.Width + 1, bounds.Right, context);
        Assert.IsLessThanOrEqualTo(size.Height + 1, bounds.Bottom, context);
    }

    private static T Find<T>(MainWindow window, string name) where T : FrameworkElement =>
        window.FindName(name) as T
        ?? throw new AssertFailedException($"The actual window must contain {name} as {typeof(T).Name}.");

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Nyx.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Nyx.slnx must be available above the test output to save UI previews.");
    }

    private sealed record PreviewIssue(string DisplayText);
    private sealed record PreviewTarget(string Path, string Kind, string Size, string Assurance);
    private sealed record PreviewResult(string Path, string Status, string FailureStage, string BytesWritten, string ErrorCode);
}
