using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.Versioning;
using MediaModule.Application.Abstractions;
using MediaModule.Domain.Entities;
using Forms = System.Windows.Forms;

namespace MediaModule.Infrastructure.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowsFileCorrectionService : IFileCorrectionService
{
    public Task<FileCorrectionAction> RequestCorrectionAsync(
        string rejectedFilePath,
        string recommendedDirectory,
        string recommendedFileName,
        string reason,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(FileCorrectionAction.None);
        }

        var source = new TaskCompletionSource<FileCorrectionAction>();
        var thread = new Thread(() =>
        {
            try
            {
                using var form = BuildForm(rejectedFilePath, recommendedDirectory, recommendedFileName, reason, out var action);
                form.ShowDialog();
                source.TrySetResult(action.Value);
            }
            catch (Exception ex)
            {
                source.TrySetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
        return source.Task;
    }

    private static Forms.Form BuildForm(
        string sourceFilePath,
        string recommendedDirectory,
        string recommendedFileName,
        string reason,
        out CorrectionActionHolder action)
    {
        action = new CorrectionActionHolder();
        var actionHolder = action;
        var targetPath = Path.Combine(recommendedDirectory, recommendedFileName);

        var form = new Forms.Form
        {
            Text = "MediaModule: рекомендация по файлу",
            Width = 760,
            Height = 590,
            StartPosition = Forms.FormStartPosition.CenterScreen,
            ShowInTaskbar = false,
            TopMost = true,
            MinimizeBox = false,
            MaximizeBox = false,
            FormBorderStyle = Forms.FormBorderStyle.None,
            BackColor = Color.FromArgb(248, 250, 252),
            ForeColor = Color.FromArgb(15, 23, 42),
        };
        form.Load += (_, _) => form.Region = CreateRoundedRegion(form.ClientRectangle, 18);
        form.Paint += (_, args) =>
        {
            args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.FromArgb(203, 213, 225), 2f);
            args.Graphics.DrawRectangle(pen, 1, 1, form.Width - 3, form.Height - 3);
        };
        form.Shown += (_, _) =>
        {
            form.TopMost = true;
            form.BringToFront();
            form.Activate();
        };

        var title = CreateLabel("Рекомендация по названию и папке", 28, 24, 620, 30, 14f, FontStyle.Bold, Color.FromArgb(15, 23, 42));
        var subtitle = CreateLabel(
            "Файл можно оставить как есть или перенести в рекомендуемую структуру хранения.",
            28,
            58,
            650,
            38,
            9.5f,
            FontStyle.Regular,
            Color.FromArgb(71, 85, 105));
        var closeButton = CreateFlatButton("x", 704, 24, 28, 28, Color.FromArgb(248, 250, 252), Color.FromArgb(100, 116, 139));
        closeButton.Click += (_, _) =>
        {
            actionHolder.Value = FileCorrectionAction.CancelProcessing;
            form.Close();
        };

        var reasonLabel = CreateLabel("Причина", 28, 118, 110, 22, 9.2f, FontStyle.Bold, Color.FromArgb(15, 23, 42));
        var reasonBox = CreateReadonlyBox(
            string.IsNullOrWhiteSpace(reason) ? "Имя или расположение файла не соответствуют правилам." : reason,
            28,
            144,
            704,
            52,
            multiline: true);

        var currentLabel = CreateLabel("Сейчас", 28, 214, 110, 22, 9.2f, FontStyle.Bold, Color.FromArgb(15, 23, 42));
        var currentBox = CreateReadonlyBox(sourceFilePath, 28, 240, 704, 34, multiline: false);

        var targetLabel = CreateLabel("Рекомендуется", 28, 300, 160, 22, 9.2f, FontStyle.Bold, Color.FromArgb(15, 23, 42));
        var targetNameLabel = CreateLabel("Имя файла", 28, 330, 120, 22, 9.2f, FontStyle.Regular, Color.FromArgb(71, 85, 105));
        var targetNameBox = CreateReadonlyBox(recommendedFileName, 152, 326, 580, 34, multiline: false);
        var targetDirectoryLabel = CreateLabel("Папка", 28, 374, 120, 22, 9.2f, FontStyle.Regular, Color.FromArgb(71, 85, 105));
        var targetDirectoryBox = CreateReadonlyBox(FormatMissingDirectory(recommendedDirectory), 152, 370, 580, 34, multiline: false);
        var targetPathLabel = CreateLabel("Итоговый путь", 28, 418, 120, 22, 9.2f, FontStyle.Regular, Color.FromArgb(71, 85, 105));
        var targetPathBox = CreateReadonlyBox(FormatMissingDirectory(targetPath), 152, 414, 580, 34, multiline: false);

        var hint = CreateLabel(
            "При подтверждении файл будет переименован и перенесен. Если оставить как есть, нарушение попадет в журнал.",
            28,
            472,
            704,
            44,
            9.1f,
            FontStyle.Regular,
            Color.FromArgb(100, 116, 139));

        var backButton = CreateFlatButton("Назад", 278, 530, 110, 34, Color.White, Color.FromArgb(71, 85, 105));
        var keepButton = CreateFlatButton("Оставить как есть", 400, 530, 150, 34, Color.White, Color.FromArgb(71, 85, 105));
        var moveButton = CreateFlatButton("Исправить и перенести", 562, 530, 170, 34, Color.FromArgb(255, 42, 0), Color.White);

        backButton.Click += (_, _) =>
        {
            actionHolder.Value = FileCorrectionAction.BackToOrderSelection;
            form.Close();
        };

        keepButton.Click += (_, _) =>
        {
            actionHolder.Value = FileCorrectionAction.None;
            form.Close();
        };

        moveButton.Click += (_, _) =>
        {
            actionHolder.Value = FileCorrectionAction.AcceptAndMove;
            form.Close();
        };

        form.Controls.Add(title);
        form.Controls.Add(subtitle);
        form.Controls.Add(closeButton);
        form.Controls.Add(reasonLabel);
        form.Controls.Add(reasonBox);
        form.Controls.Add(currentLabel);
        form.Controls.Add(currentBox);
        form.Controls.Add(targetLabel);
        form.Controls.Add(targetNameLabel);
        form.Controls.Add(targetNameBox);
        form.Controls.Add(targetDirectoryLabel);
        form.Controls.Add(targetDirectoryBox);
        form.Controls.Add(targetPathLabel);
        form.Controls.Add(targetPathBox);
        form.Controls.Add(hint);
        form.Controls.Add(backButton);
        form.Controls.Add(keepButton);
        form.Controls.Add(moveButton);

        return form;
    }

    private static Forms.Label CreateLabel(string text, int left, int top, int width, int height, float size, FontStyle style, Color foreColor) =>
        new()
        {
            Text = text,
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            Font = new Font("Segoe UI", size, style),
            ForeColor = foreColor,
            BackColor = Color.Transparent,
        };

    private static Forms.Label CreateReadonlyBox(string text, int left, int top, int width, int height, bool multiline) =>
        new()
        {
            Text = text,
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            BorderStyle = Forms.BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(248, 250, 252),
            ForeColor = Color.FromArgb(15, 23, 42),
            Font = new Font("Segoe UI", 9.4f, FontStyle.Regular),
            Padding = new Forms.Padding(8, multiline ? 6 : 5, 8, 4),
            AutoEllipsis = true,
        };

    private static string FormatMissingDirectory(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "Не удалось определить рекомендуемую папку. Проверьте корневую директорию и данные заказа."
            : value;
    }

    private static Forms.Button CreateFlatButton(string text, int left, int top, int width, int height, Color backColor, Color foreColor)
    {
        var button = new Forms.Button
        {
            Text = text,
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            FlatStyle = Forms.FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = foreColor,
            Font = new Font("Segoe UI", 8.8f, FontStyle.Regular),
            TabStop = false,
        };
        button.FlatAppearance.BorderColor = backColor == Color.White
            ? Color.FromArgb(203, 213, 225)
            : backColor;
        button.FlatAppearance.BorderSize = 1;
        return button;
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

    private sealed class CorrectionActionHolder
    {
        public FileCorrectionAction Value { get; set; } = FileCorrectionAction.None;
    }
}
