using System.Text;

namespace AirTray;

// Opt-in diagnostic log. No permanent Bluetooth addresses are written. Raw Apple
// manufacturer payloads may still contain device-specific data: do not publish unredacted.
internal sealed class BleCapture : IDisposable
{
    private const long MaxBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(5);
    private readonly object gate = new();
    private readonly Dictionary<ulong, int> deviceIds = new();
    private StreamWriter? writer;
    private DateTimeOffset started;
    private int nextId;
    private int count;
    private long bytesWritten;
    private string? lastFile;
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirTray", "Diagnostics");
    public bool Active { get { lock (gate) return writer != null; } }
    public string? LastFile { get { lock (gate) return lastFile; } }

    public string Start()
    {
        lock (gate)
        {
            StopLocked("new capture");
            Directory.CreateDirectory(Folder);
            var file = Path.Combine(Folder, $"ble-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            writer = new StreamWriter(new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
            lastFile = file;
            started = DateTimeOffset.UtcNow;
            count = 0;
            bytesWritten = 0;
            nextId = 0;
            deviceIds.Clear();
            WriteLocked("# AirTray BLE diagnosis — Apple manufacturer advertisements only; times are UTC; device IDs are session-local aliases");
            WriteLocked("# WARNING: raw Apple BLE payloads can reveal device-related information. Review before sharing.");
            return file;
        }
    }

    public void Stop(string reason = "user stopped") { lock (gate) StopLocked(reason); }
    private void StopLocked(string reason)
    {
        if (writer == null) return;
        try { WriteLocked($"# STOP {DateTimeOffset.UtcNow:O} reason={reason} captured={count}"); writer.Dispose(); }
        catch { try { writer.Dispose(); } catch { } }
        finally { writer = null; }
    }
    private void WriteLocked(string line)
    {
        if (writer is null) return;
        writer.WriteLine(line);
        bytesWritten += Encoding.UTF8.GetByteCount(line) + 2;
    }
    public void Packet(ulong address, short rssi, byte[] payload, BatteryReport? parsed, string decision)
    {
        lock (gate)
        {
            if (writer is null) return;
            if (DateTimeOffset.UtcNow - started >= MaxDuration || bytesWritten >= MaxBytes) { StopLocked("limit: five minutes or 8 MiB"); return; }
            if (!deviceIds.TryGetValue(address, out int id)) { id = ++nextId; deviceIds[address] = id; }
            // All Apple packets, not just packets matching the current AirPods parser.
            // This lets us investigate packet layout and spurious model selection.
            string line = $"{DateTimeOffset.UtcNow:O} device=D{id} rssi={rssi} apple_hex={Convert.ToHexString(payload)} " +
                (parsed is null ? "parse=ignored" :
                $"parse=ok model=0x{parsed.Model:X4} L={parsed.Left?.ToString() ?? "-"} R={parsed.Right?.ToString() ?? "-"} C={parsed.Case?.ToString() ?? "-"} LC={parsed.LeftCharging} RC={parsed.RightCharging} CC={parsed.CaseCharging}") +
                $" decision={decision}";
            try { WriteLocked(line); count++; }
            catch (Exception ex) { StopLocked("I/O failure: " + ex.GetType().Name); }
        }
    }
    public void Dispose() => Stop("application exited");
}