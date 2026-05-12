using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.Versioning;
using MediaModule.Application.Abstractions;
using MediaModule.Domain.Entities;
using Forms = System.Windows.Forms;

namespace MediaModule.Infrastructure.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowsTagReviewService : ITagReviewService
{
    public Task<bool> RequestTagApprovalAsync(
        string filePath,
        IReadOnlyCollection<TagItem> tags,
        OrderData? orderData,
        CancellationToken cancellationToken)
    {
        var visibleTags = FilterVisibleTags(tags);
        if (visibleTags.Count == 0 || cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(false);
        }

        var source = new TaskCompletionSource<bool>();
        var thread = new Thread(() =>
        {
            try
            {
                using var form = BuildForm(filePath, visibleTags, orderData, out var accepted);
                form.ShowDialog();
                source.TrySetResult(accepted.Value);
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

    private static IReadOnlyCollection<TagItem> FilterVisibleTags(IReadOnlyCollection<TagItem> tags)
    {
        return tags
            .Where(static tag => !string.IsNullOrWhiteSpace(tag.Key) && !ShouldHideTag(tag.Key))
            .ToList();
    }

    private static bool ShouldHideTag(string key)
    {
        return key.Trim().ToLowerInvariant() is
            "composition" or
            "object_type" or
            "layout_type" or
            "design_type" or
            "mood" or
            "purpose" or
            "audience" or
            "format";
    }

    private static Forms.Form BuildForm(
        string filePath,
        IReadOnlyCollection<TagItem> tags,
        OrderData? orderData,
        out DecisionHolder accepted)
    {
        accepted = new DecisionHolder();
        var decision = accepted;

        var form = new Forms.Form
        {
            Text = "MediaModule: предложенные теги",
            Width = 760,
            Height = 560,
            StartPosition = Forms.FormStartPosition.CenterScreen,
            ShowInTaskbar = false,
            TopMost = true,
            MinimizeBox = false,
            MaximizeBox = false,
            FormBorderStyle = Forms.FormBorderStyle.None,
            BackColor = Color.FromArgb(248, 250, 252),
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

        var title = CreateLabel("GigaChat предложил теги", 28, 24, 560, 30, 14f, FontStyle.Bold, Color.FromArgb(15, 23, 42));
        var subtitleText = orderData is null
            ? Path.GetFileName(filePath)
            : $"{Path.GetFileName(filePath)}   |   заказ {orderData.OrderId}: {orderData.ClientName} / {orderData.ProductType}";
        var subtitle = CreateLabel(subtitleText, 28, 58, 650, 22, 9.4f, FontStyle.Regular, Color.FromArgb(71, 85, 105));
        var closeButton = CreateFlatButton("x", 710, 26, 28, 28, Color.FromArgb(248, 250, 252), Color.FromArgb(100, 116, 139));
        closeButton.Click += (_, _) =>
        {
            decision.Value = false;
            form.Close();
        };

        var grid = new Forms.DataGridView
        {
            Left = 24,
            Top = 120,
            Width = 712,
            Height = 348,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            SelectionMode = Forms.DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeRowsMode = Forms.DataGridViewAutoSizeRowsMode.AllCells,
            BackgroundColor = Color.White,
            BorderStyle = Forms.BorderStyle.None,
            RowHeadersVisible = false,
            ColumnHeadersHeight = 34,
            Font = new Font("Segoe UI", 9.2f, FontStyle.Regular),
            GridColor = Color.FromArgb(226, 232, 240),
        };

        grid.Columns.Add(new Forms.DataGridViewTextBoxColumn
        {
            HeaderText = "Характеристика",
            DataPropertyName = "Key",
            Width = 190,
        });
        grid.Columns.Add(new Forms.DataGridViewTextBoxColumn
        {
            HeaderText = "Значение",
            DataPropertyName = "Value",
            AutoSizeMode = Forms.DataGridViewAutoSizeColumnMode.Fill,
        });

        foreach (var tag in tags)
        {
            grid.Rows.Add(DisplayTagKey(tag.Key), tag.Value);
        }

        var hint = CreateLabel(
            "Проверьте, подходят ли характеристики для поиска макета. Теги запишутся в базу только после подтверждения.",
            24,
            482,
            470,
            42,
            9.2f,
            FontStyle.Regular,
            Color.FromArgb(71, 85, 105));

        var rejectButton = CreateFlatButton("Отклонить", 506, 496, 108, 34, Color.White, Color.FromArgb(71, 85, 105));
        rejectButton.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
        var acceptButton = CreateFlatButton("Принять теги", 626, 496, 110, 34, Color.FromArgb(255, 42, 0), Color.White);

        rejectButton.Click += (_, _) =>
        {
            decision.Value = false;
            form.Close();
        };

        acceptButton.Click += (_, _) =>
        {
            decision.Value = true;
            form.Close();
        };

        form.Controls.Add(title);
        form.Controls.Add(subtitle);
        form.Controls.Add(closeButton);
        form.Controls.Add(grid);
        form.Controls.Add(hint);
        form.Controls.Add(rejectButton);
        form.Controls.Add(acceptButton);

        return form;
    }

    private static Forms.Label CreateLabel(string text, int left, int top, int width, int height, float size, FontStyle style, Color color) =>
        new()
        {
            Text = text,
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            Font = new Font("Segoe UI", size, style),
            ForeColor = color,
            BackColor = Color.Transparent,
        };

    private static string DisplayTagKey(string key)
    {
        return key.Trim().ToLowerInvariant() switch
        {
            "description" or "visual_description" => "Описание",
            "visible_text" => "Надписи",
            "dominant_colors" or "colors" or "color" => "Цвета",
            "background" => "Фон",
            "product_type" or "product" => "Продукт",
            "style" => "Стиль",
            "client" => "Клиент",
            "order_id" or "orderid" => "Заказ",
            "file_name" => "Файл",
            "extension" => "Расширение",
            "search_keywords" => "Ключевые слова",
            var value when value.StartsWith("search_keyword_", StringComparison.Ordinal) => "Ключевое слово",
            _ => key.Replace("_", " "),
        };
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
            Font = new Font("Segoe UI", 9.2f, FontStyle.Regular),
            TabStop = false,
        };
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = backColor == Color.White
            ? Color.FromArgb(203, 213, 225)
            : backColor;
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

    private sealed class DecisionHolder
    {
        public bool Value { get; set; }
    }
}
