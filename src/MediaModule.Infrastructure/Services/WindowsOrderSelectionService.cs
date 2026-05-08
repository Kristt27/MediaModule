using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.Versioning;
using MediaModule.Application.Abstractions;
using MediaModule.Domain.Entities;
using Forms = System.Windows.Forms;

namespace MediaModule.Infrastructure.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowsOrderSelectionService : IOrderSelectionService
{
    public Task<OrderData?> SelectOrderAsync(
        string filePath,
        IReadOnlyCollection<OrderData> orders,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult<OrderData?>(null);
        }

        var source = new TaskCompletionSource<OrderData?>();
        var thread = new Thread(() =>
        {
            try
            {
                using var form = BuildForm(filePath, orders, out var selection);
                var result = form.ShowDialog();
                source.TrySetResult(result == Forms.DialogResult.OK
                    ? selection.GetSelectedOrder()
                    : null);
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
        string filePath,
        IReadOnlyCollection<OrderData> orders,
        out OrderSelectionState selection)
    {
        var completedOrders = orders
            .Where(IsCompletedWithinLastYear)
            .OrderByDescending(static order => order.CompletedAtUtc ?? DateTime.UtcNow)
            .ToList();

        var form = new Forms.Form
        {
            Text = "MediaModule: выбор заказа",
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
        form.Shown += (_, _) =>
        {
            form.TopMost = true;
            form.BringToFront();
            form.Activate();
        };

        var header = new Forms.Panel
        {
            Dock = Forms.DockStyle.Top,
            Height = 104,
            BackColor = Color.White,
        };

        var accent = new Forms.Panel
        {
            Left = 0,
            Top = 0,
            Width = 6,
            Height = 104,
            BackColor = Color.FromArgb(255, 42, 0),
        };

        var title = CreateLabel("Выберите заказ из ELMA", 28, 20, 560, 28, 13f, FontStyle.Bold, Color.FromArgb(15, 23, 42));
        var label = CreateLabel(
            $"Файл: {Path.GetFileName(filePath)}\r\nЕсли заказ закрыт раньше, введите данные вручную.",
            28,
            52,
            620,
            42,
            9.5f,
            FontStyle.Regular,
            Color.FromArgb(71, 85, 105));

        var closeButton = CreateFlatButton("x", 710, 22, 28, 28, Color.White, Color.FromArgb(100, 116, 139));
        closeButton.Click += (_, _) =>
        {
            form.DialogResult = Forms.DialogResult.Cancel;
            form.Close();
        };

        header.Controls.Add(accent);
        header.Controls.Add(title);
        header.Controls.Add(label);
        header.Controls.Add(closeButton);

        var tabs = new Forms.TabControl
        {
            Left = 24,
            Top = 126,
            Width = 712,
            Height = 326,
            Font = new Font("Segoe UI", 9.4f, FontStyle.Regular),
        };

        var completedTab = new Forms.TabPage("Открыть завершенные заказы");
        var manualTab = new Forms.TabPage("Ввести вручную");
        completedTab.BackColor = Color.White;
        manualTab.BackColor = Color.White;

        var listBox = new Forms.ListBox
        {
            Left = 16,
            Top = 48,
            Width = 664,
            Height = 218,
            DisplayMember = nameof(OrderDisplay.Text),
            BorderStyle = Forms.BorderStyle.FixedSingle,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(15, 23, 42),
            Font = new Font("Segoe UI", 10f, FontStyle.Regular),
        };

        var completedHint = CreateLabel(
            $"Завершенные заказы за последний год: {completedOrders.Count}.",
            16,
            16,
            520,
            22,
            9.2f,
            FontStyle.Regular,
            Color.FromArgb(71, 85, 105));

        foreach (var order in completedOrders)
        {
            listBox.Items.Add(new OrderDisplay(order));
        }

        if (listBox.Items.Count > 0)
        {
            listBox.SelectedIndex = 0;
        }
        else
        {
            completedHint.Text = "За последний год завершенные заказы не найдены. Заполните заказ вручную.";
        }

        completedTab.Controls.Add(completedHint);
        completedTab.Controls.Add(listBox);

        var manualOrderIdLabel = CreateLabel("OrderId", 18, 28, 160, 22, 9.2f, FontStyle.Regular, Color.FromArgb(71, 85, 105));
        var manualOrderIdBox = CreateTextBox(18, 52, 260);
        var manualClientLabel = CreateLabel("Клиент", 18, 96, 160, 22, 9.2f, FontStyle.Regular, Color.FromArgb(71, 85, 105));
        var manualClientBox = CreateTextBox(18, 120, 320);
        var manualProductLabel = CreateLabel("Тип продукта", 18, 164, 160, 22, 9.2f, FontStyle.Regular, Color.FromArgb(71, 85, 105));
        var manualProductBox = CreateTextBox(18, 188, 320);
        var manualHint = CreateLabel(
            "Эти данные попадут в проверку имени, пути, теги и журнал обработки.",
            372,
            52,
            270,
            70,
            9.2f,
            FontStyle.Regular,
            Color.FromArgb(100, 116, 139));

        manualTab.Controls.Add(manualOrderIdLabel);
        manualTab.Controls.Add(manualOrderIdBox);
        manualTab.Controls.Add(manualClientLabel);
        manualTab.Controls.Add(manualClientBox);
        manualTab.Controls.Add(manualProductLabel);
        manualTab.Controls.Add(manualProductBox);
        manualTab.Controls.Add(manualHint);

        tabs.TabPages.Add(completedTab);
        tabs.TabPages.Add(manualTab);
        if (completedOrders.Count == 0)
        {
            tabs.SelectedTab = manualTab;
            manualOrderIdBox.Focus();
        }

        selection = new OrderSelectionState(
            tabs,
            manualTab,
            listBox,
            manualOrderIdBox,
            manualClientBox,
            manualProductBox);
        var selectionState = selection;

        var chooseButton = CreateFlatButton("Выбрать", 620, 486, 116, 34, Color.FromArgb(255, 42, 0), Color.White);
        chooseButton.Click += (_, _) =>
        {
            if (selectionState.GetSelectedOrder() is null)
            {
                Forms.MessageBox.Show(
                    "Выберите заказ из списка или заполните OrderId, клиента и тип продукта вручную.",
                    "MediaModule",
                    Forms.MessageBoxButtons.OK,
                    Forms.MessageBoxIcon.Information);
                return;
            }

            form.DialogResult = Forms.DialogResult.OK;
            form.Close();
        };

        var cancelButton = CreateFlatButton("Отмена", 492, 486, 116, 34, Color.White, Color.FromArgb(71, 85, 105));
        cancelButton.Click += (_, _) =>
        {
            form.DialogResult = Forms.DialogResult.Cancel;
            form.Close();
        };

        var hint = CreateLabel(
            "Список берется из ELMA/API или из mock-настроек. В локальную SQLite-базу записывается только результат обработки.",
            24,
            466,
            430,
            48,
            9.1f,
            FontStyle.Regular,
            Color.FromArgb(100, 116, 139));

        form.Controls.Add(header);
        form.Controls.Add(tabs);
        form.Controls.Add(hint);
        form.Controls.Add(chooseButton);
        form.Controls.Add(cancelButton);
        form.AcceptButton = chooseButton;
        form.CancelButton = cancelButton;

        return form;
    }

    private static bool IsCompletedWithinLastYear(OrderData order)
    {
        var status = order.Status?.Trim();
        var completedStatus = string.IsNullOrWhiteSpace(status) ||
            status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("done", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("closed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("завершен", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("завершён", StringComparison.OrdinalIgnoreCase);

        if (!completedStatus)
        {
            return false;
        }

        return order.CompletedAtUtc is null || order.CompletedAtUtc >= DateTime.UtcNow.AddYears(-1);
    }

    private static Forms.TextBox CreateTextBox(int left, int top, int width) =>
        new()
        {
            Left = left,
            Top = top,
            Width = width,
            Height = 30,
            BorderStyle = Forms.BorderStyle.FixedSingle,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(15, 23, 42),
            Font = new Font("Segoe UI", 10f, FontStyle.Regular),
        };

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

    private sealed class OrderSelectionState
    {
        private readonly Forms.TabControl _tabs;
        private readonly Forms.TabPage _manualTab;
        private readonly Forms.ListBox _listBox;
        private readonly Forms.TextBox _manualOrderIdBox;
        private readonly Forms.TextBox _manualClientBox;
        private readonly Forms.TextBox _manualProductBox;

        public OrderSelectionState(
            Forms.TabControl tabs,
            Forms.TabPage manualTab,
            Forms.ListBox listBox,
            Forms.TextBox manualOrderIdBox,
            Forms.TextBox manualClientBox,
            Forms.TextBox manualProductBox)
        {
            _tabs = tabs;
            _manualTab = manualTab;
            _listBox = listBox;
            _manualOrderIdBox = manualOrderIdBox;
            _manualClientBox = manualClientBox;
            _manualProductBox = manualProductBox;
        }

        public OrderData? GetSelectedOrder()
        {
            var hasManualInput =
                !string.IsNullOrWhiteSpace(_manualOrderIdBox.Text) ||
                !string.IsNullOrWhiteSpace(_manualClientBox.Text) ||
                !string.IsNullOrWhiteSpace(_manualProductBox.Text);

            if (_tabs.SelectedTab == _manualTab || hasManualInput)
            {
                var orderId = _manualOrderIdBox.Text.Trim();
                var client = _manualClientBox.Text.Trim();
                var product = _manualProductBox.Text.Trim();

                return string.IsNullOrWhiteSpace(orderId) ||
                    string.IsNullOrWhiteSpace(client) ||
                    string.IsNullOrWhiteSpace(product)
                    ? null
                    : new OrderData(orderId, client, product)
                    {
                        Status = "Completed",
                        CompletedAtUtc = DateTime.UtcNow,
                    };
            }

            return _listBox.SelectedItem is OrderDisplay display
                ? display.Order
                : null;
        }
    }

    private sealed class OrderDisplay
    {
        public OrderDisplay(OrderData order)
        {
            Order = order;
            var completedAt = order.CompletedAtUtc is null
                ? "дата не указана"
                : order.CompletedAtUtc.Value.ToLocalTime().ToString("dd.MM.yyyy");
            Text = $"{order.OrderId} | {order.ClientName} / {order.ProductType} | {completedAt}";
        }

        public OrderData Order { get; }

        public string Text { get; }

        public override string ToString() => Text;
    }
}
