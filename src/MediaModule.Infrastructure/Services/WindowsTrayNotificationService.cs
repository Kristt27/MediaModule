using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MediaModule.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace MediaModule.Infrastructure.Services;

public sealed class WindowsTrayNotificationService : IFileNotificationService, IDisposable
{
    private readonly ILogger<WindowsTrayNotificationService> _logger;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _uiThread;

    private NotifyIcon? _notifyIcon;
    private Form? _hostForm;
    private Form? _progressNotification;
    private System.Windows.Forms.Timer? _progressTimer;
    private bool _disposed;

    public WindowsTrayNotificationService(ILogger<WindowsTrayNotificationService> logger)
    {
        _logger = logger;

        _uiThread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "MediaModule.TrayNotifications",
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();

        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public Task NotifyAsync(string title, string message, CancellationToken cancellationToken)
    {
        if (_disposed || cancellationToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        _logger.LogWarning("{Title}: {Message}", title, message);

        if (_hostForm is null || _notifyIcon is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            _hostForm.BeginInvoke(new Action(() => ShowCustomNotification(title, message)));
        }
        catch (InvalidOperationException)
        {
            _logger.LogDebug("Tray notification skipped because UI handle is no longer available.");
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hostForm is not null && _hostForm.IsHandleCreated)
        {
            try
            {
                _hostForm.BeginInvoke(new Action(() =>
                {
                    if (_notifyIcon is not null)
                    {
                        _notifyIcon.Visible = false;
                        _notifyIcon.Dispose();
                        _notifyIcon = null;
                    }

                    CloseProgressNotification();
                    _hostForm?.Close();
                }));
            }
            catch (InvalidOperationException)
            {
                // Ignore shutdown race.
            }
        }

        _ready.Dispose();
    }

    private void RunMessageLoop()
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

        _hostForm = new Form
        {
            ShowInTaskbar = false,
            WindowState = FormWindowState.Minimized,
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            Opacity = 0,
        };

        _hostForm.Load += (_, _) =>
        {
            _hostForm.Visible = false;
        };

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "MediaModule Worker",
            Visible = true,
            BalloonTipTitle = "MediaModule",
        };

        _notifyIcon.DoubleClick += (_, _) => OpenDesktopUi();
        _notifyIcon.BalloonTipClicked += (_, _) => OpenDesktopUi();

        _ready.Set();
        System.Windows.Forms.Application.Run(_hostForm);
    }

    private void OpenDesktopUi()
    {
        if (TryActivateDesktopWindow())
        {
            return;
        }

        var desktopExe = TryFindDesktopExecutable();
        if (desktopExe is not null)
        {
            StartProcess(desktopExe, string.Empty);
            return;
        }

        var desktopProject = TryFindDesktopProject();
        if (desktopProject is not null)
        {
            StartProcess("dotnet", $"run --project \"{desktopProject}\"");
            return;
        }

        _logger.LogWarning("MediaModule Desktop could not be found for launch from tray notification.");
    }

    private static bool TryActivateDesktopWindow()
    {
        var candidates = Process.GetProcessesByName("MediaModule.Desktop")
            .Concat(Process.GetProcesses().Where(static process =>
            {
                try
                {
                    return process.MainWindowTitle.Contains("MediaModule Desktop", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }));

        foreach (var process in candidates)
        {
            if (process.MainWindowHandle == IntPtr.Zero)
            {
                continue;
            }

            ShowWindow(process.MainWindowHandle, 9);
            SetForegroundWindow(process.MainWindowHandle);
            return true;
        }

        return false;
    }

    private static string? TryFindDesktopExecutable()
    {
        var baseDir = AppContext.BaseDirectory;

        var candidates = new[]
        {
            Path.Combine(baseDir, "MediaModule.Desktop.exe"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "MediaModule.Desktop", "bin", "Debug", "net8.0-windows", "MediaModule.Desktop.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "MediaModule.Desktop", "bin", "Release", "net8.0-windows", "MediaModule.Desktop.exe")),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? TryFindDesktopProject()
    {
        var baseDir = AppContext.BaseDirectory;

        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "MediaModule.Desktop", "MediaModule.Desktop.csproj")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "MediaModule.Desktop", "MediaModule.Desktop.csproj")),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void StartProcess(string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        Process.Start(startInfo);
    }

    private void ShowCustomNotification(string title, string message)
    {
        var isProgress = IsProgressNotification(title);
        if (!isProgress)
        {
            CloseProgressNotification();
        }

        var notification = new Form
        {
            Width = isProgress ? 620 : 390,
            Height = isProgress ? 196 : 168,
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
            TopMost = true,
            FormBorderStyle = FormBorderStyle.None,
            BackColor = isProgress ? Color.FromArgb(255, 248, 237) : Color.White,
            Padding = new Padding(0),
        };
        notification.Load += (_, _) => notification.Region = CreateRoundedRegion(notification.ClientRectangle, isProgress ? 16 : 12);
        notification.Shown += (_, _) =>
        {
            notification.TopMost = true;
            notification.BringToFront();
        };

        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetWorkingArea(notification);
        notification.Location = isProgress
            ? new Point(
                workingArea.Left + (workingArea.Width - notification.Width) / 2,
                workingArea.Top + (workingArea.Height - notification.Height) / 2)
            : new Point(
                workingArea.Right - notification.Width - 18,
                workingArea.Bottom - notification.Height - 18);

        var accent = ResolveAccentColor(title);
        var borderPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = isProgress ? Color.FromArgb(255, 248, 237) : Color.White,
        };

        var accentBar = new Panel
        {
            Dock = DockStyle.Left,
            Width = isProgress ? 0 : 6,
            BackColor = accent,
        };

        var titleLabel = new Label
        {
            Text = Trim(title.Replace("MediaModule:", "MediaModule -"), 72),
            AutoSize = false,
            Left = isProgress ? 28 : 22,
            Top = isProgress ? 22 : 16,
            Width = isProgress ? 564 : 306,
            Height = 24,
            Font = new Font("Segoe UI", isProgress ? 11.5f : 10.5f, FontStyle.Bold),
            ForeColor = isProgress ? Color.FromArgb(156, 83, 0) : Color.FromArgb(31, 41, 55),
        };

        var closeButton = new Button
        {
            Text = "x",
            Left = 346,
            Top = 12,
            Width = 28,
            Height = 26,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(107, 114, 128),
            TabStop = false,
            Visible = !isProgress,
        };
        closeButton.FlatAppearance.BorderSize = 0;
        closeButton.Click += (_, _) => notification.Close();

        var messageLabel = new Label
        {
            Text = Trim(message, 185),
            AutoSize = false,
            Left = isProgress ? 28 : 22,
            Top = isProgress ? 58 : 48,
            Width = isProgress ? 564 : 340,
            Height = isProgress ? 54 : 82,
            Font = new Font("Segoe UI", 9.2f, FontStyle.Regular),
            ForeColor = isProgress ? Color.FromArgb(51, 65, 85) : Color.FromArgb(55, 65, 81),
        };

        var hintLabel = new Label
        {
            Text = "Двойной клик по значку в трее откроет журнал",
            AutoSize = false,
            Left = 22,
            Top = 132,
            Width = 340,
            Height = 20,
            Font = new Font("Segoe UI", 8.3f, FontStyle.Regular),
            ForeColor = Color.FromArgb(107, 114, 128),
        };

        if (isProgress)
        {
            hintLabel.Text = "Пожалуйста, подождите. Следующее окно откроется автоматически.";
            hintLabel.Left = 28;
            hintLabel.Top = 150;
            hintLabel.Width = 564;
            hintLabel.ForeColor = Color.FromArgb(71, 85, 105);
        }

        if (!isProgress)
        {
            notification.Click += (_, _) => OpenDesktopUi();
            titleLabel.Click += (_, _) => OpenDesktopUi();
            messageLabel.Click += (_, _) => OpenDesktopUi();
        }

        borderPanel.Paint += (_, args) =>
        {
            args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(isProgress ? Color.FromArgb(255, 184, 77) : Color.FromArgb(229, 231, 235), isProgress ? 2f : 1f);
            var inset = isProgress ? 2 : 0;
            args.Graphics.DrawRectangle(pen, inset, inset, notification.Width - (inset * 2) - 1, notification.Height - (inset * 2) - 1);
        };

        var progressBar = new ProgressBar
        {
            Left = 28,
            Top = 124,
            Width = 564,
            Height = 8,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 35,
            Visible = isProgress,
        };

        borderPanel.Controls.Add(accentBar);
        borderPanel.Controls.Add(titleLabel);
        borderPanel.Controls.Add(closeButton);
        borderPanel.Controls.Add(messageLabel);
        borderPanel.Controls.Add(progressBar);
        borderPanel.Controls.Add(hintLabel);
        notification.Controls.Add(borderPanel);

        var timer = new System.Windows.Forms.Timer
        {
            Interval = isProgress ? 30000 : 10000,
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            if (!notification.IsDisposed)
            {
                notification.Close();
            }
        };

        notification.FormClosed += (_, _) =>
        {
            timer.Dispose();
            notification.Dispose();
        };

        if (isProgress)
        {
            CloseProgressNotification();
            _progressNotification = notification;
            _progressTimer = timer;
        }

        notification.Show(_hostForm);
        timer.Start();
    }

    private void CloseProgressNotification()
    {
        if (_progressTimer is not null)
        {
            _progressTimer.Stop();
            _progressTimer.Dispose();
            _progressTimer = null;
        }

        if (_progressNotification is not null && !_progressNotification.IsDisposed)
        {
            _progressNotification.Close();
        }

        _progressNotification = null;
    }

    private static bool IsProgressNotification(string title)
    {
        return title.Contains("подожд", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("провер", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("ожида", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("ищ", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("готов", StringComparison.OrdinalIgnoreCase);
    }

    private static Color ResolveAccentColor(string title)
    {
        if (title.Contains("ошибка", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            return Color.FromArgb(220, 38, 38);
        }

        if (title.Contains("запрещ", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("блок", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("blocked", StringComparison.OrdinalIgnoreCase))
        {
            return Color.FromArgb(245, 124, 0);
        }

        return Color.FromArgb(255, 138, 0);
    }

    private static string Trim(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength
            ? value
            : $"{value[..Math.Max(0, maxLength - 3)]}...";
    }

    private static Region CreateRoundedRegion(Rectangle bounds, int radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return new Region(path);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
