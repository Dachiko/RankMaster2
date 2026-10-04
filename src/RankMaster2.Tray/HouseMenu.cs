using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace RankMaster2.Tray;

/// <summary>
/// The house look for the tray's menus (plan H § 3.5): paper background, a 1.5 px ink outline,
/// rounded corners, Inter 13, the hovered row filled ink with on-ink text, and a dotted rule for a
/// separator.
/// <para/>
/// Text is drawn here rather than by the base renderer because the Inter fonts are private to the
/// process and only GDI+ can draw with them (see <see cref="House.CreateFont"/>).
/// </summary>
internal sealed class HouseMenuRenderer : ToolStripRenderer
{
    private static float Radius => House.Px(10);

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(House.Bg);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        // Stroked on the inside of the window so the outline is not cut off by the rounded region.
        var width = 1.5f * House.Scale;
        var bounds = new RectangleF(width / 2f, width / 2f, e.ToolStrip.Width - width, e.ToolStrip.Height - width);
        using var path = House.RoundedRect(bounds, Radius - width / 2f);
        using var pen = new Pen(House.Ink, width);
        graphics.DrawPath(pen, path);
    }

    /// <summary>The ink-filled shape of a row: its bounds less its inset.</summary>
    private static RectangleF RowBounds(ToolStripItem item)
    {
        var inset = (item as HouseMenuItem)?.Inset ?? Padding.Empty;
        return new RectangleF(inset.Left, inset.Top,
            item.Width - inset.Horizontal, item.Height - inset.Vertical);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled)
            return;

        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = House.RoundedRect(RowBounds(e.Item), House.Px(6));
        using var brush = new SolidBrush(House.Ink);
        graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        using var brush = new SolidBrush(e.Item.Selected ? House.OnInk : House.Ink);

        var row = RowBounds(e.Item);
        var area = new RectangleF(row.Left + House.Px(10), row.Top, row.Width - House.Px(20), row.Height);
        graphics.DrawString(e.Text, e.TextFont ?? e.Item.Font, brush, area, format);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2f;
        House.DrawDottedRule(e.Graphics, House.Rule, House.Px(8), e.Item.Width - House.Px(8), y);
    }
}

/// <summary>A row of the house menu: it draws itself inside its own bounds, so its look does not
/// depend on how a drop-down lays its items out.</summary>
internal sealed class HouseMenuItem : ToolStripMenuItem
{
    /// <summary>The paper left around the row's ink hover shape, in device pixels. The first and last
    /// row carry the menu's top and bottom padding here.</summary>
    public Padding Inset { get; init; }

    public HouseMenuItem(string text, Font font, int width, int top, int bottom, EventHandler onClick)
        : base(text)
    {
        AutoSize = false;
        Font = font;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        Inset = new Padding(House.Px(6), top, House.Px(6), bottom);
        Size = new Size(width, top + House.Px(32) + bottom);
        Click += onClick;
    }
}

/// <summary>Builds the tray's menus in the house look, so the icon's menu and the QR card's menu
/// are made the same way.</summary>
internal static class HouseMenu
{
    /// <summary>
    /// A menu with the house renderer and no image or check margin. The rows size themselves — the
    /// padding around them is part of the first and last row — because a <see
    /// cref="ContextMenuStrip"/> ignores its own <c>Padding</c> and sizes itself from text it
    /// measures with GDI, which cannot see the private Inter fonts. The width is a minimum: the
    /// menu would otherwise be as wide as its text, with the rows spilling out of it.
    /// </summary>
    public static ContextMenuStrip Create(Font font, int logicalWidth, params (string? Text, EventHandler? OnClick)[] rows)
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new HouseMenuRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            BackColor = House.Bg,
            Padding = Padding.Empty,
        };

        var width = House.Px(logicalWidth);
        menu.MinimumSize = new Size(width, 0);
        for (var i = 0; i < rows.Length; i++)
        {
            ToolStripItem item;
            if (rows[i].Text is null)
            {
                item = new ToolStripSeparator
                {
                    AutoSize = false,
                    Margin = Padding.Empty,
                    Padding = Padding.Empty,
                    Size = new Size(width, House.Px(13)),
                };
            }
            else
            {
                // A drop-down keeps 2 px of its own above the first row and below the last.
                var top = i == 0 ? House.Px(8) - 2 : 0;
                var bottom = i == rows.Length - 1 ? House.Px(8) - 2 : 0;
                item = new HouseMenuItem(rows[i].Text!, font, width, top, bottom, rows[i].OnClick!);
            }

            menu.Items.Add(item);
        }

        // The window is clipped to the rounded outline. The region's edge is not anti-aliased;
        // at a 10 px radius that is a pixel of stair-step on four corners, not something to chase.
        menu.SizeChanged += (_, _) =>
        {
            using var path = House.RoundedRect(new RectangleF(0, 0, menu.Width, menu.Height), House.Px(10));
            var previous = menu.Region;
            menu.Region = new Region(path);
            previous?.Dispose();
        };

        return menu;
    }
}
