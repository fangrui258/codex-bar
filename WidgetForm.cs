using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CodexBar;

internal sealed class WidgetForm : Form
{
    private const int WmNchittest = 0x0084;
    private const int HtCaption = 2;
    private const int WidgetWidth = 286;
    private const int WidgetHeight = 100;
    private readonly AppSettings settings = AppSettings.Load();
    private readonly CodexAppServerClient liveClient = new();
    private readonly Icon appIcon;
    private readonly NotifyIcon trayIcon;
    private readonly System.Windows.Forms.Timer refreshTimer;
    private readonly System.Windows.Forms.Timer positionSaveTimer;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly Pen borderPen = new(Color.FromArgb(42, 58, 49));
    private readonly Pen controlPen = new(Color.FromArgb(130, 150, 138), 1.2f);
    private readonly Font titleFont = new("Segoe UI Semibold", 8.5f);
    private readonly Font percentFont = new("Segoe UI Semibold", 25f);
    private readonly Font resetFont = new("Segoe UI", 9f);
    private readonly SolidBrush dimBrush = new(Color.FromArgb(143, 160, 150));
    private readonly SolidBrush textBrush = new(Color.FromArgb(225, 235, 229));
    private readonly SolidBrush trackBrush = new(Color.FromArgb(31, 39, 34));
    private readonly SolidBrush usageBrush = new(Color.FromArgb(46, 220, 112));
    private UsageSnapshot? snapshot;
    private bool liveConnected;
    private string? readError;
    private bool exiting;

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public WidgetForm()
    {
        Text = "CodexBar";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(WidgetWidth, WidgetHeight);
        MinimumSize = MaximumSize = Size;
        BackColor = Color.FromArgb(8, 10, 9);
        DoubleBuffered = true;
        TopMost = settings.AlwaysOnTop;
        Opacity = Math.Clamp(settings.Opacity, 0.5, 1.0);
        Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? CreateTrayIcon();
        Icon = appIcon;

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = settings.X.HasValue && settings.Y.HasValue
            ? KeepVisible(new Point(settings.X.Value, settings.Y.Value))
            : new Point(area.Right - Width - 24, area.Bottom - Height - 24);

        trayIcon = new NotifyIcon
        {
            Icon = (Icon)appIcon.Clone(),
            Text = "CodexBar — loading weekly usage",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        trayIcon.DoubleClick += (_, _) => ShowWidget();
        ContextMenuStrip = trayIcon.ContextMenuStrip;

        refreshTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Clamp(settings.RefreshSeconds, 5, 300) * 1_000
        };
        refreshTimer.Tick += async (_, _) => await RefreshUsageAsync();
        refreshTimer.Start();

        positionSaveTimer = new System.Windows.Forms.Timer { Interval = 400 };
        positionSaveTimer.Tick += (_, _) =>
        {
            positionSaveTimer.Stop();
            SavePositionNow();
        };

        Shown += async (_, _) => await RefreshUsageAsync();
        LocationChanged += (_, _) => QueuePositionSave();
        FormClosing += OnFormClosing;
        MouseDoubleClick += (_, _) => HideToTray();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        g.DrawRectangle(borderPen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

        g.DrawString("CODEX  ·  WEEKLY LEFT", titleFont, dimBrush, 16, 14);
        DrawWindowControls(g);

        if (snapshot is null)
        {
            var message = readError ?? "Waiting for Codex usage…";
            g.DrawString("—%", percentFont, textBrush, 14, 33);
            g.DrawString(message, resetFont, dimBrush, 104, 46);
            DrawProgress(g, 0, Color.FromArgb(45, 62, 52));
            return;
        }

        var remainingPercent = 100 - snapshot.UsedPercent;
        var color = RemainingColor(remainingPercent);
        usageBrush.Color = color;
        g.DrawString($"{remainingPercent:0}%", percentFont, usageBrush, 14, 33);

        var localReset = snapshot.ResetsAt.ToLocalTime();
        var resetText = localReset.Date == DateTimeOffset.Now.Date
            ? $"Resets today · {localReset:h:mm tt}"
            : $"Resets {localReset:ddd, MMM d} · {localReset:h:mm tt}";
        g.DrawString(resetText, resetFont, textBrush, 104, 41);
        var freshness = liveConnected
            ? $"Live · checked {RelativeTime(snapshot.CapturedAt)}"
            : $"Offline · data {RelativeTime(snapshot.CapturedAt)}";
        g.DrawString(freshness, titleFont, dimBrush, 104, 61);
        DrawProgress(g, remainingPercent, color);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WmNchittest && (int)m.Result == 1)
        {
            var screenPoint = new Point((short)(m.LParam.ToInt64() & 0xffff),
                (short)((m.LParam.ToInt64() >> 16) & 0xffff));
            var clientPoint = PointToClient(screenPoint);
            var overControls = clientPoint.Y <= 30 && clientPoint.X >= Width - 60;
            if (!overControls) m.Result = HtCaption;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && e.Y <= 30 && e.X >= Width - 60)
        {
            if (e.X >= Width - 30) HideToTray();
            else Close();
            return;
        }
        base.OnMouseDown(e);
    }

    private async Task RefreshUsageAsync()
    {
        if (!await refreshGate.WaitAsync(0)) return;
        refreshTimer.Stop();
        try
        {
            snapshot = await liveClient.ReadWeeklyAsync();
            liveConnected = true;
            readError = null;
            if (snapshot is not null)
            {
                var remainingPercent = 100 - snapshot.UsedPercent;
                trayIcon.Text = ($"Codex weekly left: {remainingPercent:0}% · reset " +
                    snapshot.ResetsAt.ToLocalTime().ToString("ddd h:mm tt"))[..Math.Min(63,
                    $"Codex weekly left: {remainingPercent:0}% · reset {snapshot.ResetsAt.ToLocalTime():ddd h:mm tt}".Length)];
            }
            Invalidate();
        }
        catch (Exception ex)
        {
            liveConnected = false;
            readError = snapshot is null ? "Live usage unavailable" : null;
            Debug.WriteLine(ex);
            Invalidate();
        }
        finally
        {
            refreshTimer.Start();
            refreshGate.Release();
        }
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("Show widget", null, (_, _) => ShowWidget());
        menu.Items.Add("Refresh now", null, async (_, _) => await RefreshUsageAsync());

        var refreshInterval = new ToolStripMenuItem("Refresh interval");
        foreach (var option in new[]
                 {
                     (Label: "5 seconds", Seconds: 5),
                     (Label: "15 seconds", Seconds: 15),
                     (Label: "30 seconds", Seconds: 30),
                     (Label: "1 minute", Seconds: 60),
                     (Label: "5 minutes", Seconds: 300)
                 })
        {
            var item = new ToolStripMenuItem(option.Label) { CheckOnClick = true };
            item.Checked = settings.RefreshSeconds == option.Seconds;
            item.Click += (_, _) =>
            {
                settings.RefreshSeconds = option.Seconds;
                refreshTimer.Interval = option.Seconds * 1_000;
                foreach (ToolStripMenuItem peer in refreshInterval.DropDownItems)
                    peer.Checked = peer == item;
                settings.Save();
            };
            refreshInterval.DropDownItems.Add(item);
        }
        menu.Items.Add(refreshInterval);

        var opacity = new ToolStripMenuItem("Transparency");
        foreach (var percent in new[] { 100, 90, 80, 70, 60, 50 })
        {
            var item = new ToolStripMenuItem($"{percent}% opaque") { CheckOnClick = true };
            item.Checked = Math.Abs(settings.Opacity - percent / 100d) < 0.01;
            item.Click += (_, _) =>
            {
                settings.Opacity = percent / 100d;
                Opacity = settings.Opacity;
                foreach (ToolStripMenuItem peer in opacity.DropDownItems) peer.Checked = peer == item;
                settings.Save();
            };
            opacity.DropDownItems.Add(item);
        }
        menu.Items.Add(opacity);

        var topmost = new ToolStripMenuItem("Always on top") { CheckOnClick = true, Checked = settings.AlwaysOnTop };
        topmost.CheckedChanged += (_, _) => { settings.AlwaysOnTop = TopMost = topmost.Checked; settings.Save(); };
        menu.Items.Add(topmost);

        var startup = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = AppSettings.StartsWithWindows };
        startup.CheckedChanged += (_, _) =>
        {
            try { AppSettings.StartsWithWindows = startup.Checked; }
            catch { startup.Checked = !startup.Checked; }
        };
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { exiting = true; Close(); });
        return menu;
    }

    private void DrawWindowControls(Graphics g)
    {
        g.DrawLine(controlPen, Width - 48, 17, Width - 40, 17);
        g.DrawLine(controlPen, Width - 20, 13, Width - 12, 21);
        g.DrawLine(controlPen, Width - 12, 13, Width - 20, 21);
    }

    private void DrawProgress(Graphics g, double percent, Color color)
    {
        var track = new Rectangle(16, Height - 17, Width - 32, 4);
        g.FillRectangle(trackBrush, track);
        usageBrush.Color = color;
        if (percent > 0) g.FillRectangle(usageBrush, track.X, track.Y,
            (int)Math.Round(track.Width * Math.Clamp(percent, 0, 100) / 100), track.Height);
    }

    private static Color RemainingColor(double percent)
    {
        var start = Color.FromArgb(245, 67, 67);
        var mid = Color.FromArgb(246, 190, 55);
        var end = Color.FromArgb(46, 220, 112);
        return percent <= 60 ? Lerp(start, mid, percent / 60) : Lerp(mid, end, (percent - 60) / 40);
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    private static string RelativeTime(DateTimeOffset time)
    {
        var age = DateTimeOffset.UtcNow - time.ToUniversalTime();
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours}h ago";
        return time.ToLocalTime().ToString("MMM d");
    }

    private void ShowWidget()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void HideToTray()
    {
        FlushPositionSave();
        Hide();
        ShowInTaskbar = false;
        trayIcon.ShowBalloonTip(1500, "CodexBar", "Still updating in the notification area.", ToolTipIcon.Info);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!exiting)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        refreshTimer.Stop();
        FlushPositionSave();
        trayIcon.Visible = false;
    }

    private void QueuePositionSave()
    {
        if (WindowState != FormWindowState.Normal) return;
        positionSaveTimer.Stop();
        positionSaveTimer.Start();
    }

    private void FlushPositionSave()
    {
        if (!positionSaveTimer.Enabled) return;
        positionSaveTimer.Stop();
        SavePositionNow();
    }

    private void SavePositionNow()
    {
        if (WindowState != FormWindowState.Normal) return;
        settings.X = Left;
        settings.Y = Top;
        settings.Save();
    }

    private Point KeepVisible(Point desired)
    {
        var screen = Screen.FromPoint(desired).WorkingArea;
        return new Point(Math.Clamp(desired.X, screen.Left, Math.Max(screen.Left, screen.Right - Width)),
            Math.Clamp(desired.Y, screen.Top, Math.Max(screen.Top, screen.Bottom - Height)));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            liveClient.Dispose();
            refreshTimer.Dispose();
            positionSaveTimer.Dispose();
            trayIcon.Dispose();
            appIcon.Dispose();
            borderPen.Dispose();
            controlPen.Dispose();
            titleFont.Dispose();
            percentFont.Dispose();
            resetFont.Dispose();
            dimBrush.Dispose();
            textBrush.Dispose();
            trackBrush.Dispose();
            usageBrush.Dispose();
            refreshGate.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var background = new SolidBrush(Color.FromArgb(8, 10, 9));
        using var ring = new Pen(Color.FromArgb(46, 220, 112), 3);
        g.FillEllipse(background, 2, 2, 28, 28);
        g.DrawArc(ring, 6, 6, 20, 20, -90, 285);
        using var font = new Font("Segoe UI", 10, FontStyle.Bold);
        using var brush = new SolidBrush(Color.White);
        g.DrawString("C", font, brush, 9, 7);
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }
}
