using System.Drawing;
using System.Drawing.Drawing2D;
using Windows.Storage.Streams;
using Microsoft.Win32;
using Windows.Devices.Bluetooth.Advertisement;

namespace AirTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => CrashLog.Write("UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write("Unhandled exception", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        try { Application.Run(new AirTrayContext()); }
        catch (Exception e) { CrashLog.Write("Main", e); }
    }
}

internal static class CrashLog
{
    public static void Write(string origin, Exception error)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirTray");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "errors.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {origin}\r\n{error}\r\n\r\n");
        }
        catch { /* Logging must never crash AirTray. */ }
    }
}

internal sealed record BatteryReport(int Model, int? Left, int? Right, int? Case,
    bool LeftCharging, bool RightCharging, bool CaseCharging, ulong Address, short Rssi,
    DateTime Received)
{
    public int? Average => Left.HasValue && Right.HasValue ? (Left.Value + Right.Value) / 2 : Left ?? Right ?? Case;
}

internal static class AirPodsParser
{
    private static readonly Dictionary<int, string> Models = new()
    {
        [0x0220] = "AirPods (1)", [0x0F20] = "AirPods (2)", [0x1320] = "AirPods (3)",
        [0x1920] = "AirPods (4)", [0x1B20] = "AirPods (4 ANC)",
        [0x0E20] = "AirPods Pro", [0x1420] = "AirPods Pro (2)",
        [0x2420] = "AirPods Pro (2 USB-C)", [0x2720] = "AirPods Pro (3)",
        [0x0A20] = "AirPods Max", [0x1F20] = "AirPods Max (USB-C)"
    };
    public static string ModelName(int model) => "AirPods"; // Model not verified by unauthenticated BLE packets.

    public static BatteryReport? Parse(byte[] data, ulong address, short rssi)
    {
        if (data.Length != 27 || data[0] != 0x07 || data[1] != 0x19 || data[2] != 0x01)
            return null;
        int model = (data[3] << 8) | data[4];
        // Unknown models can still use the common paired-mode battery format.
        // Do not silently discard newly released AirPods.
        // Apple's Continuity proximity-pairing payload uses 4-bit, approximate battery readings.
        int first = data[6] >> 4, second = data[6] & 15;
        int flags = data[7] >> 4;
        // Orientation depends on the flip flag in status byte 5.
        bool flipped = (data[5] & 0x40) != 0;
        int left = flipped ? second : first, right = flipped ? first : second;
        bool leftCharge = (flags & (flipped ? 2 : 1)) != 0;
        bool rightCharge = (flags & (flipped ? 1 : 2)) != 0;
        return new BatteryReport(model, Percent(left), Percent(right), Percent(data[7] & 15),
            leftCharge, rightCharge, (flags & 4) != 0, address, rssi, DateTime.Now);
    }
    private static int? Percent(int nibble) => nibble switch { 15 => null, 10 => 100, >= 0 and <= 9 => nibble * 10, _ => null };
}

internal sealed class AirTrayContext : ApplicationContext
{
    private readonly NotifyIcon tray = new();
    private readonly BluetoothLEAdvertisementWatcher watcher = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly ToolStripMenuItem statusItem = new("Suche AirPods ...") { Enabled = false };
    private readonly ToolStripMenuItem leftItem = new("Links: –") { Enabled = false };
    private readonly ToolStripMenuItem rightItem = new("Rechts: –") { Enabled = false };
    private readonly ToolStripMenuItem caseItem = new("Case: –") { Enabled = false };
    private BatteryWindow? batteryWindow;
    private readonly ToolStripMenuItem startupItem = new("Mit Windows starten");
    private readonly Dictionary<ulong, BatteryReport> reports = new();
    private readonly object receivedGate = new();
    private readonly Dictionary<ulong, BatteryReport> pendingReports = new();
    private BatteryReport? displayed;
    private DateTime? lastBatteryChange;
    private string? batterySignature;
    // Rotating BLE addresses are transport identifiers, not separate AirPods devices.
    private int? activeModel;
    private bool stopping;
    private bool bluetoothError;
    private Icon? renderedIcon;
    private readonly Control dispatcher = new();
    private readonly BleCapture capture = new();
    private readonly ToolStripMenuItem captureItem = new("Bluetooth-Diagnose starten (5 Min.)");
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan UnchangedWarning = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Retain = TimeSpan.FromMinutes(10);
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "AirTray";

    public AirTrayContext()
    {
        // Create a WinForms handle on the main STA thread before starting BLE callbacks.
        // Dispatcher.BeginInvoke then consistently marshals all UI mutations onto this thread.
        _ = dispatcher.Handle;
        captureItem.Click += (_, _) => ToggleCapture();
        startupItem.Checked = IsStartupEnabled();
        startupItem.Click += (_, _) => ToggleStartup();
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            statusItem, new ToolStripSeparator(), leftItem, rightItem, caseItem,
            new ToolStripSeparator(), startupItem,
            new ToolStripMenuItem("Erneut suchen", null, (_, _) => RestartScan()),
            new ToolStripMenuItem("Windows-Bluetooth öffnen", null, (_, _) => OpenBluetoothSettings()),
            new ToolStripSeparator(), captureItem,
            new ToolStripMenuItem("Diagnoseordner öffnen", null, (_, _) => OpenCaptureFolder()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Beenden", null, (_, _) => ExitThread())
        });
        tray.ContextMenuStrip = menu;
        tray.Text = "AirTray – Suche AirPods";
        tray.Visible = true;
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                try { ShowPopup(); }
                catch (Exception ex)
                {
                    CrashLog.Write("Open battery window", ex);
                    MessageBox.Show("Das Akkufenster konnte nicht geöffnet werden. Details unter %LOCALAPPDATA%\\AirTray\\errors.log", "AirTray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        };
        watcher.ScanningMode = BluetoothLEScanningMode.Active;
        watcher.Received += OnReceived;
        watcher.Stopped += (_, _) => PostToUi(() => { bluetoothError = true; UpdateDisplay(); });
        timer.Tick += (_, _) => { DrainReceived(); UpdateDisplay(); };
        timer.Start();
        UpdateDisplay();
        RestartScan();
    }

    private void PostToUi(Action work)
    {
        if (stopping || dispatcher.IsDisposed) return;
        try { dispatcher.BeginInvoke(new Action(() =>
        {
            if (stopping) return;
            try { work(); } catch (Exception ex) { CrashLog.Write("UI dispatch", ex); }
        })); }
        catch (InvalidOperationException) { /* UI handle was destroyed while shutting down. */ }
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
    {
        foreach (var section in e.Advertisement.ManufacturerData)
        {
            if (section.CompanyId != 0x004C) continue;
            try
            {
                var bytes = new byte[(int)section.Data.Length];
                using var reader = DataReader.FromBuffer(section.Data);
                reader.ReadBytes(bytes);
                var report = AirPodsParser.Parse(bytes, e.BluetoothAddress, e.RawSignalStrengthInDBm);
                var decision = report is null ? "not-airpods-format" : "parsed-airpods-format";
                capture.Packet(e.BluetoothAddress, e.RawSignalStrengthInDBm, bytes, report, decision);
                if (report is null) continue;
                // BLE broadcasts can arrive hundreds of times per second. Coalesce
                // them by transport address and update WinForms on its own timer.
                lock (receivedGate) pendingReports[report.Address] = report;
            }
            catch (Exception ex) { CrashLog.Write("BLE packet processing", ex); }
        }
    }

    private void DrainReceived()
    {
        lock (receivedGate)
        {
            if (pendingReports.Count == 0) return;
            foreach (var entry in pendingReports) reports[entry.Key] = entry.Value;
            pendingReports.Clear();
        }
        bluetoothError = false;
    }

    private void ToggleCapture()
    {
        try
        {
            if (capture.Active) capture.Stop();
            else capture.Start();
            RefreshCaptureItem();
        }
        catch (Exception ex) { CrashLog.Write("BLE capture", ex); MessageBox.Show(ex.Message, "Bluetooth-Diagnose", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    private void RefreshCaptureItem() => captureItem.Text = capture.Active
        ? "Bluetooth-Diagnose beenden" : "Bluetooth-Diagnose starten (5 Min.)";
    private static void OpenCaptureFolder()
    {
        try
        {
            Directory.CreateDirectory(BleCapture.Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{BleCapture.Folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { CrashLog.Write("Open diagnosis folder", ex); }
    }

    private void RestartScan()
    {
        try
        {
            if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) watcher.Stop();
            lock (receivedGate) pendingReports.Clear();
            reports.Clear();
            activeModel = null;
            displayed = null;
            batterySignature = null;
            lastBatteryChange = null;
            bluetoothError = false;
            watcher.Start();
        }
        catch { bluetoothError = true; }
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        RefreshCaptureItem();
        var now = DateTime.Now;
        foreach (var id in reports.Where(x => now - x.Value.Received > Retain).Select(x => x.Key).ToArray()) reports.Remove(id);
        // Choose one logical AirPods set automatically. Different BLE addresses
        // with the same model can represent rotating addresses of the same set.
        // Prefer the strongest nearby model group and keep it until it expires.
        var candidates = reports.Values.Where(x => now - x.Received <= Retain).ToList();
        var live = candidates.Where(x => now - x.Received <= Fresh).ToList();
        if (activeModel.HasValue && !live.Any(x => x.Model == activeModel.Value))
            activeModel = null;
        if (!activeModel.HasValue && live.Count != 0)
            activeModel = live.OrderByDescending(x => x.Rssi).First().Model;
        var matching = live.Where(x => x.Model == activeModel).ToList();
        // Most recent observation, regardless of BLE address. This prevents
        // a fixed address from suppressing updates after address rotation.
        displayed = matching.OrderByDescending(x => x.Received).FirstOrDefault();
        if (displayed is null && activeModel.HasValue)
            displayed = candidates.Where(x => x.Model == activeModel.Value)
                .OrderByDescending(x => x.Received).FirstOrDefault();
        bool fresh = displayed is not null && now - displayed.Received <= Fresh;
        if (displayed is not null && fresh)
        {
            string signature = $"{displayed.Left}:{displayed.Right}:{displayed.Case}:{displayed.LeftCharging}:{displayed.RightCharging}:{displayed.CaseCharging}";
            if (batterySignature != signature)
            {
                batterySignature = signature;
                lastBatteryChange = now;
            }
        }
        bool unchangedLong = displayed is not null && lastBatteryChange.HasValue && now - lastBatteryChange.Value > UnchangedWarning;
        if (displayed == null)
        {
            statusItem.Text = bluetoothError ? "Bluetooth-Scanner nicht verfügbar" : "Keine AirPods gefunden";
            leftItem.Text = "Links: –"; rightItem.Text = "Rechts: –"; caseItem.Text = "Case: –";
            leftItem.Visible = false; rightItem.Visible = false; caseItem.Visible = false;
        }
        else
        {
            statusItem.Text = "AirPods" + (!fresh ? " · Messung veraltet" : " · Signal empfangen");
            leftItem.Visible = displayed.Left.HasValue;
            rightItem.Visible = displayed.Right.HasValue;
            leftItem.Text = "Links: " + Format(displayed.Left, displayed.LeftCharging);
            rightItem.Text = "Rechts: " + Format(displayed.Right, displayed.RightCharging);
            caseItem.Text = "Case: " + Format(displayed.Case, displayed.CaseCharging);
            caseItem.Visible = displayed.Case.HasValue;
        }
        if (batteryWindow?.Visible == true)
        {
            try { batteryWindow.Render(displayed, fresh, lastBatteryChange); }
            catch (Exception ex) { CrashLog.Write("Update battery window", ex); batteryWindow.Hide(); }
        }
        string title = displayed is null ? "AirTray: Keine Werte" :
            $"AirPods{(!fresh ? " (alt)" : "")} | L:{Format(displayed.Left, false)} R:{Format(displayed.Right, false)}{(displayed.Case.HasValue ? $" C:{Format(displayed.Case, false)}" : "")}";
        tray.Text = title.Length <= 63 ? title : title[..63];
        Icon newIcon = CreateIcon(fresh ? displayed?.Average : null, fresh);
        tray.Icon = newIcon;
        renderedIcon?.Dispose();
        renderedIcon = newIcon;
    }

    private static string Format(int? percent, bool charging) => percent is null ? "–" : $"{percent}%{(charging ? " ⚡" : "")}";
    private void ShowPopup()
    {
        if (batteryWindow is null || batteryWindow.IsDisposed)
        {
            batteryWindow = new BatteryWindow();
            // A focus/deactivate callback here could hide the window during
            // creation. Keep an ordinary tool window and close with its X button.
        }
        if (batteryWindow.Visible) { batteryWindow.Hide(); return; }
        batteryWindow.Render(displayed, displayed is not null && DateTime.Now - displayed.Received <= Fresh, lastBatteryChange);
        var workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        batteryWindow.Location = new Point(
            Math.Min(workArea.Right - batteryWindow.Width - 12, Math.Max(workArea.Left + 12, Cursor.Position.X - batteryWindow.Width / 2)),
            Math.Max(workArea.Top + 12, workArea.Bottom - batteryWindow.Height - 12));
        batteryWindow.Show();
        batteryWindow.Activate();
    }

    private static Icon CreateIcon(int? value, bool fresh)
    {
        using var bmp = new Bitmap(64, 64);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        Color c = !fresh || value is null ? Color.Gray : value < 20 ? Color.FromArgb(218, 70, 60) : value < 40 ? Color.FromArgb(225, 157, 34) : Color.FromArgb(34, 151, 97);
        using var background = new SolidBrush(c);
        g.FillEllipse(background, 1, 1, 61, 61);
        string label = value?.ToString() ?? "?";
        float fontSize = label.Length > 2 ? 24 : 29;
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var foreground = new SolidBrush(Color.White);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(label, font, foreground, new RectangleF(0, 0, 64, 62), sf);
        IntPtr handle = bmp.GetHicon();
        try { using var temp = Icon.FromHandle(handle); return (Icon)temp.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static bool IsStartupEnabled()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(RunName) != null; }
        catch { return false; }
    }
    private void ToggleStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (startupItem.Checked) key.DeleteValue(RunName, false);
            else key.SetValue(RunName, $"\"{Application.ExecutablePath}\"");
            startupItem.Checked = !startupItem.Checked;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Autostart nicht geändert", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    private static void OpenBluetoothSettings()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
        catch { }
    }
    protected override void ExitThreadCore()
    {
        stopping = true;
        timer.Stop();
        batteryWindow?.Dispose();
        try { watcher.Stop(); } catch (Exception ex) { CrashLog.Write("Stop scan", ex); }
        tray.Visible = false;
        tray.Dispose();
        renderedIcon?.Dispose();
        watcher.Received -= OnReceived;
        capture.Dispose();
        dispatcher.Dispose();
        base.ExitThreadCore();
    }
}

// Modern, custom-drawn native WinForms popup. No nested progress bars or child controls;
// all rows, including the optional case row, share the same DPI-aware layout.
internal sealed class BatteryWindow : Form
{
    private BatteryReport? report;
    private bool fresh;
    private DateTime? lastChange;
    private static readonly Color Background = Color.FromArgb(250, 251, 253);
    private static readonly Color TextPrimary = Color.FromArgb(24, 32, 44);
    private static readonly Color TextSecondary = Color.FromArgb(108, 119, 133);
    private static readonly Color Accent = Color.FromArgb(41, 133, 105);
    private static readonly Color Low = Color.FromArgb(216, 76, 68);
    private static readonly Color Track = Color.FromArgb(228, 233, 239);

    public BatteryWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Background;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Width = 344;
        Height = 204;
        Padding = Padding.Empty;
        MinimumSize = new Size(280, 156);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        Deactivate += (_, _) => Hide();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Ask Windows 11 for rounded popup corners, where available.
        try
        {
            int preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int));
        }
        catch { /* Windows 10 or DWM unavailable: keep the plain rectangle. */ }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private float Scale => DeviceDpi / 96f;
    private int Px(float amount) => (int)Math.Round(amount * Scale);
    private int Rows => (report?.Left.HasValue == true ? 1 : 0)
                      + (report?.Right.HasValue == true ? 1 : 0)
                      + (report?.Case.HasValue == true ? 1 : 0);

    public void Render(BatteryReport? latest, bool isFresh, DateTime? changed)
    {
        if (InvokeRequired)
        {
            if (IsHandleCreated && !IsDisposed)
            {
                try { BeginInvoke(new Action(() => Render(latest, isFresh, changed))); }
                catch (InvalidOperationException) { }
            }
            return;
        }
        report = latest;
        fresh = isFresh;
        lastChange = changed;
        // Header: 74px; rows: 61px; footer: 28px. Nothing can be clipped at 3 rows.
        int desiredHeight = Px(96 + Math.Max(1, Rows) * 61 + 20);
        int desiredWidth = Px(344);
        if (Width != desiredWidth || Height != desiredHeight)
        {
            // Anchor bottom-right so the popover stays put while BLE updates arrive.
            var oldBottom = Bottom;
            var oldRight = Right;
            Size = new Size(desiredWidth, desiredHeight);
            if (Visible) Location = new Point(oldRight - Width, oldBottom - Height);
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Background);
        float s = Scale;
        float w = ClientSize.Width / s;
        using var headingFont = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var normalFont = new Font("Segoe UI", 12.8f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var numberFont = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var statusFont = new Font("Segoe UI", 11.4f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var titleBrush = new SolidBrush(TextPrimary);
        using var secondaryBrush = new SolidBrush(TextSecondary);

        // Header label; no guesses about the model when the broadcast model ID isn't trustworthy.
        Draw(g, "AirPods", headingFont, titleBrush, 22, 20);
        string status = report is null ? "Keine Akkudaten verfügbar" :
            !fresh ? "Zuletzt empfangen · nicht aktuell" : "Bluetooth-Signal empfangen";
        Draw(g, status, statusFont, secondaryBrush, 22, 47);
        using (var pen = new Pen(Track, Math.Max(1, s)))
            g.DrawLine(pen, 22 * s, 75 * s, (w - 22) * s, 75 * s);

        float top = 91;
        if (report is not null)
        {
            if (report.Left is int left) { DrawRow(g, "Links", left, report.LeftCharging, top, w, normalFont, numberFont); top += 61; }
            if (report.Right is int right) { DrawRow(g, "Rechts", right, report.RightCharging, top, w, normalFont, numberFont); top += 61; }
            if (report.Case is int batteryCase) { DrawRow(g, "Case", batteryCase, report.CaseCharging, top, w, normalFont, numberFont); top += 61; }
        }
        if (Rows == 0) Draw(g, "Keine Messwerte empfangen", normalFont, secondaryBrush, 22, 101);
    }

    private void DrawRow(Graphics g, string title, int value, bool charging, float top,
        float width, Font labelFont, Font amountFont)
    {
        float s = Scale;
        value = Math.Clamp(value, 0, 100);
        using var ink = new SolidBrush(TextPrimary);
        using var muted = new SolidBrush(TextSecondary);
        Draw(g, title, labelFont, ink, 22, top);
        string percent = $"{value}%" + (charging ? "  ⚡" : "");
        using var format = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Near };
        g.DrawString(percent, amountFont, ink,
            new RectangleF((width - 150) * s, (top - 1) * s, 128 * s, 23 * s), format);

        // Track starts and ends at the same x for every row, independent of text length.
        const float x = 22, trackHeight = 8;
        float trackWidth = Math.Max(1, width - 44);
        using var trackPath = Rounded(new RectangleF(x * s, (top + 31) * s, trackWidth * s, trackHeight * s), 4 * s);
        using (var trackBrush = new SolidBrush(Track)) g.FillPath(trackBrush, trackPath);
        float filled = trackWidth * value / 100f;
        if (filled > 0)
        {
            using var fillPath = Rounded(new RectangleF(x * s, (top + 31) * s,
                Math.Max(1, filled * s), trackHeight * s), Math.Min(4 * s, filled * s / 2));
            using var fill = new SolidBrush(value < 20 ? Low : Accent);
            g.FillPath(fill, fillPath);
        }
    }

    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float diameter = Math.Min(2 * radius, Math.Min(rect.Width, rect.Height));
        if (diameter <= 0) { path.AddRectangle(rect); return path; }
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void Draw(Graphics g, string value, Font font, Brush brush, float x, float y) =>
        g.DrawString(value, font, brush, x * Scale, y * Scale);
}