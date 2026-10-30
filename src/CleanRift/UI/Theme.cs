using System.Drawing;
using System.Runtime.InteropServices;
using CleanRift.Core;

namespace CleanRift.UI;

public sealed class Theme
{
    public Color Window { get; init; }
    public Color WindowAlt { get; init; }
    public Color ListBack { get; init; }
    public Color Text { get; init; }
    public Color Muted { get; init; }
    public Color Border { get; init; }
    public Color Accent { get; init; }
    public Color Selection { get; init; }
    public Color ToolbarBack { get; init; }
    public Color StatusBack { get; init; }
    public Color Hover { get; init; }
    public Color HeaderBack { get; init; }
    public bool IsDark { get; init; }

    public static Theme Light { get; } = new()
    {
        Window = Color.FromArgb(246, 246, 246),
        WindowAlt = Color.FromArgb(252, 252, 252),
        ListBack = Color.White,
        Text = Color.FromArgb(28, 28, 28),
        Muted = Color.FromArgb(110, 110, 110),
        Border = Color.FromArgb(216, 216, 216),
        Accent = Color.FromArgb(15, 123, 138),
        Selection = Color.FromArgb(214, 238, 242),
        ToolbarBack = Color.FromArgb(247, 247, 247),
        StatusBack = Color.FromArgb(240, 240, 240),
        Hover = Color.FromArgb(230, 244, 246),
        HeaderBack = Color.FromArgb(243, 243, 243),
        IsDark = false
    };

    public static Theme Dark { get; } = new()
    {
        Window = Color.FromArgb(32, 32, 32),
        WindowAlt = Color.FromArgb(38, 38, 38),
        ListBack = Color.FromArgb(32, 32, 32),
        Text = Color.FromArgb(240, 240, 240),
        Muted = Color.FromArgb(160, 160, 160),
        Border = Color.FromArgb(65, 65, 65),
        Accent = Color.FromArgb(95, 208, 222),
        Selection = Color.FromArgb(47, 94, 102),
        ToolbarBack = Color.FromArgb(32, 32, 32),
        StatusBack = Color.FromArgb(51, 51, 51),
        Hover = Color.FromArgb(58, 74, 78),
        HeaderBack = Color.FromArgb(25, 25, 25),
        IsDark = true
    };

    public static Theme FromScheme(ColorScheme scheme)
    {
        if (scheme == ColorScheme.Dark)
            return Dark;
        if (scheme == ColorScheme.Light)
            return Light;

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value && value == 0)
                return Dark;
        }
        catch
        {
            // ignore
        }

        return Light;
    }

    public static Theme Current { get; set; } = Light;

    /// <summary>
    /// Applies the current palette to the controls of a dialog. Dark mode uses
    /// dark edits, lists and flat dark buttons; light mode keeps the native
    /// Windows controls.
    /// </summary>
    public static void ApplyDialogTheme(Control root)
    {
        var theme = Current;
        foreach (Control control in root.Controls)
        {
            switch (control)
            {
                case Button button when theme.IsDark &&
                                        button.FlatStyle is FlatStyle.System or FlatStyle.Standard:
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderColor = theme.Border;
                    button.BackColor = theme.WindowAlt;
                    button.ForeColor = theme.Text;
                    break;
                case TextBox textBox:
                    textBox.BackColor = theme.IsDark ? theme.WindowAlt : Color.White;
                    textBox.ForeColor = theme.Text;
                    break;
                case ComboBox combo:
                    combo.BackColor = theme.IsDark ? theme.WindowAlt : Color.White;
                    combo.ForeColor = theme.Text;
                    if (theme.IsDark)
                    {
                        combo.FlatStyle = FlatStyle.Flat;
                        combo.DrawMode = DrawMode.OwnerDrawFixed;
                        combo.DrawItem += (_, e) =>
                        {
                            if (e.Index < 0)
                                return;
                            using (var back = new SolidBrush(theme.WindowAlt))
                                e.Graphics.FillRectangle(back, e.Bounds);
                            var text = e.Index >= 0
                                ? combo.GetItemText(combo.Items[e.Index])
                                : "";
                            TextRenderer.DrawText(e.Graphics, text, e.Font, e.Bounds,
                                theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                        };
                        void ThemeCombo()
                        {
                            try
                            {
                                NativeMethods.SetWindowTheme(combo.Handle, "", "");
                            }
                            catch
                            {
                                // ignore
                            }
                        }

                        combo.HandleCreated += (_, _) => ThemeCombo();
                        if (combo.IsHandleCreated)
                            ThemeCombo();
                    }
                    break;
                case LinkLabel link:
                    link.LinkColor = theme.Accent;
                    link.ActiveLinkColor = theme.Accent;
                    link.VisitedLinkColor = theme.Accent;
                    break;
                case GroupBox group:
                    group.BackColor = theme.Window;
                    group.ForeColor = theme.Text;
                    break;
                case ListView list:
                    list.BackColor = theme.ListBack;
                    list.ForeColor = theme.Text;
                    break;
                case TabPage page:
                    page.BackColor = theme.Window;
                    page.UseVisualStyleBackColor = false;
                    break;
            }

            if (control.HasChildren)
                ApplyDialogTheme(control);
        }
    }

    public void Apply(Control root)
    {
        root.BackColor = Window;
        root.ForeColor = Text;
        ApplyRecursive(root);
    }

    private void ApplyRecursive(Control control)
    {
        switch (control)
        {
            case ListView list:
                list.BackColor = ListBack;
                list.ForeColor = Text;
                break;
            case TextBox text:
                text.BackColor = IsDark ? Color.FromArgb(45, 45, 45) : Color.White;
                text.ForeColor = Text;
                text.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ComboBox combo:
                combo.BackColor = IsDark ? Color.FromArgb(45, 45, 45) : Color.White;
                combo.ForeColor = Text;
                combo.FlatStyle = FlatStyle.Flat;
                break;
            case Button button:
                button.BackColor = IsDark ? Color.FromArgb(56, 56, 56) : Color.FromArgb(240, 240, 240);
                button.ForeColor = Text;
                button.FlatStyle = FlatStyle.System;
                break;
            case LinkLabel link:
                link.LinkColor = Accent;
                link.ActiveLinkColor = IsDark ? Color.FromArgb(120, 180, 255) : Color.FromArgb(0, 80, 150);
                link.VisitedLinkColor = Accent;
                link.BackColor = control.Parent?.BackColor ?? Window;
                break;
            case Label label:
                label.BackColor = Color.Transparent;
                label.ForeColor = label.ForeColor == Color.Empty ? Text : label.ForeColor;
                break;
            case TabControl tabs:
                tabs.BackColor = Window;
                tabs.ForeColor = Text;
                break;
            case TabPage page:
                page.BackColor = Window;
                page.ForeColor = Text;
                break;
            case GroupBox group:
                group.BackColor = Window;
                group.ForeColor = Text;
                break;
            case Panel panel:
                panel.BackColor = panel.BackColor == Color.Empty ? Window : panel.BackColor;
                panel.ForeColor = Text;
                break;
            case CheckBox check:
                check.BackColor = Color.Transparent;
                check.ForeColor = Text;
                break;
            case RadioButton radio:
                radio.BackColor = Color.Transparent;
                radio.ForeColor = Text;
                break;
            case NumericUpDown num:
                num.BackColor = IsDark ? Color.FromArgb(45, 45, 45) : Color.White;
                num.ForeColor = Text;
                break;
        }

        if (control is not LinkLabel)
            control.ForeColor = control is Label or CheckBox or RadioButton ? Text : Text;

        foreach (Control child in control.Controls)
            ApplyRecursive(child);
    }

    public static void ApplyDarkTitleBar(Form form)
    {
        if (!Current.IsDark || !OperatingSystem.IsWindows())
            return;
        try
        {
            var value = 1;
            NativeMethods.DwmSetWindowAttribute(form.Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
                ref value, sizeof(int));
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Switches the native scrollbars of an already created control (list view,
    /// tree view, text box) to their dark variant on Windows 10/11.
    /// </summary>
    public static void ApplyDarkScrollbars(Control control)
    {
        if (!Current.IsDark || !OperatingSystem.IsWindows() || !control.IsHandleCreated)
            return;
        try
        {
            NativeMethods.AllowDarkModeForWindow(control.Handle, true);
            NativeMethods.SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        }
        catch
        {
            // Older Windows builds do not expose the dark scrollbar theme.
        }
    }

    /// <summary>
    /// Applies the dark theme to a tab control and its pages where Windows
    /// exposes the dark tab theme; the page area is always painted dark.
    /// </summary>
    public static void ApplyDarkTabs(TabControl tabs)
    {
        if (!Current.IsDark || !tabs.IsHandleCreated)
            return;
        try
        {
            NativeMethods.SetWindowTheme(tabs.Handle, "DarkMode_Explorer", null);
            foreach (TabPage page in tabs.TabPages)
            {
                page.BackColor = Current.Window;
                page.UseVisualStyleBackColor = false;
                if (page.IsHandleCreated)
                    NativeMethods.SetWindowTheme(page.Handle, "DarkMode_Explorer", null);
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Removes the visual style from combo boxes so their BackColor (dark)
    /// is actually used; the native themed combo ignores it otherwise.
    /// </summary>
    public static void ApplyDarkInputThemes(Control root)
    {
        if (!Current.IsDark)
            return;
        foreach (Control control in root.Controls)
        {
            if (control is ComboBox combo && combo.IsHandleCreated)
            {
                try
                {
                    NativeMethods.SetWindowTheme(combo.Handle, "", "");
                }
                catch
                {
                    // ignore
                }
            }

            if (control is ListView list && list.IsHandleCreated)
            {
                try
                {
                    NativeMethods.SetWindowTheme(list.Handle, "DarkMode_Explorer", null);
                }
                catch
                {
                    // ignore
                }
            }

            if (control.HasChildren)
                ApplyDarkInputThemes(control);
        }
    }
}

/// <summary>
/// ToolStrip renderer that follows the current light/dark theme instead of the
/// stock Office colors.
/// </summary>
public sealed class ThemedColorTable : ProfessionalColorTable
{
    private readonly Theme _theme;

    public ThemedColorTable(Theme theme)
    {
        _theme = theme;
        UseSystemColors = false;
    }

    public override Color ToolStripGradientBegin => _theme.ToolbarBack;
    public override Color ToolStripGradientMiddle => _theme.ToolbarBack;
    public override Color ToolStripGradientEnd => _theme.ToolbarBack;
    public override Color ToolStripBorder => _theme.Border;
    public override Color MenuStripGradientBegin => _theme.ToolbarBack;
    public override Color MenuStripGradientEnd => _theme.ToolbarBack;
    public override Color MenuItemSelected => _theme.Hover;
    public override Color MenuItemSelectedGradientBegin => _theme.Hover;
    public override Color MenuItemSelectedGradientEnd => _theme.Hover;
    public override Color MenuItemBorder => _theme.Border;
    public override Color MenuItemPressedGradientBegin => _theme.Window;
    public override Color MenuItemPressedGradientEnd => _theme.Window;
    public override Color ButtonSelectedHighlight => _theme.Hover;
    public override Color ButtonSelectedGradientBegin => _theme.Hover;
    public override Color ButtonSelectedGradientEnd => _theme.Hover;
    public override Color ButtonSelectedBorder => _theme.Border;
    public override Color ButtonPressedGradientBegin => _theme.Selection;
    public override Color ButtonPressedGradientEnd => _theme.Selection;
    public override Color ButtonCheckedGradientBegin => _theme.Selection;
    public override Color ButtonCheckedGradientEnd => _theme.Selection;
    public override Color SeparatorDark => _theme.Border;
    public override Color SeparatorLight => _theme.Border;
    public override Color ImageMarginGradientBegin => _theme.Window;
    public override Color ImageMarginGradientMiddle => _theme.Window;
    public override Color ImageMarginGradientEnd => _theme.Window;
    public override Color CheckBackground => _theme.Selection;
    public override Color CheckSelectedBackground => _theme.Selection;
    public override Color CheckPressedBackground => _theme.Selection;
    public override Color GripDark => _theme.Border;
    public override Color GripLight => _theme.Border;
    public override Color OverflowButtonGradientBegin => _theme.ToolbarBack;
    public override Color OverflowButtonGradientMiddle => _theme.ToolbarBack;
    public override Color OverflowButtonGradientEnd => _theme.ToolbarBack;
}

public sealed class ThemedToolStripRenderer : ToolStripProfessionalRenderer
{
    private readonly Theme _theme;

    public Color? BackgroundOverride { get; init; }

    public ThemedToolStripRenderer(Theme theme) : base(new ThemedColorTable(theme))
    {
        _theme = theme;
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(BackgroundOverride ?? _theme.ToolbarBack);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(_theme.Border);
        var bounds = e.ToolStrip.ClientRectangle;
        e.Graphics.DrawLine(pen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? _theme.Text : _theme.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled)
        {
            base.OnRenderMenuItemBackground(e);
            return;
        }

        using var brush = new SolidBrush(_theme.Hover);
        e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
        using var pen = new Pen(_theme.Accent);
        e.Graphics.DrawRectangle(pen, 0, 0, e.Item.Width - 1, e.Item.Height - 1);
    }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected && !e.Item.Pressed && !(e.Item is ToolStripButton { Checked: true }))
        {
            base.OnRenderButtonBackground(e);
            return;
        }

        var color = e.Item.Pressed ? _theme.Selection : _theme.Hover;
        using var brush = new SolidBrush(color);
        var bounds = new Rectangle(0, 0, e.Item.Width, e.Item.Height);
        e.Graphics.FillRectangle(brush, bounds);
        using var pen = new Pen(_theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, bounds.Width - 1, bounds.Height - 1);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(_theme.Border);
        var y = e.Item.Height / 2;
        if (e.Vertical)
            e.Graphics.DrawLine(pen, e.Item.Width / 2, 3, e.Item.Width / 2, e.Item.Height - 4);
        else
            e.Graphics.DrawLine(pen, 3, y, e.Item.Width - 4, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = _theme.Text;
        base.OnRenderArrow(e);
    }
}
