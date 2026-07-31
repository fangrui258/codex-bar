using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CodexBar;

internal sealed class WidgetForm : Form
{
    private const int WmNchittest = 0x0084;
    private const int HtCaption = 2;
    private const int WidgetWidth = 286;
    private const int WidgetHeight = 100;
    private const double FullAvailabilityUsedPercent = 0.001;
    private static readonly TimeSpan ResetTimeMatchTolerance = TimeSpan.FromHours(1);
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
    private readonly SolidBrush statusOkBrush = new(Color.FromArgb(84, 235, 120));
    private readonly SolidBrush statusOfflineBrush = new(Color.FromArgb(239, 128, 128));
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

        var titleY = 14f;
        var codexHeight = g.MeasureString("CODEX", titleFont).Height;
        var dotY = titleY + (codexHeight - 6f) / 2f;
        var statusBrush = liveConnected ? statusOkBrush : statusOfflineBrush;
        g.FillEllipse(statusBrush, 10, dotY, 6, 6);
        g.DrawRectangle(borderPen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

        g.DrawString("CODEX  ·  WEEKLY LEFT", titleFont, dimBrush, 20, titleY);
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
                await NotifyIfUsageLimitReachedAsync(snapshot);
            }
            Invalidate();
        }
        catch (Exception ex)
        {
            liveConnected = false;
            readError = snapshot is null ? "Usage unavailable" : null;
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
        startup.Click += (_, _) =>
        {
            try { AppSettings.StartsWithWindows = startup.Checked; }
            catch { startup.Checked = !startup.Checked; }
        };
        menu.Items.Add(startup);
        menu.Items.Add("Usage notifications…", null, (_, _) => ShowNotificationSettings());
        menu.Items.Add("Send test alert", null, async (_, _) =>
        {
            var sent = await SendTestUsageAlertAsync();
            trayIcon.ShowBalloonTip(
                sent ? 2500 : 4500,
                "CodexBar",
                sent ? "Test usage alert triggered." : "Could not trigger test usage alert.",
                sent ? ToolTipIcon.Info : ToolTipIcon.Warning);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { exiting = true; Close(); });
        return menu;
    }

    private void ShowNotificationSettings()
    {
        using var dialog = new Form
        {
            Text = "Usage notification settings",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(540, 360),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false
        };

        var header = new Label
        {
            Location = new Point(12, 12),
            AutoSize = false,
            Size = new Size(510, 50),
            Text = "When weekly capacity resets to 100% available, CodexBar can send one notification email to your configured address.\n" +
                   "If SMTP fields are blank, CodexBar opens your default mail app with a prefilled draft instead.",
            TextAlign = ContentAlignment.TopLeft
        };
        header.MaximumSize = new Size(510, 0);

        var enabled = new CheckBox
        {
            Text = "Notify when weekly capacity resets to 100% available",
            Location = new Point(12, 50),
            AutoSize = true,
            Checked = settings.NotifyOnUsageLimitReached
        };

        var toLabel = new Label { Location = new Point(12, 84), AutoSize = true, Text = "Notification address:" };
        var toInput = new TextBox { Location = new Point(190, 82), Width = 334, Text = settings.UsageLimitNotificationEmail ?? string.Empty };

        var hostLabel = new Label { Location = new Point(12, 118), AutoSize = true, Text = "SMTP host:" };
        var hostInput = new TextBox { Location = new Point(190, 116), Width = 220, Text = settings.SmtpHost ?? string.Empty };

        var portLabel = new Label { Location = new Point(12, 152), AutoSize = true, Text = "SMTP port:" };
        var portInput = new TextBox { Location = new Point(190, 150), Width = 90, Text = settings.SmtpPort.ToString(System.Globalization.CultureInfo.InvariantCulture) };

        var ssl = new CheckBox
        {
            Text = "Use SSL/TLS",
            Location = new Point(290, 150),
            AutoSize = true,
            Checked = settings.SmtpUseSsl
        };

        var userLabel = new Label { Location = new Point(12, 186), AutoSize = true, Text = "SMTP user:" };
        var userInput = new TextBox { Location = new Point(190, 184), Width = 334, Text = settings.SmtpUser ?? string.Empty };

        var passwordLabel = new Label { Location = new Point(12, 220), AutoSize = true, Text = "SMTP password:" };
        var passwordInput = new TextBox
        {
            Location = new Point(190, 218),
            Width = 334,
            Text = settings.SmtpPassword ?? string.Empty,
            UseSystemPasswordChar = true
        };

        var fromLabel = new Label { Location = new Point(12, 254), AutoSize = true, Text = "From address (optional):" };
        var fromInput = new TextBox { Location = new Point(190, 252), Width = 334, Text = settings.SmtpFromAddress ?? string.Empty };

        var save = new Button
        {
            Text = "Save",
            Location = new Point(445, 298),
            Width = 75,
            Height = 28
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(362, 298),
            Width = 75,
            Height = 28
        };
        var instructions = new Label
        {
            Location = new Point(12, 298),
            AutoSize = false,
            Width = 340,
            Text = "Tip: Gmail requires an app password (not your login password)."
        };

        if (settings.NotifyOnUsageLimitReached)
            instructions.Text += " Tip 2: Leave SMTP host blank to use mail draft fallback.";
        dialog.Controls.AddRange(new Control[]
        {
            header, enabled, toLabel, toInput, hostLabel, hostInput, portLabel, portInput, ssl,
            userLabel, userInput, passwordLabel, passwordInput, fromLabel, fromInput,
            save, cancel, instructions
        });

        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;

        save.Click += (_, _) =>
        {
            if (enabled.Checked && string.IsNullOrWhiteSpace(toInput.Text))
            {
                MessageBox.Show(dialog, "Notification email is required when notifications are enabled.", "Validation", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            MailAddress? toAddress = null;
            if (!string.IsNullOrWhiteSpace(toInput.Text) &&
                !MailAddress.TryCreate(toInput.Text.Trim(), out toAddress))
            {
                MessageBox.Show(dialog, "Enter a valid notification email address.", "Validation", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            MailAddress? fromAddress = null;
            if (!string.IsNullOrWhiteSpace(fromInput.Text) &&
                !MailAddress.TryCreate(fromInput.Text.Trim(), out fromAddress))
            {
                MessageBox.Show(dialog, "Enter a valid From address.", "Validation", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (!string.IsNullOrWhiteSpace(hostInput.Text))
            {
                if (!int.TryParse(portInput.Text, out var parsedPort) || parsedPort is < 1 or > 65535)
                {
                    MessageBox.Show(dialog, "SMTP port must be a number between 1 and 65535.", "Validation", MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                settings.SmtpPort = parsedPort;
            }

            var hasUser = !string.IsNullOrWhiteSpace(userInput.Text);
            var hasPassword = !string.IsNullOrWhiteSpace(passwordInput.Text);
            if (hasUser != hasPassword)
            {
                MessageBox.Show(dialog, "SMTP user and password must either both be filled in or both be blank.", "Validation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (hasUser && !ssl.Checked)
            {
                MessageBox.Show(dialog, "SSL/TLS must be enabled when SMTP credentials are provided.", "Validation",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            settings.NotifyOnUsageLimitReached = enabled.Checked;
            settings.UsageLimitNotificationEmail = toAddress?.Address;
            settings.SmtpHost = NormalizeOrNull(hostInput.Text);
            settings.SmtpUseSsl = ssl.Checked;
            settings.SmtpUser = NormalizeOrNull(userInput.Text);
            settings.SmtpPassword = NormalizeOrNull(passwordInput.Text);
            settings.SmtpFromAddress = fromAddress?.Address;
            settings.Save();
            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        };

        dialog.ShowDialog(this);
    }

    private static string? NormalizeOrNull(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim();
    }

    private static string BuildNotificationBody(UsageSnapshot snapshot, bool isTest = false)
    {
        var resetText = snapshot.ResetsAt.ToLocalTime().ToString("ddd, MMM d h:mm tt");
        return new StringBuilder()
            .Append(isTest ? "TEST: " : string.Empty)
            .Append("Codex has reached 0% usage (100% available) at ")
            .Append(DateTimeOffset.Now.ToString("ddd, MMM d h:mm tt"))
            .Append(". Current window resets at ")
            .Append(resetText)
            .Append('.')
            .ToString();
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

    private async Task NotifyIfUsageLimitReachedAsync(UsageSnapshot current)
    {
        var previousUsedPercent = settings.LastObservedWeeklyUsedPercent;
        var lastNotificationReset = settings.LastUsageLimitNotificationResetAt;
        var alreadyNotifiedForWindow = lastNotificationReset.HasValue &&
            (current.ResetsAt - lastNotificationReset.Value).Duration() <= ResetTimeMatchTolerance;
        var becameFullyAvailable = previousUsedPercent.HasValue &&
            previousUsedPercent.Value > FullAvailabilityUsedPercent &&
            current.UsedPercent <= FullAvailabilityUsedPercent;
        var observationChanged =
            settings.LastObservedWeeklyResetAt != current.ResetsAt ||
            settings.LastObservedWeeklyUsedPercent != current.UsedPercent;

        // Persist changed observations first. If the app restarts, or notification
        // delivery fails, this transition cannot be handled a second time.
        settings.LastObservedWeeklyResetAt = current.ResetsAt;
        settings.LastObservedWeeklyUsedPercent = current.UsedPercent;

        if (!settings.NotifyOnUsageLimitReached || !becameFullyAvailable || alreadyNotifiedForWindow)
        {
            if (observationChanged)
                settings.Save();
            return;
        }

        // The reset can happen at any time, so do not infer it from the expected
        // weekly schedule. The usage transition above is the event; the reset time
        // is retained only as a narrow guard against duplicate readings.
        settings.LastUsageLimitNotificationResetAt = current.ResetsAt;
        settings.Save();

        var sent = await TrySendUsageLimitEmailAsync(current);
        trayIcon.ShowBalloonTip(
            sent ? 2500 : 4500,
            "CodexBar",
            sent
                ? "Weekly capacity reset to 100% available. The email notification was triggered."
                : "Weekly capacity reset to 100% available, but the email notification could not be sent.",
            sent ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    private async Task<bool> SendTestUsageAlertAsync()
    {
        var test = new UsageSnapshot(
            0,
            DateTimeOffset.UtcNow.AddHours(24),
            DateTimeOffset.UtcNow);
        return await SendUsageLimitEmailAsync(test, isTest: true, forceSend: true);
    }

    private async Task<bool> TrySendUsageLimitEmailAsync(UsageSnapshot current)
    {
        return await SendUsageLimitEmailAsync(current);
    }

    private async Task<bool> SendUsageLimitEmailAsync(UsageSnapshot current, bool isTest = false, bool forceSend = false)
    {
        var toAddress = settings.UsageLimitNotificationEmail;
        var smtpHost = settings.SmtpHost;

        if (!forceSend && !settings.NotifyOnUsageLimitReached)
            return false;
        if (string.IsNullOrWhiteSpace(toAddress))
            return false;

        if (string.IsNullOrWhiteSpace(smtpHost))
            return TryLaunchMailClient(current, toAddress!, isTest);

        return await SendViaSmtpAsync(current, toAddress!, smtpHost, isTest);
    }

    private static bool TryLaunchMailClient(UsageSnapshot current, string toAddress, bool isTest)
    {
        try
        {
            if (!MailAddress.TryCreate(toAddress, out var recipient))
                return false;

            var subject = Uri.EscapeDataString(isTest
                ? "TEST: Codex weekly capacity reset alert"
                : "Codex weekly capacity is 100% available");
            var body = Uri.EscapeDataString(BuildNotificationBody(current, isTest));
            var uri = $"mailto:{Uri.EscapeDataString(recipient.Address)}?subject={subject}&body={body}";

            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    private async Task<bool> SendViaSmtpAsync(UsageSnapshot current, string toAddress, string smtpHost, bool isTest)
    {
        try
        {
            var fromAddress = string.IsNullOrWhiteSpace(settings.SmtpFromAddress) ? toAddress : settings.SmtpFromAddress!;
            var subject = isTest
                ? "TEST: Codex weekly capacity reset alert"
                : "Codex weekly capacity is 100% available";
            var body = BuildNotificationBody(current, isTest);

            using var message = new MailMessage(fromAddress, toAddress)
            {
                Subject = subject,
                Body = body
            };

            var hasUser = !string.IsNullOrWhiteSpace(settings.SmtpUser);
            var hasPassword = !string.IsNullOrWhiteSpace(settings.SmtpPassword);
            if (hasUser != hasPassword || (hasUser && !settings.SmtpUseSsl))
                return false;

            var credentialsProvided = hasUser && hasPassword;
            using var client = new SmtpClient(smtpHost, Math.Clamp(settings.SmtpPort, 1, 65535))
            {
                EnableSsl = settings.SmtpUseSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false
            };

            if (credentialsProvided)
                client.Credentials = new NetworkCredential(settings.SmtpUser, settings.SmtpPassword);

            await client.SendMailAsync(message);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
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
            statusOkBrush.Dispose();
            statusOfflineBrush.Dispose();
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
