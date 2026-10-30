using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using CleanRift.Core;

namespace CleanRift.UI;

/// <summary>
/// CleanRift's own icon set. Icons are drawn as vectors at the requested size
/// instead of loading image files, so the application ships no third-party
/// artwork. The identifiers are stable names used by the UI.
/// </summary>
public static class AppIcons
{
    public const int Uninstaller = 1;
    public const int Remove = 2;
    public const int Startup = 3;
    public const int Apps = 4;
    public const int System = 5;
    public const int Hidden = 6;
    public const int Store = 7;
    public const int Recent = 8;
    public const int InstallTrace = 9;
    public const int Refresh = 10;
    public const int Tools = 11;
    public const int Help = 12;
    public const int Export = 13;
    public const int Search = 14;
    public const int Delete = 15;
    public const int Warning = 16;
    public const int Info = 17;
    public const int Shield = 18;
    public const int Google = 19;
    public const int Globe = 20;
    public const int FolderSearch = 21;
    public const int Add = 22;
    public const int RemoveEntry = 23;
    public const int OpenFolder = 24;
    public const int Home = 25;
    public const int Cart = 26;
    public const int Windows = 27;
    public const int Disable = 28;
    public const int Clock = 29;
    public const int Install = 30;
    public const int Error = 31;
    public const int Registry = 32;
    public const int FileSearch = 33;
    public const int Report = 34;
    public const int Eye = 35;
    public const int BoxUp = 36;

    private static readonly Dictionary<int, Image> Cache = new();

    public static Image? Get(int id, int size = 16)
    {
        var key = ((id * 1000) + size) * 2 + (Theme.Current.IsDark ? 1 : 0);
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached))
                return cached;
        }

        try
        {
            var bitmap = new Bitmap(size, size);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

            var ink = Theme.Current.IsDark
                ? Color.FromArgb(224, 236, 238)
                : Color.FromArgb(45, 70, 75);
            var accent = Theme.Current.Accent;
            DrawIcon(graphics, id, size, ink, accent);

            lock (Cache)
                Cache[key] = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public static void Reset()
    {
        lock (Cache)
        {
            foreach (var image in Cache.Values)
                image.Dispose();
            Cache.Clear();
        }
    }

    public static Icon? AppIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(path))
                return new Icon(path, 64, 64);
        }
        catch
        {
            // ignore
        }

        return IconCache.LoadAppIcon();
    }

    private static void DrawIcon(Graphics g, int id, int size, Color ink, Color accent)
    {
        var scale = size / 64f;
        g.ScaleTransform(scale, scale);

        var stroke = 5f;
        using var pen = new Pen(ink, stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var accentPen = new Pen(accent, stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var inkBrush = new SolidBrush(ink);
        using var accentBrush = new SolidBrush(accent);

        switch (id)
        {
            case Uninstaller:
                DrawBox(g, pen, accentPen, accentBrush);
                break;
            case Remove:
            case Delete:
                DrawTrash(g, pen, accentPen);
                break;
            case Startup:
                DrawPower(g, accentPen);
                break;
            case Apps:
                DrawApps(g, accentBrush, inkBrush);
                break;
            case System:
                DrawMonitor(g, pen, accentPen);
                break;
            case Hidden:
                DrawEye(g, pen, false);
                break;
            case Store:
                DrawBag(g, pen, accentPen);
                break;
            case Recent:
            case Clock:
                DrawClock(g, pen);
                break;
            case InstallTrace:
                DrawMagnifier(g, pen, accentPen, true);
                break;
            case Refresh:
                DrawRefresh(g, accentPen);
                break;
            case Tools:
                DrawSliders(g, pen, accentPen);
                break;
            case Help:
                DrawHelp(g, accentPen, accentBrush);
                break;
            case Info:
                DrawInfo(g, accentPen, accentBrush);
                break;
            case Export:
            case BoxUp:
                DrawOpenBox(g, pen, accentPen, accentBrush);
                break;
            case Search:
                DrawMagnifier(g, pen, accentPen, false);
                break;
            case Warning:
                DrawWarning(g, accentPen, accentBrush);
                break;
            case Shield:
                DrawShield(g, pen, accentPen);
                break;
            case Google:
            case Globe:
                DrawGlobe(g, pen, accentPen, accentBrush);
                break;
            case FolderSearch:
                DrawFolder(g, pen, accentPen, true);
                break;
            case Add:
                DrawPlusMinus(g, accentPen, accentBrush, true);
                break;
            case RemoveEntry:
                DrawPlusMinus(g, accentPen, accentBrush, false);
                break;
            case OpenFolder:
                DrawFolder(g, pen, accentPen, false);
                break;
            case Home:
                DrawHome(g, pen, accentPen);
                break;
            case Cart:
                DrawCart(g, pen, accentPen);
                break;
            case Windows:
                DrawGrid(g, accentBrush);
                break;
            case Disable:
                DrawDisable(g, accentPen, inkBrush);
                break;
            case Install:
                DrawInstall(g, pen, accentPen);
                break;
            case Error:
                DrawError(g, accentPen, accentBrush);
                break;
            case Registry:
                DrawRegistry(g, pen, accentPen);
                break;
            case FileSearch:
                DrawFile(g, pen, accentPen, true);
                break;
            case Report:
                DrawFile(g, pen, accentPen, false);
                break;
            case Eye:
                DrawEye(g, pen, true);
                break;
            default:
                DrawGlyph(g, "•", inkBrush, 40f);
                break;
        }
    }

    private static void DrawBox(Graphics g, Pen pen, Pen accentPen, Brush accentBrush)
    {
        // Open box with an arrow leaving it.
        using var box = new GraphicsPath();
        box.AddLine(14, 30, 14, 52);
        box.AddLine(14, 52, 50, 52);
        box.AddLine(50, 52, 50, 30);
        box.AddLine(50, 30, 40, 36);
        box.AddLine(40, 36, 24, 36);
        box.AddLine(24, 36, 14, 30);
        g.DrawPath(pen, box);

        using var arrow = new GraphicsPath();
        arrow.AddLine(32, 34, 32, 14);
        arrow.AddLine(32, 14, 24, 22);
        arrow.AddLine(32, 14, 40, 22);
        g.DrawPath(accentPen, arrow);
        g.FillEllipse(accentBrush, 29f, 24f, 6f, 6f);
    }

    private static void DrawOpenBox(Graphics g, Pen pen, Pen accentPen, Brush accentBrush)
    {
        using var box = new GraphicsPath();
        box.AddLine(12, 34, 12, 54);
        box.AddLine(12, 54, 52, 54);
        box.AddLine(52, 54, 52, 34);
        g.DrawPath(pen, box);
        using var arrow = new GraphicsPath();
        arrow.AddLine(32, 40, 32, 16);
        arrow.AddLine(32, 16, 24, 24);
        arrow.AddLine(32, 16, 40, 24);
        g.DrawPath(accentPen, arrow);
        g.FillEllipse(accentBrush, 29f, 40f, 6f, 6f);
    }

    private static void DrawTrash(Graphics g, Pen pen, Pen accentPen)
    {
        g.DrawLine(pen, 18, 20, 46, 20);
        g.DrawLine(pen, 25, 20, 27, 14);
        g.DrawLine(pen, 39, 20, 37, 14);
        g.DrawLine(pen, 27, 14, 37, 14);
        using var body = new GraphicsPath();
        body.AddLine(20, 24, 24, 52);
        body.AddLine(24, 52, 40, 52);
        body.AddLine(40, 52, 44, 24);
        g.DrawPath(pen, body);
        g.DrawLine(accentPen, 28, 30, 29, 46);
        g.DrawLine(accentPen, 36, 30, 35, 46);
    }

    private static void DrawPower(Graphics g, Pen accentPen)
    {
        using var arc = new GraphicsPath();
        arc.AddArc(16, 16, 32, 32, -55, 290);
        g.DrawPath(accentPen, arc);
        g.DrawLine(accentPen, 32, 12, 32, 32);
    }

    private static void DrawApps(Graphics g, Brush accent, Brush ink)
    {
        using var path = new GraphicsPath();
        path.AddRectangle(new RectangleF(14, 14, 16, 16));
        path.AddRectangle(new RectangleF(34, 14, 16, 16));
        path.AddRectangle(new RectangleF(14, 34, 16, 16));
        path.AddRectangle(new RectangleF(34, 34, 16, 16));
        g.FillPath(ink, path);
        g.FillRectangle(accent, 18, 18, 8, 8);
        g.FillRectangle(accent, 38, 18, 8, 8);
        g.FillRectangle(accent, 18, 38, 8, 8);
        g.FillRectangle(accent, 38, 38, 8, 8);
    }

    private static void DrawMonitor(Graphics g, Pen pen, Pen accentPen)
    {
        using var screen = new GraphicsPath();
        screen.AddRectangle(new RectangleF(10, 14, 44, 30));
        g.DrawPath(pen, screen);
        g.DrawLine(pen, 32, 44, 32, 52);
        g.DrawLine(pen, 22, 52, 42, 52);
        g.DrawLine(accentPen, 16, 20, 48, 20);
        g.DrawLine(accentPen, 16, 28, 38, 28);
    }

    private static void DrawSliders(Graphics g, Pen pen, Pen accentPen)
    {
        g.DrawLine(pen, 12, 20, 52, 20);
        g.DrawLine(pen, 12, 32, 52, 32);
        g.DrawLine(pen, 12, 44, 52, 44);
        g.FillEllipse(accentPen.Brush, 24, 15, 10, 10);
        g.FillEllipse(accentPen.Brush, 38, 27, 10, 10);
        g.FillEllipse(accentPen.Brush, 28, 39, 10, 10);
    }

    private static void DrawHelp(Graphics g, Pen accentPen, Brush accentBrush)
    {
        g.DrawEllipse(accentPen, 10, 10, 44, 44);
        DrawGlyph(g, "?", accentBrush, 34f);
    }

    private static void DrawEye(Graphics g, Pen pen, bool open)
    {
        using var path = new GraphicsPath();
        path.AddBezier(12, 32, 22, 18, 42, 18, 52, 32);
        path.AddBezier(52, 32, 42, 46, 22, 46, 12, 32);
        g.DrawPath(pen, path);
        g.DrawEllipse(pen, 26, 26, 12, 12);
        if (!open)
            g.DrawLine(pen, 16, 50, 48, 14);
    }

    private static void DrawBag(Graphics g, Pen pen, Pen accentPen)
    {
        using var path = new GraphicsPath();
        path.AddLine(16, 24, 48, 24);
        path.AddLine(48, 24, 45, 52);
        path.AddLine(45, 52, 19, 52);
        path.AddLine(19, 52, 16, 24);
        g.DrawPath(pen, path);
        using var handle = new GraphicsPath();
        handle.AddArc(24, 12, 16, 20, 180, 180);
        g.DrawPath(accentPen, handle);
    }

    private static void DrawClock(Graphics g, Pen pen)
    {
        g.DrawEllipse(pen, 12, 12, 40, 40);
        g.DrawLine(pen, 32, 32, 32, 20);
        g.DrawLine(pen, 32, 32, 41, 38);
    }

    private static void DrawMagnifier(Graphics g, Pen pen, Pen accentPen, bool plus)
    {
        g.DrawEllipse(pen, 14, 14, 26, 26);
        g.DrawLine(pen, 37, 37, 51, 51);
        if (plus)
        {
            g.DrawLine(accentPen, 21, 27, 33, 27);
            g.DrawLine(accentPen, 27, 21, 27, 33);
        }
    }

    private static void DrawRefresh(Graphics g, Pen accentPen)
    {
        using var path = new GraphicsPath();
        path.AddArc(16, 16, 32, 32, 35, 285);
        g.DrawPath(accentPen, path);
        using var head = new GraphicsPath();
        head.AddLine(48, 16, 50, 28);
        head.AddLine(50, 28, 38, 26);
        g.DrawPath(accentPen, head);
    }

    private static void DrawWrench(Graphics g, Pen pen)
    {
        using var path = new GraphicsPath();
        path.AddLine(20, 44, 40, 24);
        path.AddLine(40, 24, 46, 18);
        path.AddLine(46, 18, 52, 24);
        path.AddLine(52, 24, 46, 30);
        path.AddLine(46, 30, 26, 50);
        path.AddLine(26, 50, 20, 44);
        g.DrawPath(pen, path);
        g.DrawEllipse(pen, 14, 38, 12, 12);
    }

    private static void DrawGlyph(Graphics g, string text, Brush brush, float pixels)
    {
        using var font = new Font("Segoe UI", pixels, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString(text, font, brush, new RectangleF(0, 0, 64, 64), format);
    }

    private static void DrawInfo(Graphics g, Pen accentPen, Brush accentBrush)
    {
        g.DrawEllipse(accentPen, 12, 12, 40, 40);
        g.FillEllipse(accentBrush, 29, 21, 6, 6);
        g.DrawLine(accentPen, 32, 32, 32, 45);
    }

    private static void DrawWarning(Graphics g, Pen accentPen, Brush accentBrush)
    {
        using var path = new GraphicsPath();
        path.AddLine(32, 10, 56, 52);
        path.AddLine(56, 52, 8, 52);
        path.AddLine(8, 52, 32, 10);
        g.DrawPath(accentPen, path);
        g.DrawLine(accentPen, 32, 24, 32, 38);
        g.FillEllipse(accentBrush, 29, 43, 6, 6);
    }

    private static void DrawShield(Graphics g, Pen pen, Pen accentPen)
    {
        using var path = new GraphicsPath();
        path.AddLine(32, 10, 51, 18);
        path.AddLine(51, 18, 49, 38);
        path.AddLine(49, 38, 32, 54);
        path.AddLine(32, 54, 15, 38);
        path.AddLine(15, 38, 13, 18);
        path.AddLine(13, 18, 32, 10);
        g.DrawPath(pen, path);
        g.DrawLine(accentPen, 24, 32, 30, 38);
        g.DrawLine(accentPen, 30, 38, 41, 25);
    }

    private static void DrawGlobe(Graphics g, Pen pen, Pen accentPen, Brush accentBrush)
    {
        g.DrawEllipse(pen, 12, 12, 40, 40);
        g.DrawEllipse(accentPen, 24, 12, 16, 40);
        g.DrawLine(pen, 13, 32, 51, 32);
        g.DrawArc(pen, 12, 20, 40, 16, 0, 180);
        g.DrawArc(pen, 12, 28, 40, 16, 0, 180);
        g.FillEllipse(accentBrush, 29, 29, 6, 6);
    }

    private static void DrawFolder(Graphics g, Pen pen, Pen accentPen, bool search)
    {
        using var path = new GraphicsPath();
        path.AddLine(8, 20, 20, 20);
        path.AddLine(20, 20, 24, 26);
        path.AddLine(24, 26, 48, 26);
        path.AddLine(48, 26, 44, 50);
        path.AddLine(44, 50, 12, 50);
        path.AddLine(12, 50, 8, 20);
        g.DrawPath(pen, path);
        if (search)
        {
            g.DrawEllipse(accentPen, 36, 34, 16, 16);
            g.DrawLine(accentPen, 49, 47, 55, 53);
        }
    }

    private static void DrawPlusMinus(Graphics g, Pen accentPen, Brush accentBrush, bool plus)
    {
        g.DrawEllipse(accentPen, 12, 12, 40, 40);
        g.DrawLine(accentPen, 22, 32, 42, 32);
        if (plus)
            g.DrawLine(accentPen, 32, 22, 32, 42);
    }

    private static void DrawHome(Graphics g, Pen pen, Pen accentPen)
    {
        using var roof = new GraphicsPath();
        roof.AddLine(10, 30, 32, 12);
        roof.AddLine(32, 12, 54, 30);
        g.DrawPath(accentPen, roof);
        using var body = new GraphicsPath();
        body.AddLine(16, 30, 16, 52);
        body.AddLine(16, 52, 48, 52);
        body.AddLine(48, 52, 48, 30);
        g.DrawPath(pen, body);
        g.DrawRectangle(pen, 28, 38, 8, 14);
    }

    private static void DrawCart(Graphics g, Pen pen, Pen accentPen)
    {
        g.DrawLine(pen, 10, 16, 18, 16);
        g.DrawLine(pen, 18, 16, 24, 42);
        g.DrawLine(pen, 24, 42, 48, 42);
        g.DrawLine(pen, 20, 24, 50, 24);
        g.DrawLine(accentPen, 50, 24, 46, 36);
        g.DrawLine(accentPen, 46, 36, 24, 36);
        g.DrawEllipse(pen, 24, 46, 6, 6);
        g.DrawEllipse(pen, 42, 46, 6, 6);
    }

    private static void DrawGrid(Graphics g, Brush accent)
    {
        g.FillRectangle(accent, 14, 14, 15, 15);
        g.FillRectangle(accent, 35, 14, 15, 15);
        g.FillRectangle(accent, 14, 35, 15, 15);
        g.FillRectangle(accent, 35, 35, 15, 15);
    }

    private static void DrawDisable(Graphics g, Pen accentPen, Brush ink)
    {
        g.DrawEllipse(accentPen, 12, 12, 40, 40);
        g.DrawLine(accentPen, 20, 20, 44, 44);
    }

    private static void DrawInstall(Graphics g, Pen pen, Pen accentPen)
    {
        g.DrawLine(pen, 14, 44, 14, 52);
        g.DrawLine(pen, 14, 52, 50, 52);
        g.DrawLine(pen, 50, 52, 50, 44);
        g.DrawLine(accentPen, 32, 10, 32, 38);
        using var head = new GraphicsPath();
        head.AddLine(32, 38, 22, 28);
        head.AddLine(32, 38, 42, 28);
        g.DrawPath(accentPen, head);
    }

    private static void DrawError(Graphics g, Pen accentPen, Brush accentBrush)
    {
        g.DrawEllipse(accentPen, 12, 12, 40, 40);
        g.DrawLine(accentPen, 23, 23, 41, 41);
        g.DrawLine(accentPen, 41, 23, 23, 41);
    }

    private static void DrawRegistry(Graphics g, Pen pen, Pen accentPen)
    {
        g.DrawEllipse(pen, 16, 10, 32, 12);
        g.DrawLine(pen, 16, 16, 16, 46);
        g.DrawLine(pen, 48, 16, 48, 46);
        using var bottom = new GraphicsPath();
        bottom.AddArc(16, 40, 32, 12, 0, 180);
        g.DrawPath(pen, bottom);
        g.DrawArc(accentPen, 16, 24, 32, 10, 0, 180);
        g.DrawArc(accentPen, 16, 33, 32, 10, 0, 180);
    }

    private static void DrawFile(Graphics g, Pen pen, Pen accentPen, bool search)
    {
        using var page = new GraphicsPath();
        page.AddLine(18, 8, 40, 8);
        page.AddLine(40, 8, 50, 18);
        page.AddLine(50, 18, 50, 54);
        page.AddLine(50, 54, 18, 54);
        page.AddLine(18, 54, 18, 8);
        g.DrawPath(pen, page);
        g.DrawLine(pen, 40, 8, 40, 18);
        g.DrawLine(pen, 40, 18, 50, 18);
        if (search)
        {
            g.DrawEllipse(accentPen, 28, 26, 16, 16);
            g.DrawLine(accentPen, 41, 39, 48, 46);
        }
        else
        {
            g.DrawLine(pen, 25, 28, 43, 28);
            g.DrawLine(pen, 25, 36, 43, 36);
            g.DrawLine(pen, 25, 44, 36, 44);
        }
    }
}
