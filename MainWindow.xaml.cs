using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CaptionClock;

public partial class MainWindow : Window
{
    public ObservableCollection<SubtitleEntry> Captions { get; } = new();
    private readonly ObservableCollection<string> _fontNames = new();
    private readonly DispatcherTimer _timer;
    private bool _updatingSlider;
    private bool _hasVideo;
    private bool _isPlaying;
    private bool _loadingStyleControls;
    private bool _draggingCaption;
    private string? _subtitlePath;
    private string? _videoPath;
    private string? _projectPath;
    private Point _dragOffset;
    private readonly Dictionary<string, FontFamily> _customFontFamilies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _customFontPaths = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex TimestampLine = new(
        @"^\s*(?<start>(?:\d{2}:)?\d{2}:\d{2}[,.]\d{3})\s*-->\s*(?<end>(?:\d{2}:)?\d{2}:\d{2}[,.]\d{3})(?:\s+.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public MainWindow()
    {
        InitializeComponent();
        CaptionGrid.ItemsSource = Captions;
        foreach (var name in Fonts.SystemFontFamilies.Select(f => f.Source).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n))
            _fontNames.Add(name);
        FontCombo.ItemsSource = _fontNames;
        FontCombo.SelectedItem = _fontNames.Contains("Segoe UI") ? "Segoe UI" : _fontNames.FirstOrDefault();
        UpdateCaptionPreview();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => UpdatePlaybackPosition();
        _timer.Start();
    }

    private void OpenVideo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a video file",
            Filter = "Video files|*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.webm|All files|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        OpenVideoPath(dialog.FileName);
    }

    private void OpenVideoPath(string path)
    {
        try
        {
            VideoPlayer.Stop();
            VideoPlayer.Source = new Uri(path);
            _videoPath = path;
            VideoLabel.Text = System.IO.Path.GetFileName(path);
            _hasVideo = true;
            _isPlaying = false;
            PlayButton.Content = "Play";
            StatusLabel.Text = "Video loaded. Use the playhead and Set Start / Set End controls to time captions.";
        }
        catch (Exception ex)
        {
            _hasVideo = false;
            MessageBox.Show($"Could not open that video.\n\n{ex.Message}", "CaptionClock", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (VideoPlayer.NaturalDuration.HasTimeSpan)
        {
            SeekSlider.Maximum = Math.Max(0.1, VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds);
            DurationLabel.Text = $"/ {FormatClock(VideoPlayer.NaturalDuration.TimeSpan)}";
            UpdateCaptionPreview();
        }
    }

    private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        VideoPlayer.Pause();
        _isPlaying = false;
        PlayButton.Content = "Play";
        UpdateCaptionPreview();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasVideo)
        {
            MessageBox.Show("Open a video first to use playback controls.", "CaptionClock", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (PlayButton.Content?.ToString() == "Pause")
        {
            VideoPlayer.Pause();
            _isPlaying = false;
            PlayButton.Content = "Play";
        }
        else
        {
            VideoPlayer.Play();
            _isPlaying = true;
            PlayButton.Content = "Pause";
        }
        UpdateCaptionPreview();
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider || !_hasVideo || !VideoPlayer.NaturalDuration.HasTimeSpan) return;
        try { VideoPlayer.Position = TimeSpan.FromSeconds(SeekSlider.Value); }
        catch { /* The media source may still be opening. */ }
    }

    private void UpdatePlaybackPosition()
    {
        if (!_hasVideo) return;
        try
        {
            var position = VideoPlayer.Position;
            _updatingSlider = true;
            SeekSlider.Value = Math.Clamp(position.TotalSeconds, SeekSlider.Minimum, SeekSlider.Maximum);
            CurrentTimeLabel.Text = FormatClock(position);
            UpdateCaptionPreview();
        }
        catch { }
        finally { _updatingSlider = false; }
    }

    private void OpenSubtitles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open subtitles or CaptionClock project",
            Filter = "CaptionClock project or subtitles|*.ccproj;*.srt;*.vtt|CaptionClock project (*.ccproj)|*.ccproj|SubRip (*.srt)|*.srt|WebVTT (*.vtt)|*.vtt|All files|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            if (System.IO.Path.GetExtension(dialog.FileName).Equals(".ccproj", StringComparison.OrdinalIgnoreCase))
            {
                LoadProject(dialog.FileName);
                return;
            }
            var loaded = ParseSubtitles(File.ReadAllText(dialog.FileName));
            Captions.Clear();
            foreach (var caption in loaded) { TrackCaption(caption); Captions.Add(caption); }
            _subtitlePath = dialog.FileName;
            _projectPath = null;
            Title = $"CaptionClock — {System.IO.Path.GetFileName(dialog.FileName)}";
            StatusLabel.Text = $"Loaded {Captions.Count} caption(s) from {System.IO.Path.GetFileName(dialog.FileName)}.";
            UpdateCaptionPreview();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read that subtitle file.\n\n{ex.Message}", "CaptionClock", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static List<SubtitleEntry> ParseSubtitles(string contents)
    {
        var lines = contents.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var result = new List<SubtitleEntry>();
        for (var i = 0; i < lines.Length; i++)
        {
            var match = TimestampLine.Match(lines[i]);
            if (!match.Success) continue;
            if (!TryParseClock(match.Groups["start"].Value, out var start) ||
                !TryParseClock(match.Groups["end"].Value, out var end)) continue;

            var text = new List<string>();
            for (i++; i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]); i++) text.Add(lines[i]);
            var body = string.Join(Environment.NewLine, text);
            var bold = Regex.IsMatch(body, "<b>", RegexOptions.IgnoreCase);
            var italic = Regex.IsMatch(body, "<i>", RegexOptions.IgnoreCase);
            var underline = Regex.IsMatch(body, "<u>", RegexOptions.IgnoreCase);
            body = Regex.Replace(body, "</?(b|i|u)>", string.Empty, RegexOptions.IgnoreCase);
            body = WebUtility.HtmlDecode(body);

            var entry = new SubtitleEntry
            {
                Start = FormatTimestamp(start), End = FormatTimestamp(end), Text = body,
                Bold = bold, Italic = italic, Underline = underline
            };
            var position = Regex.Match(match.Value, @"\bposition:(?<x>\d{1,3}(?:\.\d+)?)%", RegexOptions.IgnoreCase);
            var line = Regex.Match(match.Value, @"\bline:(?<y>\d{1,3}(?:\.\d+)?)%", RegexOptions.IgnoreCase);
            if (double.TryParse(position.Groups["x"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) entry.X = Math.Clamp(x / 100, 0, 1);
            if (double.TryParse(line.Groups["y"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) entry.Y = Math.Clamp(y / 100, 0, 1);
            var align = Regex.Match(match.Value, @"\balign:(?<a>start|left|center|right|end)\b", RegexOptions.IgnoreCase);
            if (align.Success) entry.Alignment = align.Groups["a"].Value.ToLowerInvariant() switch
            {
                "left" or "start" => "Left",
                "right" or "end" => "Right",
                _ => "Center"
            };
            result.Add(entry);
        }
        return result;
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        CaptionGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        CaptionGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!ValidateCaptions()) return;

        var dialog = new SaveFileDialog
        {
            Title = "Save project or export subtitles",
            Filter = "CaptionClock project (*.ccproj)|*.ccproj|SubRip (*.srt)|*.srt|WebVTT (*.vtt)|*.vtt",
            DefaultExt = ".ccproj",
            AddExtension = true,
            FileName = _projectPath is not null ? System.IO.Path.GetFileName(_projectPath) :
                _subtitlePath is null ? "captions.ccproj" : System.IO.Path.GetFileNameWithoutExtension(_subtitlePath) + ".ccproj"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (System.IO.Path.GetExtension(dialog.FileName).Equals(".ccproj", StringComparison.OrdinalIgnoreCase))
            {
                SaveProject(dialog.FileName);
                _projectPath = dialog.FileName;
                Title = $"CaptionClock — {System.IO.Path.GetFileName(dialog.FileName)}";
                StatusLabel.Text = $"Saved project with {Captions.Count} caption(s). Project styling is preserved.";
                return;
            }

            File.WriteAllText(dialog.FileName, BuildSubtitleText(dialog.FileName), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _subtitlePath = dialog.FileName;
            Title = $"CaptionClock — {System.IO.Path.GetFileName(dialog.FileName)}";
            StatusLabel.Text = $"Exported {Captions.Count} caption(s). Emphasis is included; VTT also includes placement. Use .ccproj to preserve fonts, outlines, and exact styling.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save the subtitle file.\n\n{ex.Message}", "CaptionClock", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveProject(string projectPath)
    {
        var projectDirectory = System.IO.Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory;
        var oldProjectDirectory = _projectPath is null ? projectDirectory :
            System.IO.Path.GetDirectoryName(_projectPath) ?? projectDirectory;
        var fontFolderName = System.IO.Path.GetFileNameWithoutExtension(projectPath) + "_fonts";
        var fontFolderPath = System.IO.Path.Combine(projectDirectory, fontFolderName);

        foreach (var caption in Captions)
        {
            if (string.IsNullOrWhiteSpace(caption.FontPath)) continue;
            var sourcePath = System.IO.Path.IsPathRooted(caption.FontPath)
                ? caption.FontPath
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(oldProjectDirectory, caption.FontPath));
            if (!File.Exists(sourcePath)) continue;

            Directory.CreateDirectory(fontFolderPath);
            var safeFamilyName = Regex.Replace(caption.FontFamily, @"[^A-Za-z0-9_-]+", "_");
            var fontFileName = safeFamilyName + System.IO.Path.GetExtension(sourcePath);
            var destinationPath = System.IO.Path.Combine(fontFolderPath, fontFileName);
            if (!System.IO.Path.GetFullPath(sourcePath).Equals(System.IO.Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
                File.Copy(sourcePath, destinationPath, overwrite: true);
            caption.FontPath = System.IO.Path.Combine(fontFolderName, fontFileName);
            _customFontPaths[caption.FontFamily] = destinationPath;
            _customFontFamilies[caption.FontFamily] = CreateImportedFontFamily(destinationPath, caption.FontFamily);
        }

        var document = new CaptionClockProject
        {
            Version = 1,
            VideoPath = _videoPath,
            Captions = Captions.ToList()
        };
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(projectPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void LoadProject(string projectPath)
    {
        var document = JsonSerializer.Deserialize<CaptionClockProject>(File.ReadAllText(projectPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("This is not a valid CaptionClock project file.");

        Captions.Clear();
        var projectDirectory = System.IO.Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory;
        foreach (var caption in document.Captions ?? new List<SubtitleEntry>())
        {
            if (!string.IsNullOrWhiteSpace(caption.FontPath))
            {
                var fontPath = System.IO.Path.IsPathRooted(caption.FontPath)
                    ? caption.FontPath
                    : System.IO.Path.GetFullPath(System.IO.Path.Combine(projectDirectory, caption.FontPath));
                if (File.Exists(fontPath))
                {
                    caption.FontPath = fontPath;
                    try
                    {
                        _customFontFamilies[caption.FontFamily] = CreateImportedFontFamily(fontPath, caption.FontFamily);
                        _customFontPaths[caption.FontFamily] = fontPath;
                        AddFontChoice(caption.FontFamily);
                    }
                    catch { /* Use WPF's normal font fallback if an imported font is unavailable. */ }
                }
            }
            TrackCaption(caption);
            Captions.Add(caption);
        }

        _projectPath = projectPath;
        _subtitlePath = null;
        Title = $"CaptionClock — {System.IO.Path.GetFileName(projectPath)}";
        if (!string.IsNullOrWhiteSpace(document.VideoPath) && File.Exists(document.VideoPath)) OpenVideoPath(document.VideoPath);
        else if (!string.IsNullOrWhiteSpace(document.VideoPath)) StatusLabel.Text = "Project opened. Its saved video path is unavailable; open the video file again.";
        else StatusLabel.Text = $"Project opened with {Captions.Count} caption(s).";
        if (CaptionGrid.Items.Count > 0) CaptionGrid.SelectedIndex = 0;
        UpdateCaptionPreview();
    }

    private void ImportFont_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a font file",
            Filter = "Font files|*.ttf;*.otf|TrueType font (*.ttf)|*.ttf|OpenType font (*.otf)|*.otf"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var fullPath = System.IO.Path.GetFullPath(dialog.FileName);
            var glyphTypeface = new GlyphTypeface(new Uri(fullPath));
            var familyName = glyphTypeface.Win32FamilyNames.Values.FirstOrDefault()
                ?? System.IO.Path.GetFileNameWithoutExtension(fullPath);
            var family = CreateImportedFontFamily(fullPath, familyName);
            _customFontFamilies[familyName] = family;
            _customFontPaths[familyName] = fullPath;
            AddFontChoice(familyName);
            FontCombo.SelectedItem = familyName;
            StatusLabel.Text = $"Imported font: {familyName}. Save a .ccproj project to copy the font beside the project.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not load that font file. Use a valid .ttf or .otf font.\n\n{ex.Message}", "CaptionClock", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddFontChoice(string name)
    {
        if (!_fontNames.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))) _fontNames.Add(name);
    }

    private void TrackCaption(SubtitleEntry caption)
    {
        caption.PropertyChanged += (_, _) =>
        {
            if (ReferenceEquals(CaptionGrid.SelectedItem, caption) || ReferenceEquals(GetPreviewCaption(), caption))
                UpdateCaptionPreview();
        };
    }

    private static FontFamily CreateImportedFontFamily(string fontPath, string familyName)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(fontPath))
            ?? throw new InvalidOperationException("The font file has no parent directory.");
        var baseUri = new Uri(directory + System.IO.Path.DirectorySeparatorChar);
        return new FontFamily(baseUri, $"./#{familyName}");
    }

    private FontFamily ResolveFontFamily(SubtitleEntry caption)
    {
        if (_customFontFamilies.TryGetValue(caption.FontFamily, out var imported)) return imported;
        if (!string.IsNullOrWhiteSpace(caption.FontPath) && File.Exists(caption.FontPath))
        {
            try
            {
                var family = CreateImportedFontFamily(caption.FontPath, caption.FontFamily);
                _customFontFamilies[caption.FontFamily] = family;
                return family;
            }
            catch { }
        }
        try { return new FontFamily(caption.FontFamily); }
        catch { return new FontFamily("Segoe UI"); }
    }

    private void StyleFont_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyStyleFromControls();
    private void StyleOption_Click(object sender, RoutedEventArgs e) => ApplyStyleFromControls();
    private void StyleAlignment_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyStyleFromControls();
    private void StyleOutline_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyStyleFromControls();
    private void OutlineWidth_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyStyleFromControls();

    private void FontSizeBox_LostFocus(object sender, RoutedEventArgs e) => ApplyStyleFromControls();

    private void FontSizeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyStyleFromControls();
            e.Handled = true;
        }
    }

    private void ApplyStyleFromControls()
    {
        if (_loadingStyleControls || CaptionGrid?.SelectedItem is not SubtitleEntry caption || FontCombo is null) return;
        var fontName = FontCombo.SelectedItem?.ToString();
        if (!string.IsNullOrWhiteSpace(fontName)) caption.FontFamily = fontName;
        if (double.TryParse(FontSizeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var size))
        {
            caption.FontSize = Math.Clamp(size, 8, 160);
            FontSizeBox.Text = caption.FontSize.ToString("0.#", CultureInfo.InvariantCulture);
        }
        else FontSizeBox.Text = caption.FontSize.ToString("0.#", CultureInfo.InvariantCulture);
        caption.Bold = BoldCheck.IsChecked == true;
        caption.Italic = ItalicCheck.IsChecked == true;
        caption.Underline = UnderlineCheck.IsChecked == true;
        caption.Alignment = (AlignmentCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Center";
        caption.OutlineColor = (OutlineCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Black";
        caption.OutlineWidth = OutlineWidthSlider.Value;
        caption.FontPath = fontName is not null && _customFontPaths.TryGetValue(fontName, out var path) ? path : null;
        UpdateCaptionPreview();
    }

    private void LoadStyleControls(SubtitleEntry? caption)
    {
        if (caption is null) return;
        _loadingStyleControls = true;
        try
        {
            AddFontChoice(caption.FontFamily);
            FontCombo.SelectedItem = caption.FontFamily;
            FontSizeBox.Text = caption.FontSize.ToString("0.#", CultureInfo.InvariantCulture);
            BoldCheck.IsChecked = caption.Bold;
            ItalicCheck.IsChecked = caption.Italic;
            UnderlineCheck.IsChecked = caption.Underline;
            AlignmentCombo.SelectedIndex = caption.Alignment switch { "Left" => 0, "Right" => 2, _ => 1 };
            OutlineCombo.SelectedIndex = caption.OutlineColor.Equals("White", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            OutlineWidthSlider.Value = Math.Clamp(caption.OutlineWidth, 0, 6);
        }
        finally { _loadingStyleControls = false; }
    }

    private SubtitleEntry? GetPreviewCaption()
    {
        if (_isPlaying)
        {
            var position = CurrentPosition();
            return Captions.FirstOrDefault(c => TryParseClock(c.Start, out var start) && TryParseClock(c.End, out var end) && position >= start && position < end);
        }
        return CaptionGrid.SelectedItem as SubtitleEntry;
    }

    private void UpdateCaptionPreview()
    {
        if (CaptionPreview is null || CaptionOverlay is null) return;
        var caption = GetPreviewCaption();
        if (caption is null || string.IsNullOrWhiteSpace(caption.Text) || CaptionOverlay.ActualWidth < 10 || CaptionOverlay.ActualHeight < 10)
        {
            CaptionPreview.Visibility = Visibility.Collapsed;
            return;
        }

        CaptionPreview.Text = caption.Text;
        CaptionPreview.FontFamily = ResolveFontFamily(caption);
        CaptionPreview.FontSize = caption.FontSize;
        CaptionPreview.FontWeight = caption.Bold ? FontWeights.Bold : FontWeights.Normal;
        CaptionPreview.FontStyle = caption.Italic ? FontStyles.Italic : FontStyles.Normal;
        CaptionPreview.Underline = caption.Underline;
        CaptionPreview.TextAlignment = caption.Alignment switch
        {
            "Left" => TextAlignment.Left,
            "Right" => TextAlignment.Right,
            _ => TextAlignment.Center
        };
        CaptionPreview.Foreground = Brushes.White;
        CaptionPreview.OutlineBrush = caption.OutlineColor.Equals("White", StringComparison.OrdinalIgnoreCase) ? Brushes.White : Brushes.Black;
        CaptionPreview.OutlineThickness = caption.OutlineWidth;
        CaptionPreview.Width = Math.Max(1, CaptionOverlay.ActualWidth * 0.88);
        CaptionPreview.Measure(new Size(CaptionPreview.Width, Math.Max(1, CaptionOverlay.ActualHeight)));
        var left = caption.X * CaptionOverlay.ActualWidth - CaptionPreview.Width / 2;
        var top = caption.Y * CaptionOverlay.ActualHeight - CaptionPreview.DesiredSize.Height / 2;
        Canvas.SetLeft(CaptionPreview, left);
        Canvas.SetTop(CaptionPreview, top);
        CaptionPreview.Visibility = Visibility.Visible;
    }

    private void CaptionOverlay_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCaptionPreview();

    private void CaptionPreview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var caption = GetPreviewCaption();
        if (caption is null) return;
        if (_isPlaying)
        {
            VideoPlayer.Pause();
            _isPlaying = false;
            PlayButton.Content = "Play";
        }
        CaptionGrid.SelectedItem = caption;
        _dragOffset = e.GetPosition(CaptionPreview);
        _draggingCaption = true;
        CaptionPreview.CaptureMouse();
        e.Handled = true;
    }

    private void CaptionPreview_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingCaption || CaptionGrid.SelectedItem is not SubtitleEntry caption) return;
        var pointer = e.GetPosition(CaptionOverlay);
        var width = CaptionOverlay.ActualWidth;
        var height = CaptionOverlay.ActualHeight;
        var left = pointer.X - _dragOffset.X;
        var top = pointer.Y - _dragOffset.Y;
        caption.X = Math.Clamp((left + CaptionPreview.Width / 2) / width, 0, 1);
        caption.Y = Math.Clamp((top + CaptionPreview.ActualHeight / 2) / height, 0, 1);
        Canvas.SetLeft(CaptionPreview, left);
        Canvas.SetTop(CaptionPreview, top);
        e.Handled = true;
    }

    private void CaptionPreview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingCaption) return;
        _draggingCaption = false;
        CaptionPreview.ReleaseMouseCapture();
        StatusLabel.Text = "Caption position updated. Save as a .ccproj project to preserve it.";
        e.Handled = true;
    }

    private bool ValidateCaptions()
    {
        for (var i = 0; i < Captions.Count; i++)
        {
            var caption = Captions[i];
            if (!TryParseClock(caption.Start, out var start) || !TryParseClock(caption.End, out var end) || end <= start)
            {
                CaptionGrid.SelectedIndex = i;
                CaptionGrid.ScrollIntoView(caption);
                MessageBox.Show($"Caption {i + 1} has an invalid time range. Use HH:MM:SS,mmm (or MM:SS,mmm), and make sure End is after Start.",
                    "Check caption timing", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
        return true;
    }

    private string BuildSubtitleText(string path)
    {
        var isVtt = System.IO.Path.GetExtension(path).Equals(".vtt", StringComparison.OrdinalIgnoreCase);
        var ordered = Captions.OrderBy(c => TryParseClock(c.Start, out var t) ? t : TimeSpan.Zero).ToList();
        var output = new StringBuilder();
        if (isVtt) output.AppendLine("WEBVTT").AppendLine();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (!isVtt) output.AppendLine((i + 1).ToString(CultureInfo.InvariantCulture));
            var start = TryParseClock(ordered[i].Start, out var s) ? FormatTimestamp(s) : "00:00:00,000";
            var end = TryParseClock(ordered[i].End, out var e) ? FormatTimestamp(e) : "00:00:00,000";
            output.Append(start.Replace(',', isVtt ? '.' : ',')).Append(" --> ")
                .Append(end.Replace(',', isVtt ? '.' : ','));
            if (isVtt)
            {
                var align = ordered[i].Alignment switch { "Left" => "start", "Right" => "end", _ => "center" };
                output.Append($" position:{ordered[i].X * 100:0.##}% line:{ordered[i].Y * 100:0.##}% align:{align}");
            }
            output.AppendLine();

            var text = ordered[i].Text ?? string.Empty;
            if (isVtt) text = WebUtility.HtmlEncode(text);
            if (ordered[i].Underline) text = $"<u>{text}</u>";
            if (ordered[i].Italic) text = $"<i>{text}</i>";
            if (ordered[i].Bold) text = $"<b>{text}</b>";
            output.AppendLine(text).AppendLine();
        }
        return output.ToString();
    }

    private void AddCaption_Click(object sender, RoutedEventArgs e)
    {
        var start = _hasVideo ? VideoPlayer.Position : TimeSpan.Zero;
        var item = new SubtitleEntry
        {
            Start = FormatTimestamp(start),
            End = FormatTimestamp(start + TimeSpan.FromSeconds(2.5)),
            Text = "New caption",
            FontFamily = FontCombo.SelectedItem?.ToString() ?? "Segoe UI"
        };
        if (_customFontPaths.TryGetValue(item.FontFamily, out var fontPath)) item.FontPath = fontPath;
        TrackCaption(item);
        Captions.Add(item);
        CaptionGrid.SelectedItem = item;
        CaptionGrid.ScrollIntoView(item);
        CaptionGrid.BeginEdit();
        StatusLabel.Text = "Caption added. Edit its text and adjust the timing.";
    }

    private void DeleteCaption_Click(object sender, RoutedEventArgs e)
    {
        if (CaptionGrid.SelectedItem is not SubtitleEntry item) return;
        var index = Captions.IndexOf(item);
        Captions.Remove(item);
        if (Captions.Count > 0) CaptionGrid.SelectedIndex = Math.Clamp(index, 0, Captions.Count - 1);
        else UpdateCaptionPreview();
        StatusLabel.Text = "Selected caption deleted.";
    }

    private void CaptionGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LoadStyleControls(CaptionGrid.SelectedItem as SubtitleEntry);
        if (CaptionGrid.SelectedItem is SubtitleEntry item && _hasVideo && TryParseClock(item.Start, out var start))
        {
            try { VideoPlayer.Position = start; }
            catch { }
        }
        UpdateCaptionPreview();
    }

    private void SetStart_Click(object sender, RoutedEventArgs e)
    {
        if (CaptionGrid.SelectedItem is not SubtitleEntry item) { TellSelectCaption(); return; }
        item.Start = FormatTimestamp(CurrentPosition());
        UpdateCaptionPreview();
        StatusLabel.Text = "Selected caption start set to the playhead.";
    }

    private void SetEnd_Click(object sender, RoutedEventArgs e)
    {
        if (CaptionGrid.SelectedItem is not SubtitleEntry item) { TellSelectCaption(); return; }
        item.End = FormatTimestamp(CurrentPosition());
        UpdateCaptionPreview();
        StatusLabel.Text = "Selected caption end set to the playhead.";
    }

    private void NudgeEarlier_Click(object sender, RoutedEventArgs e) => NudgeSelected(-100);
    private void NudgeLater_Click(object sender, RoutedEventArgs e) => NudgeSelected(100);

    private void NudgeSelected(int milliseconds)
    {
        if (CaptionGrid.SelectedItem is not SubtitleEntry item) { TellSelectCaption(); return; }
        if (!TryParseClock(item.Start, out var start) || !TryParseClock(item.End, out var end))
        {
            MessageBox.Show("Fix the selected caption's timecodes before nudging it.", "CaptionClock", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var offset = TimeSpan.FromMilliseconds(milliseconds);
        if (start + offset < TimeSpan.Zero) { StatusLabel.Text = "A caption cannot start before 00:00:00,000."; return; }
        item.Start = FormatTimestamp(start + offset);
        item.End = FormatTimestamp(end + offset);
        UpdateCaptionPreview();
        StatusLabel.Text = $"Selected caption shifted by {milliseconds:+#;-#;0} ms.";
    }

    private TimeSpan CurrentPosition()
    {
        if (_hasVideo)
        {
            try { return VideoPlayer.Position; }
            catch { }
        }
        return TimeSpan.FromSeconds(SeekSlider.Value);
    }

    private void TellSelectCaption() => StatusLabel.Text = "Select a caption row first.";

    private static bool TryParseClock(string? value, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim().Replace(',', '.');
        var parts = normalized.Split(':');
        if (parts.Length is not (2 or 3)) return false;
        var secondsParts = parts[^1].Split('.');
        if (secondsParts.Length != 2 || secondsParts[1].Length != 3 ||
            !int.TryParse(secondsParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            !int.TryParse(secondsParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)) return false;
        var minuteIndex = parts.Length - 2;
        if (!int.TryParse(parts[minuteIndex], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first)) return false;
        var hours = parts.Length == 3 ? first : 0;
        var totalMinutes = parts.Length == 3 ? minutes : first;
        if (hours < 0 || totalMinutes < 0 || seconds < 0 || seconds > 59 || milliseconds < 0 || milliseconds > 999) return false;
        result = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(totalMinutes) + TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(milliseconds);
        return true;
    }

    private static string FormatTimestamp(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}";

    private static string FormatClock(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";
}

public sealed class SubtitleEntry : System.ComponentModel.INotifyPropertyChanged
{
    private string _start = "00:00:00,000";
    private string _end = "00:00:02,500";
    private string _text = string.Empty;
    private string _fontFamily = "Segoe UI";
    private double _fontSize = 42;
    private bool _bold;
    private bool _italic;
    private bool _underline;
    private string _alignment = "Center";
    private string _outlineColor = "Black";
    private double _outlineWidth = 2.5;
    private double _x = 0.5;
    private double _y = 0.86;
    private string? _fontPath;

    public string Start { get => _start; set { if (_start != value) { _start = value; OnChanged(nameof(Start)); } } }
    public string End { get => _end; set { if (_end != value) { _end = value; OnChanged(nameof(End)); } } }
    public string Text { get => _text; set { if (_text != value) { _text = value; OnChanged(nameof(Text)); } } }
    public string FontFamily { get => _fontFamily; set { if (_fontFamily != value) { _fontFamily = value; OnChanged(nameof(FontFamily)); } } }
    public double FontSize { get => _fontSize; set { if (Math.Abs(_fontSize - value) > 0.001) { _fontSize = value; OnChanged(nameof(FontSize)); } } }
    public bool Bold { get => _bold; set { if (_bold != value) { _bold = value; OnChanged(nameof(Bold)); } } }
    public bool Italic { get => _italic; set { if (_italic != value) { _italic = value; OnChanged(nameof(Italic)); } } }
    public bool Underline { get => _underline; set { if (_underline != value) { _underline = value; OnChanged(nameof(Underline)); } } }
    public string Alignment { get => _alignment; set { if (_alignment != value) { _alignment = value; OnChanged(nameof(Alignment)); } } }
    public string OutlineColor { get => _outlineColor; set { if (_outlineColor != value) { _outlineColor = value; OnChanged(nameof(OutlineColor)); } } }
    public double OutlineWidth { get => _outlineWidth; set { if (Math.Abs(_outlineWidth - value) > 0.001) { _outlineWidth = value; OnChanged(nameof(OutlineWidth)); } } }
    public double X { get => _x; set { var normalized = Math.Clamp(value, 0, 1); if (Math.Abs(_x - normalized) > 0.0001) { _x = normalized; OnChanged(nameof(X)); } } }
    public double Y { get => _y; set { var normalized = Math.Clamp(value, 0, 1); if (Math.Abs(_y - normalized) > 0.0001) { _y = normalized; OnChanged(nameof(Y)); } } }
    public string? FontPath { get => _fontPath; set { if (_fontPath != value) { _fontPath = value; OnChanged(nameof(FontPath)); } } }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

public sealed class CaptionClockProject
{
    public int Version { get; set; } = 1;
    public string? VideoPath { get; set; }
    public List<SubtitleEntry> Captions { get; set; } = new();
}
