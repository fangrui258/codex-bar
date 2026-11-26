using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Mail;
using System.Runtime.InteropServices;

namespace CodexBar;

internal sealed class WidgetForm : Form
{
    private const int WmNchittest = 0x0084;
    private const int HtCaption = 2;
    private const int WidgetWidth = 286;
    private const int BaseWidgetHeight = 160;
    private const int ExtraRowStride = 65;
    private const int MaxVisibleLanes = 6;
    private const int HeaderHeight = 30;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);
    private const double FullAvailabilityUsedPercent = 0.001;
    private static readonly TimeSpan NotificationDeliveryTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ResetTimeMatchTolerance = TimeSpan.FromHours(1);

    private readonly AppSettings settings = AppSettings.Load();
    private readonly IUsageProvider[] providers;
    private readonly Dictionary<string, ProviderSnapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> readErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Rectangle> tabRects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToolStripMenuItem> providerMenuItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Icon appIcon;
    private readonly NotifyIcon trayIcon;
    private readonly System.Windows.Forms.Timer refreshTimer;
    private readonly System.Windows.Forms.Timer positionSaveTimer;
    private readonly SemaphoreSlim refreshGate = new(1, 1);

    private readonly Pen borderPen = new(Color.FromArgb(42, 58, 49));
    private readonly Pen controlPen = new(Color.FromArgb(130, 150, 138), 1.2f);
    private readonly Font titleFont = new("Segoe UI Semibold", 8.5f);
    private readonly Font percentFont = new("Segoe UI Semibold", 22f);
    private readonly Font resetFont = new("Segoe UI", 9f);
    private readonly Font compactResetFont = new("Segoe UI", 7.5f);
    private readonly Font denseResetFont = new("Segoe UI", 6.5f);
    private readonly SolidBrush dimBrush = new(Color.FromArgb(143, 160, 150));
    private readonly SolidBrush statusOkBrush = new(Color.FromArgb(84, 235, 120));
    private readonly SolidBrush statusStandbyBrush = new(Color.FromArgb(148, 160, 153));
    private readonly SolidBrush statusOfflineBrush = new(Color.FromArgb(239, 128, 128));
    private readonly SolidBrush textBrush = new(Color.FromArgb(225, 235, 229));
    private readonly SolidBrush trackBrush = new(Color.FromArgb(31, 39, 34));
    private readonly SolidBrush usageBrush = new(Color.FromArgb(46, 220, 112));
    private readonly SolidBrush activeTabAccentBrush = new(Color.FromArgb(46, 220, 112));

    private string activeProviderId;
    private bool exiting;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    public WidgetForm()
        : this(new IUsageProvider[] { new CodexUsageProvider(), new AntigravityUsageProvider() })
    {
    }

    internal WidgetForm(IUsageProvider[] customProviders)
    {
        providers = customProviders;
        activeProviderId = providers.Any(p => string.Equals(p.Id, settings.ActiveProvider, StringComparison.OrdinalIgnoreCase))
            ? providers.First(p => string.Equals(p.Id, settings.ActiveProvider, StringComparison.OrdinalIgnoreCase)).Id
            : providers[0].Id;

        Text = "CodexBar";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(WidgetWidth, BaseWidgetHeight);
        MinimumSize = MaximumSize = Size;
        BackColor = Color.FromArgb(8, 10, 9);
        DoubleBuffered = true;
        TopMost = settings.AlwaysOnTop;
        Opacity = Math.Clamp(settings.Opacity, 0.5, 1.0);
        Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? CreateTrayIcon();
        Icon = appIcon;

        using (var g = CreateGraphics())
            ComputeTabRects(g);

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = settings.X.HasValue && settings.Y.HasValue
            ? KeepVisible(new Point(settings.X.Value, settings.Y.Value))
            : new Point(area.Right - Width - 24, area.Bottom - Height - 24);

        trayIcon = new NotifyIcon
        {
            Icon = (Icon)appIcon.Clone(),
            Text = "CodexBar — loading usage limits",
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

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyAlwaysOnTopState();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) ApplyAlwaysOnTopState();
    }

    internal void SetSnapshotForTest(ProviderSnapshot snapshot)
    {
        snapshots[snapshot.ProviderId] = snapshot;
        readErrors[snapshot.ProviderId] = null;
        UpdateWindowHeightForActiveProvider();
        Invalidate();
    }

    internal void SwitchProvider(string providerId, bool persist = true)
    {
        var target = providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (target is null) return;

        activeProviderId = target.Id;
        foreach (var (id, item) in providerMenuItems)
            item.Checked = string.Equals(id, activeProviderId, StringComparison.OrdinalIgnoreCase);

        if (persist && !string.Equals(settings.ActiveProvider, activeProviderId, StringComparison.OrdinalIgnoreCase))
        {
            settings.ActiveProvider = activeProviderId;
            settings.Save();
        }

        UpdateWindowHeightForActiveProvider();
        Invalidate();
    }

    private IUsageProvider ActiveProvider =>
        providers.First(p => string.Equals(p.Id, activeProviderId, StringComparison.OrdinalIgnoreCase));

    private void ComputeTabRects(Graphics g)
    {
        tabRects.Clear();
        float x = 22f;
        const int tabHeight = 22;
        const int tabTop = 4;

        for (var i = 0; i < providers.Length; i++)
        {
            var provider = providers[i];
            var glyphWidth = g.MeasureString(provider.TabLabel, titleFont, PointF.Empty, StringFormat.GenericTypographic).Width;
            var width = (int)Math.Ceiling(glyphWidth) + 8;
            tabRects[provider.Id] = new Rectangle((int)Math.Floor(x), tabTop, width, tabHeight);
            x += width + 10f;
        }
    }

    private void UpdateWindowHeightForActiveProvider()
    {
        snapshots.TryGetValue(activeProviderId, out var activeSnapshot);
        var visibleLaneCount = activeSnapshot is { Lanes.Count: > 0 }
            ? Math.Min(activeSnapshot.Lanes.Count, MaxVisibleLanes)
            : 2;

        var desiredHeight = visibleLaneCount <= 2
            ? BaseWidgetHeight
            : BaseWidgetHeight + (visibleLaneCount - 2) * ExtraRowStride;

        if (ClientSize.Height == desiredHeight) return;

        MinimumSize = Size.Empty;
        MaximumSize = Size.Empty;
        ClientSize = new Size(WidgetWidth, desiredHeight);
        MinimumSize = MaximumSize = Size;
        Location = KeepVisible(Location);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        ComputeTabRects(g);

        snapshots.TryGetValue(activeProviderId, out var activeSnapshot);
        readErrors.TryGetValue(activeProviderId, out var activeError);
        var activeProvider = ActiveProvider;
        var activeStatus = activeSnapshot?.Status ?? ProviderStatus.Offline;

        const float titleY = 12f;
        var labelHeight = g.MeasureString(activeProvider.TabLabel, titleFont).Height;
        var dotY = titleY + (labelHeight - 6f) / 2f;
        var statusBrush = activeStatus switch
        {
            ProviderStatus.Online => statusOkBrush,
            ProviderStatus.Standby => statusStandbyBrush,
            _ => statusOfflineBrush
        };
        g.FillEllipse(statusBrush, 10, dotY, 6, 6);
        g.DrawRectangle(borderPen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

        DrawTabs(g, titleY, activeStatus);
        DrawWindowControls(g);

        if (activeSnapshot is null || activeSnapshot.Lanes.Count == 0)
        {
            DrawEmptyOrStatusState(g, activeProvider, activeSnapshot, activeError);
            return;
        }

        var visibleCount = Math.Min(activeSnapshot.Lanes.Count, MaxVisibleLanes);
        for (var i = 0; i < visibleCount; i++)
        {
            var lane = activeSnapshot.Lanes[i];
            DrawLaneRow(g, lane, i, visibleCount);
        }
    }

    private void DrawTabs(Graphics g, float titleY, ProviderStatus activeStatus)
    {
        for (var i = 0; i < providers.Length; i++)
        {
            var provider = providers[i];
            var rect = tabRects[provider.Id];
            var isActive = string.Equals(provider.Id, activeProviderId, StringComparison.OrdinalIgnoreCase);
            var glyphWidth = g.MeasureString(provider.TabLabel, titleFont, PointF.Empty, StringFormat.GenericTypographic).Width;

            g.DrawString(provider.TabLabel, titleFont, isActive ? textBrush : dimBrush, new PointF(rect.X + 4, titleY), StringFormat.GenericTypographic);

            if (isActive)
            {
                activeTabAccentBrush.Color = activeStatus switch
                {
                    ProviderStatus.Online => statusOkBrush.Color,
                    ProviderStatus.Standby => statusStandbyBrush.Color,
                    _ => statusOfflineBrush.Color
                };
                g.FillRectangle(activeTabAccentBrush, rect.X + 4, HeaderHeight - 3, Math.Max(16f, glyphWidth), 2f);
            }

            if (i < providers.Length - 1)
            {
                var sepX = rect.Right + 3f;
                g.DrawString("·", titleFont, dimBrush, new PointF(sepX, titleY), StringFormat.GenericTypographic);
            }
        }
    }

    private void DrawEmptyOrStatusState(
        Graphics g,
        IUsageProvider activeProvider,
        ProviderSnapshot? activeSnapshot,
        string? activeError)
    {
        var isCodex = string.Equals(activeProvider.Id, CodexUsageProvider.ProviderId, StringComparison.OrdinalIgnoreCase);
        var primaryLabel = isCodex ? "5-HOUR LEFT" : "STATUS";
        var secondaryLabel = isCodex ? "WEEKLY LEFT" : "SETUP HINT";

        g.DrawString(primaryLabel, titleFont, dimBrush, 14, 31);
        g.DrawString(secondaryLabel, titleFont, dimBrush, 14, 88);
        g.DrawLine(borderPen, 16, 84, Width - 16, 84);

        var primaryMessage = activeSnapshot?.StatusMessage
            ?? activeError
            ?? $"Waiting for {activeProvider.DisplayName} usage…";
        g.DrawString("—%", percentFont, textBrush, 14, 43);
        g.DrawString(primaryMessage, resetFont, dimBrush, new RectangleF(104, 48, Width - 116, 34));

        if (isCodex)
        {
            g.DrawString("—%", percentFont, textBrush, 14, 100);
        }
        else
        {
            var hint = activeSnapshot?.Status == ProviderStatus.Standby
                ? "Start Antigravity or agy CLI to read live limits."
                : "Right-click tray icon → Setup Antigravity CLI bridge…";
            g.DrawString(hint, compactResetFont, dimBrush, new RectangleF(14, 106, Width - 28, 34));
        }

        DrawProgress(g, 0, Color.FromArgb(45, 62, 52), 78);
        DrawProgress(g, 0, Color.FromArgb(45, 62, 52), BaseWidgetHeight - 17);
    }

    private void DrawLaneRow(Graphics g, QuotaLane lane, int rowIndex, int totalRows)
    {
        var labelY = rowIndex == 0 ? 31 : 86 + (rowIndex - 1) * ExtraRowStride + 2;
        var percentY = rowIndex == 0 ? 43 : 86 + (rowIndex - 1) * ExtraRowStride + 14;
        var progressY = rowIndex == 0 ? 78 : 86 + (rowIndex - 1) * ExtraRowStride + 57;
        var dividerY = rowIndex == 0 ? 84 : 86 + (rowIndex - 1) * ExtraRowStride + 63;

        g.DrawString(lane.Label, titleFont, dimBrush, 14, labelY);

        var expirations = lane.ExpirationTimes ?? Array.Empty<DateTimeOffset>();
        var infoY = rowIndex == 0
            ? 52
            : 86 + (rowIndex - 1) * ExtraRowStride + (expirations.Count > 0 ? 18 : 23);

        if (lane.RemainingPercent is { } remaining)
        {
            var color = RemainingColor(remaining);
            usageBrush.Color = color;
            g.DrawString($"{remaining:0}%", percentFont, usageBrush, 14, percentY);
            DrawResetSchedule(g, lane.ResetsAt, expirations, 104, infoY);
            DrawProgress(g, remaining, color, progressY);
        }
        else
        {
            g.DrawString("—%", percentFont, textBrush, 14, percentY);
            g.DrawString(lane.UnavailableText ?? "Unavailable", resetFont, dimBrush, new RectangleF(104, infoY, Width - 116, 32));
            DrawProgress(g, 0, Color.FromArgb(45, 62, 52), progressY);
        }

        if (rowIndex < totalRows - 1)
            g.DrawLine(borderPen, 16, dividerY, Width - 16, dividerY);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WmNchittest && (int)m.Result == 1)
        {
            var screenPoint = new Point((short)(m.LParam.ToInt64() & 0xffff),
                (short)((m.LParam.ToInt64() >> 16) & 0xffff));
            var clientPoint = PointToClient(screenPoint);
            var overControls = clientPoint.Y <= HeaderHeight && clientPoint.X >= Width - 60;
            var overTabs = tabRects.Values.Any(rect => rect.Contains(clientPoint));
            if (!overControls && !overTabs) m.Result = HtCaption;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && e.Y <= HeaderHeight)
        {
            if (e.X >= Width - 60)
            {
                if (e.X >= Width - 30) HideToTray();
                else Close();
                return;
            }

            foreach (var (providerId, rect) in tabRects)
            {
                if (rect.Contains(e.Location))
                {
                    SwitchProvider(providerId);
                    return;
                }
            }
        }
        base.OnMouseDown(e);
    }

    private async Task RefreshUsageAsync()
    {
        if (!await refreshGate.WaitAsync(0)) return;
        refreshTimer.Stop();
        try
        {
            var tasks = providers.Select(RefreshProviderAsync).ToArray();
            await Task.WhenAll(tasks);

            UpdateTrayTooltip();
            UpdateWindowHeightForActiveProvider();
            Invalidate();
        }
        finally
        {
            refreshTimer.Start();
            refreshGate.Release();
        }
    }

    private async Task RefreshProviderAsync(IUsageProvider provider)
    {
        try
        {
            var accountChanged = provider.CheckAccountChanged();
            if (accountChanged)
            {
                snapshots.Remove(provider.Id);
                readErrors[provider.Id] = "Account switching…";
                if (string.Equals(provider.Id, activeProviderId, StringComparison.OrdinalIgnoreCase))
                {
                    Invalidate();
                    Update();
                }
            }

            var current = await provider.ReadUsageAsync();
            snapshots[provider.Id] = current;
            readErrors[provider.Id] = null;

            if (current.Status == ProviderStatus.Online)
                await NotifyIfUsageLimitReachedAsync(current, accountChanged);
        }
        catch (Exception ex)
        {
            if (snapshots.TryGetValue(provider.Id, out var previous))
                snapshots[provider.Id] = previous with { Status = ProviderStatus.Offline };
            readErrors[provider.Id] = snapshots.ContainsKey(provider.Id) ? null : "Usage unavailable";
            Debug.WriteLine(ex);
        }
    }

    private void UpdateTrayTooltip()
    {
        var parts = new List<string>(providers.Length);
        foreach (var provider in providers)
        {
            if (!snapshots.TryGetValue(provider.Id, out var snap))
            {
                parts.Add($"{ShortCode(provider.Id)} offline");
                continue;
            }

            if (snap.Status == ProviderStatus.Standby)
            {
                parts.Add($"{ShortCode(provider.Id)} standby");
                continue;
            }

            if (snap.Status == ProviderStatus.Offline || snap.Lanes.Count == 0)
            {
                parts.Add($"{ShortCode(provider.Id)} offline");
                continue;
            }

            var summary = string.Join(" ", snap.Lanes
                .Where(l => l.RemainingPercent.HasValue)
                .Take(2)
                .Select(l => $"{ShortLaneLabel(l.Key)} {l.RemainingPercent!.Value:0}%"));
            parts.Add(string.IsNullOrEmpty(summary)
                ? $"{ShortCode(provider.Id)} ready"
                : $"{ShortCode(provider.Id)} {summary}");
        }

        var tooltip = string.Join(" · ", parts);
        if (string.IsNullOrWhiteSpace(tooltip)) tooltip = "CodexBar";
        trayIcon.Text = tooltip[..Math.Min(63, tooltip.Length)];
    }

    private static string ShortCode(string providerId) => providerId.ToLowerInvariant() switch
    {
        CodexUsageProvider.ProviderId => "CDX",
        AntigravityUsageProvider.ProviderId => "AGY",
        _ => providerId.ToUpperInvariant()
    };

    private static string ShortLaneLabel(string key) => key.ToLowerInvariant() switch
    {
        "five-hour" => "5h",
        "weekly" => "wk",
        "gemini-weekly" => "wk",
        "gemini-pro" => "pro",
        "gemini-flash" => "fl",
        "claude" => "cld",
        _ => key.Length <= 4 ? key : key[..4]
    };

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("Show widget", null, (_, _) => ShowWidget());
        menu.Items.Add("Refresh now", null, async (_, _) => await RefreshUsageAsync());

        var providerMenu = new ToolStripMenuItem("Active tab");
        providerMenuItems.Clear();
        foreach (var provider in providers)
        {
            var item = new ToolStripMenuItem(provider.DisplayName)
            {
                Checked = string.Equals(provider.Id, activeProviderId, StringComparison.OrdinalIgnoreCase)
            };
            var capturedId = provider.Id;
            item.Click += (_, _) => SwitchProvider(capturedId);
            providerMenuItems[provider.Id] = item;
            providerMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(providerMenu);
        menu.Items.Add("Setup Antigravity CLI bridge…", null, async (_, _) => await SetupAntigravityBridgeAsync());

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

        var topmost = new ToolStripMenuItem($"Always on top — {(settings.AlwaysOnTop ? "On" : "Off")}")
        {
            CheckOnClick = true,
            Checked = settings.AlwaysOnTop
        };
        topmost.CheckedChanged += (_, _) =>
        {
            settings.AlwaysOnTop = topmost.Checked;
            ApplyAlwaysOnTopState();
            topmost.Text = $"Always on top — {(topmost.Checked ? "On" : "Off")}";
            settings.Save();
        };
        menu.Items.Add(topmost);

        var startsWithWindows = AppSettings.StartsWithWindows;
        var startup = new ToolStripMenuItem($"Start with Windows — {(startsWithWindows ? "On" : "Off")}")
        {
            CheckOnClick = true,
            Checked = startsWithWindows
        };
        startup.Click += (_, _) =>
        {
            try { AppSettings.StartsWithWindows = startup.Checked; }
            catch { startup.Checked = !startup.Checked; }
            startup.Text = $"Start with Windows — {(startup.Checked ? "On" : "Off")}";
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

    private async Task SetupAntigravityBridgeAsync()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            MessageBox.Show(this, "Could not resolve CodexBar executable path.", "Antigravity Bridge",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var settingsPath = AntigravityStatusBridge.GetCliSettingsPath();
        var command = AntigravityStatusBridge.BuildBridgeCommand(executablePath);
        var confirm = MessageBox.Show(
            this,
            "CodexBar can register itself as an Antigravity CLI (agy) statusLine bridge without replacing the default statusline.\n\n" +
            $"Target config: {settingsPath}\n" +
            $"Command: {command}\n\n" +
            "Existing custom statusLine scripts will not be overwritten. Continue?",
            "Setup Antigravity CLI Bridge",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes) return;

        try
        {
            var result = AntigravityStatusBridge.ConfigureCliSettings(executablePath, out var existingCommand);
            switch (result)
            {
                case BridgeSetupResult.Configured:
                    MessageBox.Show(
                        this,
                        "Antigravity CLI statusLine bridge configured.\n" +
                        "Run or interact with `agy` to populate live Antigravity quota data.",
                        "Antigravity Bridge",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    await RefreshUsageAsync();
                    break;

                case BridgeSetupResult.AlreadyConfigured:
                    MessageBox.Show(
                        this,
                        "Antigravity CLI statusLine bridge is already configured for this executable.",
                        "Antigravity Bridge",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    break;

                case BridgeSetupResult.CustomCommandConflict:
                    var copyResult = MessageBox.Show(
                        this,
                        "An existing custom statusLine command is already configured in settings.json:\n" +
                        $"{existingCommand}\n\n" +
                        "CodexBar left your config untouched. Copy the bridge command to your clipboard so you can pipe stdin to it from your script?",
                        "Custom statusLine Detected",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);
                    if (copyResult == DialogResult.Yes)
                        Clipboard.SetText(command);
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not configure Antigravity CLI settings:\n{ex.Message}",
                "Antigravity Bridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
            Text = "When tracked quota capacity resets to 100% available, CodexBar can send one notification email to your configured address.\n" +
                   "If SMTP fields are blank, CodexBar opens your default mail app with a prefilled draft instead.",
            TextAlign = ContentAlignment.TopLeft
        };
        header.MaximumSize = new Size(510, 0);

        var enabled = new CheckBox
        {
            Text = "Notify when tracked capacity resets to 100% available",
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

    private static string BuildNotificationBody(string providerName, QuotaLane lane, bool isTest = false)
    {
        var resetText = lane.ResetsAt?.ToLocalTime().ToString("ddd, MMM d h:mm tt") ?? "unknown";
        var prefix = isTest ? "TEST: " : string.Empty;
        return $"{prefix}{providerName} ({lane.Label}) has reached 0% usage (100% available) at {DateTimeOffset.Now:ddd, MMM d h:mm tt}. " +
            $"Current window resets at {resetText}.";
    }

    private void DrawWindowControls(Graphics g)
    {
        g.DrawLine(controlPen, Width - 48, 17, Width - 40, 17);
        g.DrawLine(controlPen, Width - 20, 13, Width - 12, 21);
        g.DrawLine(controlPen, Width - 12, 13, Width - 20, 21);
    }

    private void DrawResetSchedule(
        Graphics g,
        DateTimeOffset? resetsAt,
        IReadOnlyList<DateTimeOffset> resetExpirations,
        float x,
        float y)
    {
        if (!resetsAt.HasValue)
        {
            g.DrawString("No active reset timer", resetFont, dimBrush, new RectangleF(x, y, Width - x - 12, 32));
            return;
        }

        var reset = resetsAt.Value.ToLocalTime();
        if (resetExpirations.Count == 0)
        {
            var resetText = reset.Date == DateTimeOffset.Now.Date
                ? $"Resets today · {reset:h:mm tt}"
                : $"Resets {reset:ddd, MMM d} · {reset:h:mm tt}";
            g.DrawString(resetText, resetFont, textBrush, new RectangleF(x, y, Width - x - 12, 34));
            return;
        }

        var lines = new List<string>
        {
            $"Resets {reset:ddd, MMM d} - {reset:h:mm tt}"
        };
        lines.AddRange(resetExpirations.Select(expiration =>
        {
            var localExpiration = expiration.ToLocalTime();
            return $"Reset expires {localExpiration:ddd, MMM d} - {localExpiration:h:mm tt}";
        }));

        var font = lines.Count <= 3 ? compactResetFont : denseResetFont;
        var lineHeight = Math.Min(12f, 35f / lines.Count);
        foreach (var line in lines)
        {
            g.DrawString(line, font, textBrush, x, y);
            y += lineHeight;
        }
    }

    private void DrawProgress(Graphics g, double percent, Color color, int y)
    {
        var track = new Rectangle(16, y, Width - 32, 4);
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

    private async Task NotifyIfUsageLimitReachedAsync(ProviderSnapshot current, bool accountChanged)
    {
        if (current.AlertLaneKeys is not { Count: > 0 }) return;

        var settingsDirty = false;
        var notificationsToSend = new List<QuotaLane>();

        foreach (var laneKey in current.AlertLaneKeys)
        {
            var lane = current.Lanes.FirstOrDefault(l => string.Equals(l.Key, laneKey, StringComparison.OrdinalIgnoreCase));
            if (lane?.UsedPercent is not { } usedPercent || lane.ResetsAt is not { } resetsAt)
                continue;

            var state = settings.GetOrCreateNotificationState(current.ProviderId, lane.Key);

            if (accountChanged)
            {
                state.LastObservedUsedPercent = usedPercent;
                state.LastNotifiedResetAt = null;
                SyncLegacyCodexState(current.ProviderId, lane.Key, state);
                settingsDirty = true;
                continue;
            }

            var previousUsedPercent = state.LastObservedUsedPercent;
            var lastNotificationReset = state.LastNotifiedResetAt;
            var alreadyNotifiedForWindow = lastNotificationReset.HasValue &&
                (resetsAt - lastNotificationReset.Value).Duration() <= ResetTimeMatchTolerance;
            var becameFullyAvailable = previousUsedPercent.HasValue &&
                previousUsedPercent.Value > FullAvailabilityUsedPercent &&
                usedPercent <= FullAvailabilityUsedPercent;

            if (state.LastObservedUsedPercent != usedPercent)
            {
                state.LastObservedUsedPercent = usedPercent;
                SyncLegacyCodexState(current.ProviderId, lane.Key, state);
                settingsDirty = true;
            }

            if (settings.NotifyOnUsageLimitReached && becameFullyAvailable && !alreadyNotifiedForWindow)
            {
                state.LastNotifiedResetAt = resetsAt;
                SyncLegacyCodexState(current.ProviderId, lane.Key, state);
                settingsDirty = true;
                notificationsToSend.Add(lane);
            }
        }

        if (settingsDirty)
            settings.Save();

        foreach (var lane in notificationsToSend)
        {
            var sent = await SendUsageLimitEmailAsync(current.DisplayName, lane);
            trayIcon.ShowBalloonTip(
                sent ? 2500 : 4500,
                "CodexBar",
                sent
                    ? $"{current.DisplayName} capacity reset to 100% available. The email notification was triggered."
                    : $"{current.DisplayName} capacity reset to 100% available, but the email notification could not be sent.",
                sent ? ToolTipIcon.Info : ToolTipIcon.Warning);
        }
    }

    private void SyncLegacyCodexState(string providerId, string laneKey, ProviderNotificationState state)
    {
        if (string.Equals(providerId, CodexUsageProvider.ProviderId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(laneKey, "weekly", StringComparison.OrdinalIgnoreCase))
        {
            settings.LastObservedWeeklyUsedPercent = state.LastObservedUsedPercent;
            settings.LastUsageLimitNotificationResetAt = state.LastNotifiedResetAt;
        }
    }

    private Task<bool> SendTestUsageAlertAsync()
    {
        var active = ActiveProvider;
        var testLane = new QuotaLane("weekly", "WEEKLY LEFT", 0, DateTimeOffset.UtcNow.AddDays(7));
        return SendUsageLimitEmailAsync(active.DisplayName, testLane, isTest: true, forceSend: true);
    }

    private async Task<bool> SendUsageLimitEmailAsync(string providerName, QuotaLane lane, bool isTest = false, bool forceSend = false)
    {
        var toAddress = settings.UsageLimitNotificationEmail;
        var smtpHost = settings.SmtpHost;

        if (!forceSend && !settings.NotifyOnUsageLimitReached)
            return false;
        if (string.IsNullOrWhiteSpace(toAddress))
            return false;

        if (string.IsNullOrWhiteSpace(smtpHost))
            return TryLaunchMailClient(providerName, lane, toAddress!, isTest);

        return await SendViaSmtpAsync(providerName, lane, toAddress!, smtpHost, isTest);
    }

    private static bool TryLaunchMailClient(string providerName, QuotaLane lane, string toAddress, bool isTest)
    {
        try
        {
            if (!MailAddress.TryCreate(toAddress, out var recipient))
                return false;

            var subject = Uri.EscapeDataString(isTest
                ? $"TEST: {providerName} capacity reset alert"
                : $"{providerName} capacity is 100% available");
            var body = Uri.EscapeDataString(BuildNotificationBody(providerName, lane, isTest));
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

    private async Task<bool> SendViaSmtpAsync(string providerName, QuotaLane lane, string toAddress, string smtpHost, bool isTest)
    {
        try
        {
            var fromAddress = string.IsNullOrWhiteSpace(settings.SmtpFromAddress) ? toAddress : settings.SmtpFromAddress!;
            var subject = isTest
                ? $"TEST: {providerName} capacity reset alert"
                : $"{providerName} capacity is 100% available";
            var body = BuildNotificationBody(providerName, lane, isTest);

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

            using var timeout = new CancellationTokenSource(NotificationDeliveryTimeout);
            await client.SendMailAsync(message, timeout.Token);
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
        ApplyAlwaysOnTopState();
        Activate();
    }

    private void ApplyAlwaysOnTopState()
    {
        TopMost = settings.AlwaysOnTop;
        if (!IsHandleCreated) return;

        _ = SetWindowPos(
            Handle,
            settings.AlwaysOnTop ? HwndTopMost : HwndNoTopMost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
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
            foreach (var provider in providers)
                provider.Dispose();
            refreshTimer.Dispose();
            positionSaveTimer.Dispose();
            trayIcon.Dispose();
            appIcon.Dispose();
            borderPen.Dispose();
            controlPen.Dispose();
            titleFont.Dispose();
            percentFont.Dispose();
            resetFont.Dispose();
            compactResetFont.Dispose();
            denseResetFont.Dispose();
            dimBrush.Dispose();
            statusOkBrush.Dispose();
            statusStandbyBrush.Dispose();
            statusOfflineBrush.Dispose();
            textBrush.Dispose();
            trackBrush.Dispose();
            usageBrush.Dispose();
            activeTabAccentBrush.Dispose();
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
