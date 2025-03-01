using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Media;
using System.Diagnostics;
using System.Text.Json;
using System.IO;

namespace VideoCutter;

public partial class MainWindow : Window
{
    private bool isPlaying = false;
    private DispatcherTimer timer;
    private bool isDragging = false;
    private readonly TimeSpan frameStep = TimeSpan.FromMilliseconds(33); // Approximately 1/30th of a second
    private readonly TimeSpan secondStep = TimeSpan.FromSeconds(1); // One second step
    private TimeSpan? inPoint = null;
    private TimeSpan? outPoint = null;
    private bool isShiftPressed = false;
    private string? lastTransformation = null; // Track the last transformation applied
    private string? currentVideoPath = null; // Track the current video path
    private bool isFFmpegAvailable = false;
    private bool isNvencAvailable = false; // Track NVENC availability
    private int initialRotation = 0; // Store the initial rotation from metadata

    private Transform InitialRotationTransform
    {
        get
        {
            // Convert the rotation value to a WPF transform
            // Note: WPF rotations are clockwise, while video metadata rotations are counterclockwise
            // So we negate the rotation value
            return new RotateTransform(-initialRotation);
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        playPauseButton.IsEnabled = false;
        lastFrameButton.IsEnabled = false;
        nextFrameButton.IsEnabled = false;
        makeInButton.IsEnabled = false;
        goToInButton.IsEnabled = false;
        makeOutButton.IsEnabled = false;
        goToOutButton.IsEnabled = false;
        exportButton.IsEnabled = false;

        // Initialize timer for progress updates
        timer = new DispatcherTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100); // Update every 100ms
        timer.Tick += Timer_Tick;

        // Initialize MediaElement with LayoutTransform
        mediaPlayer.LayoutTransform = Transform.Identity;

        // Check FFmpeg and NVENC availability
        CheckFFmpegAvailability();
    }

    private void CheckFFmpegAvailability()
    {
        try
        {
            // Check ffmpeg and NVENC support
            using var ffmpegProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-encoders",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            ffmpegProcess.Start();
            var output = ffmpegProcess.StandardOutput.ReadToEnd();
            ffmpegProcess.WaitForExit();

            // Check for NVENC support
            isNvencAvailable = output.Contains("h264_nvenc") || output.Contains("hevc_nvenc");

            // Check ffprobe
            using var ffprobeProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            ffprobeProcess.Start();
            ffprobeProcess.WaitForExit();

            isFFmpegAvailable = true;

            // Log NVENC status
            using (var logFile = File.AppendText("ffmpeg-output.log"))
            {
                logFile.WriteLine($"NVENC Support: {(isNvencAvailable ? "Available" : "Not Available")}");
                if (isNvencAvailable)
                {
                    logFile.WriteLine("Hardware acceleration will be used for video encoding.");
                }
            }
        }
        catch (Exception)
        {
            isFFmpegAvailable = false;
            isNvencAvailable = false;
            MessageBox.Show(
                "FFmpeg is not installed or not available in the system PATH. Video export functionality will be disabled.\n\n" +
                "To enable video export:\n" +
                "1. Download FFmpeg from https://ffmpeg.org/download.html\n" +
                "2. Add FFmpeg to your system PATH\n" +
                "3. Restart the application",
                "FFmpeg Not Found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // Handle Ctrl+O for opening video
        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenButton_Click(openButton, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Handle spacebar for play/pause
        if (e.Key == Key.Space)
        {
            // Only handle spacebar if play/pause button is enabled
            if (playPauseButton.IsEnabled)
            {
                PlayPauseButton_Click(playPauseButton, new RoutedEventArgs());
                e.Handled = true;
            }

            return;
        }

        // Update shift state
        if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
        {
            isShiftPressed = true;
            UpdateNavigationButtons();
            e.Handled = true;
            return;
        }

        // Handle arrow keys for navigation
        if (mediaPlayer.Source != null)
        {
            switch (e.Key)
            {
                case Key.Left when lastFrameButton.IsEnabled:
                    LastFrame_Click(lastFrameButton, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.Right when nextFrameButton.IsEnabled:
                    NextFrame_Click(nextFrameButton, new RoutedEventArgs());
                    e.Handled = true;
                    break;
            }
        }
    }

    private void Window_KeyUp(object sender, KeyEventArgs e)
    {
        // Update shift state
        if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
        {
            isShiftPressed = false;
            UpdateNavigationButtons();
            e.Handled = true;
        }
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new OpenFileDialog
        {
            Filter = "Video files (*.mp4;*.avi;*.mkv)|*.mp4;*.avi;*.mkv|All files (*.*)|*.*"
        };

        if (openFileDialog.ShowDialog() == true)
        {
            try
            {
                // Get initial rotation before setting up video
                initialRotation = await GetVideoRotation(openFileDialog.FileName);

                // Apply initial rotation transform
                mediaPlayer.LayoutTransform = InitialRotationTransform;
                lastTransformation = null;

                currentVideoPath = openFileDialog.FileName;
                mediaPlayer.Source = new Uri(currentVideoPath);
                mediaPlayer.Play();
                isPlaying = true;
                UpdatePlayPauseButton();
                timer.Start();
                UpdateNavigationButtons();
                exportButton.IsEnabled = isFFmpegAvailable;

                // Log the initial rotation
                using (var logFile = File.AppendText("ffmpeg-output.log"))
                {
                    logFile.WriteLine($"Video loaded: {currentVideoPath}");
                    logFile.WriteLine($"Initial rotation from metadata: {initialRotation} degrees");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening video: {ex.Message}", "Error", MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (mediaPlayer.Source == null) return;

        if (isPlaying)
        {
            mediaPlayer.Pause();
            isPlaying = false;
            timer.Stop();
        }
        else
        {
            mediaPlayer.Play();
            isPlaying = true;
            timer.Start();
        }

        UpdatePlayPauseButton();
        UpdateNavigationButtons();
    }

    private void NextFrame_Click(object sender, RoutedEventArgs e)
    {
        if (mediaPlayer.Source == null) return;

        var step = isShiftPressed ? frameStep : secondStep;
        var newPosition = mediaPlayer.Position + step;
        if (newPosition <= mediaPlayer.NaturalDuration.TimeSpan)
        {
            mediaPlayer.Position = newPosition;
            UpdateProgress();
            UpdateNavigationButtons();
        }
    }

    private void LastFrame_Click(object sender, RoutedEventArgs e)
    {
        if (mediaPlayer.Source == null) return;

        var step = isShiftPressed ? frameStep : secondStep;
        var newPosition = mediaPlayer.Position - step;
        if (newPosition >= TimeSpan.Zero)
        {
            mediaPlayer.Position = newPosition;
            UpdateProgress();
            UpdateNavigationButtons();
        }
    }

    private void MediaPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        playPauseButton.IsEnabled = true;
        makeInButton.IsEnabled = true;
        makeOutButton.IsEnabled = true;
        UpdatePlayPauseButton();
        UpdateTotalDuration();
        UpdateNavigationButtons();
        UpdateInPointDisplay();
        UpdateOutPointDisplay();
    }

    private void MediaPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        mediaPlayer.Position = TimeSpan.Zero;
        mediaPlayer.Pause();
        isPlaying = false;
        timer.Stop();
        UpdatePlayPauseButton();
        UpdateProgress();
        UpdateNavigationButtons();
    }

    private void Timer_Tick(object sender, EventArgs e)
    {
        if (!isDragging)
        {
            UpdateProgress();
            UpdateNavigationButtons();
        }
    }

    private void ProgressOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (mediaPlayer.NaturalDuration.HasTimeSpan)
        {
            // Calculate the position based on click position
            var clickPosition = e.GetPosition(progressOverlay);
            var ratio = clickPosition.X / progressOverlay.ActualWidth;
            var newPosition = TimeSpan.FromMilliseconds(mediaPlayer.NaturalDuration.TimeSpan.TotalMilliseconds * ratio);

            // Set the new position
            mediaPlayer.Position = newPosition;
            UpdateProgress();
            UpdateNavigationButtons();
        }
    }

    private void UpdateNavigationButtons()
    {
        if (!mediaPlayer.NaturalDuration.HasTimeSpan)
        {
            nextFrameButton.IsEnabled = false;
            lastFrameButton.IsEnabled = false;
            return;
        }

        var step = isShiftPressed ? frameStep : secondStep;

        // Update button text based on mode
        nextFrameButton.Content = isShiftPressed ? "Frame ▶" : "+1 sec";
        lastFrameButton.Content = isShiftPressed ? "◀ Frame" : "-1 sec";

        // Enable/disable based on position
        nextFrameButton.IsEnabled = mediaPlayer.Position + step <= mediaPlayer.NaturalDuration.TimeSpan;
        lastFrameButton.IsEnabled = mediaPlayer.Position - step >= TimeSpan.Zero;
    }

    private void UpdateProgress()
    {
        if (mediaPlayer.NaturalDuration.HasTimeSpan)
        {
            var progress = (mediaPlayer.Position.TotalMilliseconds /
                            mediaPlayer.NaturalDuration.TimeSpan.TotalMilliseconds) * 100;
            videoProgress.Value = progress;
            currentTimeText.Text = FormatTimeSpan(mediaPlayer.Position);
        }
    }

    private void UpdateTotalDuration()
    {
        if (mediaPlayer.NaturalDuration.HasTimeSpan)
        {
            totalTimeText.Text = FormatTimeSpan(mediaPlayer.NaturalDuration.TimeSpan);
        }
    }

    private string FormatTimeSpan(TimeSpan timeSpan)
    {
        return timeSpan.ToString(@"hh\:mm\:ss");
    }

    private void UpdatePlayPauseButton()
    {
        playPauseButton.Content = isPlaying ? "Pause" : "Play";
    }

    private void UpdateInPointDisplay()
    {
        inPointText.Text = inPoint.HasValue ? $"In: {FormatTimeSpan(inPoint.Value)}" : "In: --:--:--";
        goToInButton.IsEnabled = inPoint.HasValue && mediaPlayer.Source != null;
    }

    private void UpdateOutPointDisplay()
    {
        outPointText.Text = outPoint.HasValue ? $"Out: {FormatTimeSpan(outPoint.Value)}" : "Out: --:--:--";
        goToOutButton.IsEnabled = outPoint.HasValue && mediaPlayer.Source != null;
    }

    private void MakeInButton_Click(object sender, RoutedEventArgs e)
    {
        if (mediaPlayer.Source == null) return;

        // If playing, pause the video
        if (isPlaying)
        {
            mediaPlayer.Pause();
            isPlaying = false;
            timer.Stop();
            UpdatePlayPauseButton();
            UpdateNavigationButtons();
        }

        // Set in point to current position
        inPoint = mediaPlayer.Position;
        UpdateInPointDisplay();
    }

    private void GoToInButton_Click(object sender, RoutedEventArgs e)
    {
        if (mediaPlayer.Source == null || !inPoint.HasValue) return;

        mediaPlayer.Position = inPoint.Value;
        UpdateProgress();
        UpdateNavigationButtons();
    }

    private void MakeOutButton_Click(object sender, RoutedEventArgs e)
    {
        if (mediaPlayer.Source == null) return;

        // If playing, pause the video
        if (isPlaying)
        {
            mediaPlayer.Pause();
            isPlaying = false;
            timer.Stop();
            UpdatePlayPauseButton();
            UpdateNavigationButtons();
        }

        // Set out point to current position
        outPoint = mediaPlayer.Position;
        UpdateOutPointDisplay();
    }

    private void GoToOutButton_Click(object sender, RoutedEventArgs e)
    {
        if (mediaPlayer.Source == null || !outPoint.HasValue) return;

        mediaPlayer.Position = outPoint.Value;
        UpdateProgress();
        UpdateNavigationButtons();
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!isFFmpegAvailable)
        {
            MessageBox.Show(
                "FFmpeg is not available. Please install FFmpeg and add it to your system PATH.",
                "Export Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
            return;
        }

        if (currentVideoPath == null || (!inPoint.HasValue && !outPoint.HasValue && lastTransformation == null))
        {
            MessageBox.Show("Please set in/out points or apply a transformation before exporting.", "Export Video",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var saveFileDialog = new SaveFileDialog
        {
            Filter = "MP4 files (*.mp4)|*.mp4|AVI files (*.avi)|*.avi|MKV files (*.mkv)|*.mkv",
            DefaultExt = System.IO.Path.GetExtension(currentVideoPath),
            FileName = System.IO.Path.GetFileNameWithoutExtension(currentVideoPath) + "_exported" +
                       System.IO.Path.GetExtension(currentVideoPath)
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            try
            {
                // Disable UI during export
                IsEnabled = false;
                Cursor = Cursors.Wait;

                // Show progress UI
                exportProgress.Value = 0;
                exportProgress.Visibility = Visibility.Visible;
                exportStatus.Text = "Preparing...";
                exportStatus.Visibility = Visibility.Visible;

                // Build FFmpeg arguments
                var ffmpegArgs = await BuildFFmpegArguments(currentVideoPath, saveFileDialog.FileName);

                // Get video duration for progress calculation
                var duration = await GetVideoDuration(currentVideoPath);
                var durationMs = duration.TotalMilliseconds;

                // Create process start info
                var startInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = ffmpegArgs,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                // Create log file
                using var logFile = new StreamWriter("ffmpeg-output.log", false);
                logFile.WriteLine($"FFmpeg Export Log - {DateTime.Now}");
                logFile.WriteLine($"Input file: {currentVideoPath}");
                logFile.WriteLine($"Output file: {saveFileDialog.FileName}");
                logFile.WriteLine($"FFmpeg arguments: {ffmpegArgs}");
                logFile.WriteLine("\nFFmpeg Output:");
                logFile.WriteLine("----------------------------------------");

                // Execute FFmpeg
                using var process = new Process { StartInfo = startInfo };

                // Set up progress reporting
                var progressRegex = new System.Text.RegularExpressions.Regex(@"time=(\d{2}):(\d{2}):(\d{2})\.(\d{2})");

                process.ErrorDataReceived += (s, args) =>
                {
                    if (args.Data == null) return;

                    // Log to file
                    logFile.WriteLine($"[stderr] {args.Data}");
                    logFile.Flush();

                    var match = progressRegex.Match(args.Data);
                    if (match.Success)
                    {
                        var hours = int.Parse(match.Groups[1].Value);
                        var minutes = int.Parse(match.Groups[2].Value);
                        var seconds = int.Parse(match.Groups[3].Value);
                        var hundredths = int.Parse(match.Groups[4].Value);

                        var currentMs = (hours * 3600000) + (minutes * 60000) + (seconds * 1000) + (hundredths * 10);
                        var progress = (currentMs / durationMs) * 100;

                        // Update UI on UI thread
                        Dispatcher.Invoke(() =>
                        {
                            exportProgress.Value = progress;
                            exportStatus.Text = $"Processing: {progress:F1}%";
                        });
                    }
                };

                process.OutputDataReceived += (s, args) =>
                {
                    if (args.Data != null)
                    {
                        // Log to file
                        logFile.WriteLine($"[stdout] {args.Data}");
                        logFile.Flush();
                    }
                };

                process.Start();
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
                await process.WaitForExitAsync();

                // Log completion status
                logFile.WriteLine("\n----------------------------------------");
                logFile.WriteLine($"Process exit code: {process.ExitCode}");
                logFile.WriteLine($"Export completed at: {DateTime.Now}");

                if (process.ExitCode == 0)
                {
                    exportStatus.Text = "Export Complete";
                    MessageBox.Show("Video exported successfully!", "Export Complete", MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    exportStatus.Text = "Export Failed";
                    MessageBox.Show("Error exporting video. Please check if FFmpeg is installed correctly.",
                        "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                exportStatus.Text = "Export Failed";
                MessageBox.Show($"Error exporting video: {ex.Message}", "Export Error", MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                // Re-enable UI
                IsEnabled = true;
                Cursor = Cursors.Arrow;

                // Hide progress UI after a short delay
                await Task.Delay(2000);
                exportProgress.Visibility = Visibility.Collapsed;
                exportStatus.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async Task<TimeSpan> GetVideoDuration(string videoPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffprobe",
            Arguments =
                $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (double.TryParse(output, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        throw new Exception("Could not determine video duration");
    }

    private async Task<string> BuildFFmpegArguments(string inputPath, string outputPath)
    {
        // Get video information using ffprobe
        var probeStartInfo = new ProcessStartInfo
        {
            FileName = "ffprobe",
            Arguments = $"-v quiet -print_format json -show_format -show_streams \"{inputPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        using var probeProcess = new Process { StartInfo = probeStartInfo };
        probeProcess.Start();
        var json = await probeProcess.StandardOutput.ReadToEndAsync();
        await probeProcess.WaitForExitAsync();

        var videoInfo = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(json);

        // Build complex FFmpeg command
        var args = new System.Text.StringBuilder();

        // Force overwrite output file
        args.Append("-y ");

        // Input
        args.Append($"-i \"{inputPath}\" ");

        // Trim if in/out points are set
        if (inPoint.HasValue || outPoint.HasValue)
        {
            if (inPoint.HasValue)
            {
                args.Append($"-ss {inPoint.Value:hh\\:mm\\:ss\\.fff} ");
            }

            if (outPoint.HasValue)
            {
                var duration = outPoint.Value - (inPoint ?? TimeSpan.Zero);
                args.Append($"-t {duration:hh\\:mm\\:ss\\.fff} ");
            }
        }

        // Apply transformation if set
        if (!string.IsNullOrEmpty(lastTransformation))
        {
            switch (lastTransformation)
            {
                case "rotate90":
                    args.Append("-vf \"transpose=2\" "); // 90° counterclockwise
                    break;
                case "rotate180":
                    args.Append("-vf \"transpose=2,transpose=2\" "); // 180°
                    break;
                case "rotate270":
                    args.Append("-vf \"transpose=1\" "); // 90° clockwise
                    break;
                case "mirror":
                    args.Append("-vf \"hflip\" "); // Horizontal flip
                    break;
                case "mirrorRotate90":
                    args.Append("-vf \"hflip,transpose=2\" ");
                    break;
                case "mirrorRotate180":
                    args.Append("-vf \"hflip,transpose=2,transpose=2\" ");
                    break;
                case "mirrorRotate270":
                    args.Append("-vf \"hflip,transpose=1\" ");
                    break;
            }
        }

        // Copy original codecs and settings if no transformation
        if (string.IsNullOrEmpty(lastTransformation))
        {
            args.Append("-c copy ");
        }
        else
        {
            // If we need to re-encode, try to use hardware acceleration
            args.Append("-c:a copy "); // Copy audio stream as is

            // Find video stream info
            string? originalCodec = null;
            string? bitRate = null;
            foreach (var stream in videoInfo.GetProperty("streams").EnumerateArray())
            {
                if (stream.GetProperty("codec_type").GetString() == "video")
                {
                    originalCodec = stream.GetProperty("codec_name").GetString();
                    bitRate = stream.TryGetProperty("bit_rate", out var br) ? br.GetString() : null;
                    break;
                }
            }

            // Use NVENC if available and input codec is compatible
            if (isNvencAvailable)
            {
                // Use NVENC with appropriate codec and settings based on input
                if (originalCodec == "h264")
                {
                    // For H.264 input, maintain H.264 output with NVENC
                    args.Append("-c:v h264_nvenc -preset p7 -rc vbr -cq 19 ");
                }
                else if (originalCodec == "hevc")
                {
                    // For HEVC input, maintain HEVC output with NVENC
                    args.Append("-c:v hevc_nvenc -preset p7 -rc vbr -cq 19 ");
                }
                else
                {
                    // For other codecs, default to high-quality H.264 NVENC
                    args.Append("-c:v h264_nvenc -preset p7 -rc vbr -cq 19 -profile:v high ");
                }
            }
            else
            {
                // Fallback to software encoding with original codec
                if (originalCodec != null)
                {
                    args.Append($"-c:v {originalCodec} ");
                }
            }

            // Maintain original bitrate if available
            if (bitRate != null)
            {
                args.Append($"-b:v {bitRate} -maxrate {bitRate} -bufsize {bitRate} ");
            }
        }

        // Output
        args.Append($"\"{outputPath}\"");

        return args.ToString();
    }

    private void TransformButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            // Create a TransformGroup to hold our transformations
            var transformGroup = new TransformGroup();

            // First add the initial rotation transform
            transformGroup.Children.Add(InitialRotationTransform);

            switch (button.Name)
            {
                case "normalTransformButton":
                    mediaPlayer.LayoutTransform = InitialRotationTransform;
                    lastTransformation = null;
                    return;

                case "rotate90Button":
                    transformGroup.Children.Add(new RotateTransform(-90));
                    lastTransformation = "rotate90";
                    break;

                case "rotate180Button":
                    transformGroup.Children.Add(new RotateTransform(-180));
                    lastTransformation = "rotate180";
                    break;

                case "rotate270Button":
                    transformGroup.Children.Add(new RotateTransform(-270));
                    lastTransformation = "rotate270";
                    break;

                case "mirrorButton":
                    transformGroup.Children.Add(new ScaleTransform(-1, 1));
                    lastTransformation = "mirror";
                    break;

                case "mirrorRotate90Button":
                    transformGroup.Children.Add(new ScaleTransform(-1, 1));
                    transformGroup.Children.Add(new RotateTransform(-90));
                    lastTransformation = "mirrorRotate90";
                    break;

                case "mirrorRotate180Button":
                    transformGroup.Children.Add(new ScaleTransform(-1, 1));
                    transformGroup.Children.Add(new RotateTransform(-180));
                    lastTransformation = "mirrorRotate180";
                    break;

                case "mirrorRotate270Button":
                    transformGroup.Children.Add(new ScaleTransform(-1, 1));
                    transformGroup.Children.Add(new RotateTransform(-270));
                    lastTransformation = "mirrorRotate270";
                    break;
            }

            mediaPlayer.LayoutTransform = transformGroup;
        }
    }

    private async Task<int> GetVideoRotation(string videoPath)
    {
        try
        {
            // First try to get the rotation tag
            var rotationStartInfo = new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments =
                    $"-v quiet -select_streams v:0 -show_entries stream_tags=rotate -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var rotationProcess = new Process { StartInfo = rotationStartInfo };
            rotationProcess.Start();
            var rotationOutput = await rotationProcess.StandardOutput.ReadToEndAsync();
            await rotationProcess.WaitForExitAsync();

            // If rotation tag exists and is valid, use it
            if (int.TryParse(rotationOutput.Trim(), out int rotation))
            {
                return rotation;
            }

            // If no rotation tag, try to get the display matrix
            var matrixStartInfo = new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments =
                    $"-v quiet -select_streams v:0 -show_entries stream_side_data=displaymatrix -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var matrixProcess = new Process { StartInfo = matrixStartInfo };
            matrixProcess.Start();
            var matrixOutput = await matrixProcess.StandardOutput.ReadToEndAsync();
            await matrixProcess.WaitForExitAsync();

            // Parse display matrix to determine rotation
            // Display matrix format is typically a 9-element array
            // For rotation, we typically look at elements [0] and [3]
            var matrix = matrixOutput.Trim().Split('\n');
            if (matrix.Length >= 9)
            {
                // Convert display matrix to rotation angle
                // This is a simplified version - might need adjustment based on actual matrix values
                if (double.TryParse(matrix[0], out double m00) && double.TryParse(matrix[3], out double m10))
                {
                    var angle = Math.Atan2(m10, m00) * (180 / Math.PI);
                    return (int)Math.Round(angle);
                }
            }

            // If no rotation information found
            return 0;
        }
        catch (Exception ex)
        {
            // Log the error
            using (var logFile = File.AppendText("ffmpeg-output.log"))
            {
                logFile.WriteLine($"Error reading video rotation: {ex.Message}");
            }

            return 0;
        }
    }
}