using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using CleanRift.UI;

namespace CleanRift.UI.Controls;

/// <summary>
/// The secondary toolbar: category buttons (Apps, System, Hidden, Microsoft
/// Store, Recently Installed) plus the Install and Trace command, drawn with
/// the same light/dark palette as the rest of the window.
/// </summary>
public sealed class CategoryStrip : Control
{
    public sealed class Item
    {
        public string Key { get; init; } = "";
        public string Text { get; init; } = "";
        public int IconId { get; init; }
        public string ToolTip { get; init; } = "";
        public int Count { get; set; }
        public bool IsAction { get; init; }
        public Rectangle Bounds { get; set; }
    }

    private readonly List<Item> _items = new();
    private readonly ToolTip _toolTip = new() { InitialDelay = 400, ReshowDelay = 200 };
    private Item? _hover;

    public event Action<Item>? ItemClicked;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string SelectedKey { get; set; } = "apps";

    public CategoryStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Height = 32;
        Dock = DockStyle.Bottom;
        Padding = new Padding(4, 2, 4, 0);
        Font = new Font("Segoe UI", 9f);
    }

    public void SetItems(IEnumerable<Item> items)
    {
        _items.Clear();
        _items.AddRange(items);
        Recalculate();
        Invalidate();
    }

    public void UpdateCount(string key, int count)
    {
        var item = _items.FirstOrDefault(i => i.Key == key);
        if (item == null)
            return;
        item.Count = count;
        Recalculate();
        Invalidate();
    }

    public void Select(string key)
    {
        SelectedKey = key;
        Invalidate();
    }

    private void Recalculate()
    {
        var x = Padding.Left;
        var available = ClientSize.Width - Padding.Horizontal;
        var actionWidth = 0;
        foreach (var item in _items.Where(i => i.IsAction))
            actionWidth += Measure(item).Width;

        var normal = _items.Where(i => !i.IsAction).ToList();
        var widths = normal.Select(i => Measure(i).Width).ToList();
        var total = widths.Sum();
        var maxNormal = Math.Max(0, available - actionWidth - 12);

        // Water-fill: keep short captions at their natural width and only shrink
        // the widest ones, so a long translation ("Aplicaciones de la Tienda…")
        // does not ellipsize every other category on a narrow window.
        if (total > maxNormal && total > 0)
        {
            var budget = maxNormal;
            var pending = Enumerable.Range(0, normal.Count).ToList();
            while (pending.Count > 0)
            {
                var pendingTotal = pending.Sum(i => widths[i]);
                if (pendingTotal <= budget)
                    break;

                var fair = budget / pending.Count;
                var fitting = pending.Where(i => widths[i] <= fair).ToList();
                if (fitting.Count == 0)
                {
                    var scale = (double)budget / pendingTotal;
                    foreach (var i in pending)
                        widths[i] = Math.Max(48, (int)Math.Round(widths[i] * scale));
                    break;
                }

                foreach (var i in fitting)
                    budget -= widths[i];
                pending.RemoveAll(fitting.Contains);
            }
        }

        for (var i = 0; i < normal.Count; i++)
        {
            var item = normal[i];
            item.Bounds = new Rectangle(x, Padding.Top, widths[i], Height - Padding.Vertical);
            x += widths[i];
        }

        var right = ClientSize.Width - Padding.Right;
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            if (!item.IsAction)
                continue;
            var width = Measure(item).Width;
            item.Bounds = new Rectangle(right - width, Padding.Top, width, Height - Padding.Vertical);
            right -= width;
        }

        // If the normal buttons are wider than the remaining space, allow them to
        // run under the action button (the strip is horizontally clipped).
        foreach (var item in normal.Where(i => i.Bounds.Right > right - 8))
            item.Bounds = Rectangle.FromLTRB(item.Bounds.Left, item.Bounds.Top, right - 8, item.Bounds.Bottom);
    }

    private Size Measure(Item item)
    {
        var text = DisplayText(item);
        var textWidth = TextRenderer.MeasureText(text, Font).Width;
        var width = Math.Max(62, textWidth + 34);
        if (item.IsAction)
            width += 10;
        return new Size(width, Height - Padding.Vertical);
    }

    private static string DisplayText(Item item)
    {
        // SecondaryToolbar/btnRecentlyInstalled contains the %u placeholder;
        // the other captions are plain.
        return item.Count > 0 && item.Text.Contains("%u", StringComparison.Ordinal)
            ? item.Text.Replace("%u", item.Count.ToString())
            : item.Text;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var theme = Theme.Current;
        using (var brush = new SolidBrush(theme.ToolbarBack))
            e.Graphics.FillRectangle(brush, ClientRectangle);

        var firstAction = _items.FirstOrDefault(i => i.IsAction);
        if (firstAction != null)
        {
            using var separator = new Pen(theme.Border);
            e.Graphics.DrawLine(separator, firstAction.Bounds.Left - 6, 5,
                firstAction.Bounds.Left - 6, Height - 6);
        }

        foreach (var item in _items)
        {
            var selected = !item.IsAction && item.Key == SelectedKey;
            var hover = ReferenceEquals(_hover, item);
            var bounds = item.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                continue;

            if (selected)
            {
                using var brush = new SolidBrush(theme.IsDark
                    ? Color.FromArgb(52, 52, 52)
                    : Color.FromArgb(205, 228, 248));
                using var pen = new Pen(theme.Border);
                e.Graphics.FillRectangle(brush, bounds);
                e.Graphics.DrawRectangle(pen, bounds.Left, bounds.Top, bounds.Width - 1, bounds.Height - 1);
            }
            else if (hover)
            {
                using var brush = new SolidBrush(theme.Hover);
                e.Graphics.FillRectangle(brush, bounds);
            }

            var icon = AppIcons.Get(item.IconId, 16);
            var text = DisplayText(item);
            var textSize = TextRenderer.MeasureText(text, Font);
            var contentWidth = (icon != null ? 16 + 6 : 0) + textSize.Width;
            var startX = bounds.Left + Math.Max(6, (bounds.Width - contentWidth) / 2);
            if (icon != null)
            {
                var iconY = bounds.Top + (bounds.Height - 16) / 2;
                e.Graphics.DrawImage(icon, new Rectangle(startX, iconY, 16, 16));
                startX += 16 + 6;
            }

            var textRect = new Rectangle(startX, bounds.Top + 1, Math.Max(10, bounds.Right - startX - 4), bounds.Height - 2);
            TextRenderer.DrawText(e.Graphics, text, Font, textRect, theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        using var border = new Pen(theme.Border);
        e.Graphics.DrawLine(border, 0, 0, Width, 0);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var item = _items.FirstOrDefault(i => i.Bounds.Contains(e.Location));
        if (!ReferenceEquals(item, _hover))
        {
            _hover = item;
            Invalidate();
            if (item != null && !string.IsNullOrEmpty(item.ToolTip))
            {
                _toolTip.Show(item.ToolTip, this, e.X + 12, e.Y + 18, 6000);
            }
            else
            {
                _toolTip.Hide(this);
            }
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != null)
        {
            _hover = null;
            Invalidate();
        }

        _toolTip.Hide(this);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var item = _items.FirstOrDefault(i => i.Bounds.Contains(e.Location));
        if (item == null)
            return;
        if (!item.IsAction)
            SelectedKey = item.Key;
        Invalidate();
        ItemClicked?.Invoke(item);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Recalculate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Recalculate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _toolTip.Dispose();
        base.Dispose(disposing);
    }
}
