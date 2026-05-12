using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.Versioning;
using MediaModule.Application.Abstractions;
using MediaModule.Domain.Entities;
using Forms = System.Windows.Forms;

namespace MediaModule.Infrastructure.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowsDuplicateResolutionService : IDuplicateResolutionService
{
    public Task<DuplicateResolutionAction> ResolveAsync(
        string currentFilePath,
        string duplicateFilePath,
        OrderData? orderData,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(DuplicateResolutionAction.SaveAsNew);
        }

        var source = new TaskCompletionSource<DuplicateResolutionAction>();
        var thread = new Thread(() =>
        {
            try
            {
                using var form = BuildForm(currentFilePath, duplicateFilePath, orderData, out var action);
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
        string currentFilePath,
        string duplicateFilePath,
        OrderData? orderData,
        out DuplicateActionHolder action)
    {
        action = new DuplicateActionHolder();
        var actionHolder = action;

        var form = new Forms.Form
        {
            Text = "MediaModule: похожий файл",
            Width = 860,
            Height = 620,
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

        var title = CreateLabel("Найден похожий файл", 28, 24, 560, 30, 14f, FontStyle.Bold, Color.FromArgb(15, 23, 42));
        var subtitle = CreateLabel(
            BuildSubtitle(orderData),
            28,
            58,
            720,
            42,
            9.4f,
            FontStyle.Regular,
            Color.FromArgb(71, 85, 105));
        var closeButton = CreateFlatButton("x", 810, 26, 28, 28, Color.FromArgb(248, 250, 252), Color.FromArgb(100, 116, 139));
        closeButton.Click += (_, _) =>
        {
            actionHolder.Value = DuplicateResolutionAction.SaveAsNew;
            form.Close();
        };

        var currentPanel = CreatePreviewPanel("Новый файл", currentFilePath, 28, 126);
        var duplicatePanel = CreatePreviewPanel("Похожий файл в журнале", duplicateFilePath, 438, 126);

        var hint = CreateLabel(
            "Выберите, что сделать с новым файлом. Если это другой заказчик, можно выбрать другой заказ и сохранить файл как отдельную работу.",
            28,
            478,
            500,
            44,
            9.2f,
            FontStyle.Regular,
            Color.FromArgb(100, 116, 139));

        var saveButton = CreateFlatButton("Сохранить как новый", 28, 548, 170, 36, Color.FromArgb(255, 42, 0), Color.White);
        var orderButton = CreateFlatButton("Выбрать другой заказ", 210, 548, 178, 36, Color.White, Color.FromArgb(71, 85, 105));
        var cancelButton = CreateFlatButton("Отменить сохранение", 400, 548, 178, 36, Color.White, Color.FromArgb(71, 85, 105));
        var replaceButton = CreateFlatButton("Заменить предыдущий", 590, 548, 190, 36, Color.FromArgb(15, 23, 42), Color.White);

        saveButton.Click += (_, _) => CloseWith(form, actionHolder, DuplicateResolutionAction.SaveAsNew);
        orderButton.Click += (_, _) => CloseWith(form, actionHolder, DuplicateResolutionAction.ChooseAnotherOrder);
        cancelButton.Click += (_, _) => CloseWith(form, actionHolder, DuplicateResolutionAction.CancelSave);
        replaceButton.Click += (_, _) => CloseWith(form, actionHolder, DuplicateResolutionAction.ReplaceExisting);

        form.Controls.Add(title);
        form.Controls.Add(subtitle);
        form.Controls.Add(closeButton);
        form.Controls.Add(currentPanel);
        form.Controls.Add(duplicatePanel);
        form.Controls.Add(hint);
        form.Controls.Add(saveButton);
        form.Controls.Add(orderButton);
        form.Controls.Add(cancelButton);
        form.Controls.Add(replaceButton);

        return form;
    }

    private static string BuildSubtitle(OrderData? orderData)
    {
        return orderData is null
            ? "Найден визуально похожий файл. Заказ не определен."
            : $"Найден визуально похожий файл. Текущий заказ: {orderData.OrderId} | {orderData.ClientName} / {orderData.ProductType}.";
    }

    private static Forms.Panel CreatePreviewPanel(string title, string filePath, int left, int top)
    {
        var panel = new Forms.Panel
        {
            Left = left,
            Top = top,
            Width = 390,
            Height = 330,
            BackColor = Color.White,
        };
        panel.Paint += (_, args) =>
        {
            using var pen = new Pen(Color.FromArgb(226, 232, 240));
            args.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
        };

        var titleLabel = CreateLabel(title, 14, 12, 340, 22, 9.4f, FontStyle.Bold, Color.FromArgb(15, 23, 42));
        var imageBox = new Forms.PictureBox
        {
            Left = 14,
            Top = 44,
            Width = 362,
            Height = 220,
            BackColor = Color.FromArgb(241, 245, 249),
            SizeMode = Forms.PictureBoxSizeMode.Zoom,
        };

        if (File.Exists(filePath))
        {
            try
            {
                using var image = Image.FromFile(filePath);
                imageBox.Image = new Bitmap(image);
            }
            catch
            {
                imageBox.Controls.Add(CreateCenteredLabel("Предпросмотр недоступен", imageBox.Width, imageBox.Height));
            }
        }
        else
        {
            imageBox.Controls.Add(CreateCenteredLabel("Файл не найден", imageBox.Width, imageBox.Height));
        }

        var nameLabel = CreateLabel(Path.GetFileName(filePath), 14, 278, 362, 20, 9.1f, FontStyle.Bold, Color.FromArgb(15, 23, 42));
        var pathLabel = CreateLabel(filePath, 14, 300, 362, 20, 8.4f, FontStyle.Regular, Color.FromArgb(100, 116, 139));

        panel.Controls.Add(titleLabel);
        panel.Controls.Add(imageBox);
        panel.Controls.Add(nameLabel);
        panel.Controls.Add(pathLabel);
        return panel;
    }

    private static Forms.Label CreateCenteredLabel(string text, int width, int height) =>
        new()
        {
            Text = text,
            Left = 0,
            Top = 0,
            Width = width,
            Height = height,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(100, 116, 139),
            Font = new Font("Segoe UI", 9.2f, FontStyle.Regular),
        };

    private static void CloseWith(Forms.Form form, DuplicateActionHolder holder, DuplicateResolutionAction action)
    {
        holder.Value = action;
        form.Close();
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

    private sealed class DuplicateActionHolder
    {
        public DuplicateResolutionAction Value { get; set; } = DuplicateResolutionAction.SaveAsNew;
    }
}
