using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Xml.Linq;
using VNotch.Models;

namespace VNotch.Services;

public sealed partial class AppNotificationService
{
    private const int SqliteOpenReadOnly = 0x00000001;
    private const int SqliteRow = 100;
    private const int SqliteOk = 0;

    private readonly object _wpnLock = new();
    private readonly HashSet<long> _seenWpnIds = [];
    private long _lastSeenWpnId;
    private long _lastSeenWpnArrivalTime;
    private bool _wpnSeeded;
    private string? _wpnSnapshotDir;

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_open_v2")]
    private static extern int SqliteOpenV2(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
        out IntPtr db,
        int flags,
        IntPtr zVfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_close_v2")]
    private static extern int SqliteCloseV2(IntPtr db);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_prepare_v2")]
    private static extern int SqlitePrepareV2(
        IntPtr db,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string zSql,
        int nByte,
        out IntPtr ppStmt,
        out IntPtr pzTail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_step")]
    private static extern int SqliteStep(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_finalize")]
    private static extern int SqliteFinalize(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_int64")]
    private static extern int SqliteBindInt64(IntPtr stmt, int index, long value);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_int64")]
    private static extern long SqliteColumnInt64(IntPtr stmt, int col);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_type")]
    private static extern int SqliteColumnType(IntPtr stmt, int col);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_bytes")]
    private static extern int SqliteColumnBytes(IntPtr stmt, int col);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_blob")]
    private static extern IntPtr SqliteColumnBlob(IntPtr stmt, int col);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_text")]
    private static extern IntPtr SqliteColumnText(IntPtr stmt, int col);

    private static string GetWpnDbPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Windows", "Notifications", "wpndatabase.db");

    private string EnsureWpnSnapshotDir()
    {
        if (!string.IsNullOrEmpty(_wpnSnapshotDir)) return _wpnSnapshotDir;
        string dir = Path.Combine(Path.GetTempPath(), "ATS-Notch", "wpn-snapshot");
        Directory.CreateDirectory(dir);
        _wpnSnapshotDir = dir;
        return dir;
    }

    private string? CopyWpnDbSnapshot()
    {
        string srcDb = GetWpnDbPath();
        if (!File.Exists(srcDb)) return null;

        string dir = EnsureWpnSnapshotDir();
        string dstDb = Path.Combine(dir, "wpndatabase.db");

        CopySharedFile(srcDb, dstDb);
        CopyOptionalSharedFile(srcDb + "-wal", dstDb + "-wal");
        CopyOptionalSharedFile(srcDb + "-shm", dstDb + "-shm");
        return dstDb;
    }

    private static void CopySharedFile(string source, string destination)
    {
        using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        src.CopyTo(dst);
    }

    private static void CopyOptionalSharedFile(string source, string destination)
    {
        try
        {
            if (File.Exists(source))
            {
                CopySharedFile(source, destination);
            }
            else if (File.Exists(destination))
            {
                File.Delete(destination);
            }
        }
        catch
        {
            // Ignore transient WAL/SHM lock races.
        }
    }

    private async Task RunWpnDbListenerLoopAsync(CancellationToken ct)
    {
        string dbPath = GetWpnDbPath();
        string? dir = Path.GetDirectoryName(dbPath);
        if (string.IsNullOrEmpty(dir)) return;

        RuntimeLog.Log(LogTag, $"Starting WPN database notification fallback listener ({dbPath}).");
        SeedWpnInitialState();

        using var pulse = new SemaphoreSlim(0, 8);
        FileSystemWatcher? watcher = null;
        try
        {
            if (Directory.Exists(dir))
            {
                watcher = new FileSystemWatcher(dir, "wpndatabase.db*")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                FileSystemEventHandler onFsChanged = (_, _) =>
                {
                    try
                    {
                        if (pulse.CurrentCount == 0) pulse.Release();
                    }
                    catch { }
                };
                watcher.Changed += onFsChanged;
                watcher.Created += onFsChanged;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogTag, $"WPN FileSystemWatcher unavailable: {ex.Message}");
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await pulse.WaitAsync(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
                    // Short coalesce so WAL flush settles before snapshot copy.
                    await Task.Delay(25, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!_isEnabledCheck()) continue;
                PollWpnDatabase();
            }
        }
        finally
        {
            try { watcher?.Dispose(); } catch { }
            CleanupWpnSnapshotDir();
        }
    }

    private void SeedWpnInitialState()
    {
        try
        {
            string? snapshot = CopyWpnDbSnapshot();
            if (snapshot == null) return;

            if (SqliteOpenV2(snapshot, out IntPtr db, SqliteOpenReadOnly, IntPtr.Zero) != SqliteOk)
            {
                if (db != IntPtr.Zero) SqliteCloseV2(db);
                return;
            }

            try
            {
                const string sql = "SELECT COALESCE(MAX(Id), 0), COALESCE(MAX(ArrivalTime), 0) FROM Notification WHERE Type IN ('toast', 'toastCondensed')";
                if (SqlitePrepareV2(db, sql, -1, out IntPtr stmt, out _) == SqliteOk)
                {
                    try
                    {
                        if (SqliteStep(stmt) == SqliteRow)
                        {
                            long maxId = SqliteColumnInt64(stmt, 0);
                            long maxArrival = SqliteColumnInt64(stmt, 1);
                            lock (_wpnLock)
                            {
                                _lastSeenWpnId = maxId;
                                _lastSeenWpnArrivalTime = maxArrival;
                                _wpnSeeded = true;
                            }
                            RuntimeLog.Log(LogTag, $"WPN DB seeded at Id={maxId}, ArrivalTime={maxArrival}.");
                        }
                    }
                    finally
                    {
                        SqliteFinalize(stmt);
                    }
                }
            }
            finally
            {
                SqliteCloseV2(db);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogTag, $"WPN seed failed: {ex.Message}");
        }
    }

    private void PollWpnDatabase()
    {
        List<AppNotificationInfo>? pending = null;

        try
        {
            if (!_wpnSeeded)
            {
                SeedWpnInitialState();
                return;
            }

            string? snapshot = CopyWpnDbSnapshot();
            if (snapshot == null) return;

            if (SqliteOpenV2(snapshot, out IntPtr db, SqliteOpenReadOnly, IntPtr.Zero) != SqliteOk)
            {
                if (db != IntPtr.Zero) SqliteCloseV2(db);
                return;
            }

            long lastId;
            long lastArrival;
            lock (_wpnLock)
            {
                lastId = _lastSeenWpnId;
                lastArrival = _lastSeenWpnArrivalTime;
            }

            try
            {
                const string sql =
                    "SELECT n.Id, n.ArrivalTime, n.Payload, h.PrimaryId " +
                    "FROM Notification n " +
                    "LEFT JOIN NotificationHandler h ON n.HandlerId = h.RecordId " +
                    "WHERE n.Type IN ('toast', 'toastCondensed') AND (n.Id > ?1 OR n.ArrivalTime > ?2) " +
                    "ORDER BY n.ArrivalTime ASC, n.Id ASC";

                if (SqlitePrepareV2(db, sql, -1, out IntPtr stmt, out _) != SqliteOk)
                {
                    return;
                }

                try
                {
                    SqliteBindInt64(stmt, 1, lastId);
                    SqliteBindInt64(stmt, 2, lastArrival);

                    long nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    while (SqliteStep(stmt) == SqliteRow)
                    {
                        long rowId = SqliteColumnInt64(stmt, 0);
                        long arrivalFileTime = SqliteColumnInt64(stmt, 1);

                        lock (_wpnLock)
                        {
                            if (!_seenWpnIds.Add(rowId))
                            {
                                continue;
                            }
                            if (rowId > _lastSeenWpnId) _lastSeenWpnId = rowId;
                            if (arrivalFileTime > _lastSeenWpnArrivalTime) _lastSeenWpnArrivalTime = arrivalFileTime;
                            if (_seenWpnIds.Count > 512)
                            {
                                _seenWpnIds.Clear();
                                _seenWpnIds.Add(rowId);
                            }
                        }

                        string xmlPayload = ReadColumnUtf8String(stmt, 2);
                        string primaryId = ReadColumnUtf8String(stmt, 3);
                        var info = BuildWpnNotificationInfo(rowId, arrivalFileTime, nowUnixMs, primaryId, xmlPayload);
                        if (info != null)
                        {
                            pending ??= new List<AppNotificationInfo>(2);
                            pending.Add(info);
                        }
                    }
                }
                finally
                {
                    SqliteFinalize(stmt);
                }
            }
            finally
            {
                SqliteCloseV2(db);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogTag, $"WPN poll error: {ex.Message}");
        }

        if (pending != null)
        {
            for (int i = 0; i < pending.Count; i++)
            {
                RuntimeLog.Log(LogTag, $"WPN notification captured: [{pending[i].AppName}] {pending[i].Title}");
                NotificationReceived?.Invoke(pending[i]);
            }
        }
    }

    private static string ReadColumnUtf8String(IntPtr stmt, int col)
    {
        int colType = SqliteColumnType(stmt, col);
        if (colType == 5) return string.Empty; // SQLITE_NULL

        IntPtr ptr = colType == 4 ? SqliteColumnBlob(stmt, col) : SqliteColumnText(stmt, col);
        int byteLen = SqliteColumnBytes(stmt, col);
        if (ptr == IntPtr.Zero || byteLen <= 0) return string.Empty;

        byte[] buffer = new byte[byteLen];
        Marshal.Copy(ptr, buffer, 0, byteLen);
        string text = Encoding.UTF8.GetString(buffer);
        return text.Trim('\0', '\uFEFF', ' ', '\t', '\r', '\n');
    }

    private static AppNotificationInfo? BuildWpnNotificationInfo(
        long rowId,
        long arrivalFileTime,
        long nowUnixMs,
        string primaryId,
        string xmlPayload)
    {
        if (string.IsNullOrWhiteSpace(xmlPayload)) return null;

        DateTimeOffset creationTime;
        try
        {
            creationTime = arrivalFileTime > 0
                ? DateTimeOffset.FromFileTime(arrivalFileTime)
                : DateTimeOffset.UtcNow;
        }
        catch
        {
            creationTime = DateTimeOffset.UtcNow;
        }

        // Ignore notifications older than 20 seconds on wakeup/poll races.
        long creationUnixMs = creationTime.ToUnixTimeMilliseconds();
        if (nowUnixMs - creationUnixMs > 20_000) return null;

        if (!TryExtractToastText(xmlPayload, out string rawTitle, out string rawBody))
        {
            return null;
        }

        string friendlyAppName = DeriveFriendlyAppName(primaryId);
        uint notifId = unchecked((uint)rowId);

        if (!TryEvaluateAndFormat(
                notifId,
                creationUnixMs,
                nowUnixMs,
                primaryId,
                friendlyAppName,
                rawTitle,
                rawBody,
                out string normApp,
                out string normTitle,
                out string normBody))
        {
            return null;
        }

        ImageSource? icon = TryResolveAppIcon(primaryId, normApp);
        return new AppNotificationInfo
        {
            Id = notifId,
            AppId = primaryId,
            AppName = normApp,
            Title = normTitle,
            Body = normBody,
            AppIcon = icon,
            CreationTime = creationTime
        };
    }

    private static bool TryExtractToastText(string xmlPayload, out string title, out string body)
    {
        title = string.Empty;
        body = string.Empty;

        int start = xmlPayload.IndexOf('<');
        int end = xmlPayload.LastIndexOf('>');
        if (start < 0 || end <= start) return false;

        string cleanXml = xmlPayload.Substring(start, end - start + 1);
        try
        {
            var doc = XDocument.Parse(cleanXml, LoadOptions.None);
            var textLines = doc.Descendants()
                .Where(e => string.Equals(e.Name.LocalName, "text", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Value?.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            if (textLines.Count == 0) return false;
            title = textLines[0]!;
            if (textLines.Count == 2)
            {
                body = textLines[1]!;
            }
            else if (textLines.Count > 2)
            {
                body = string.Join("\n", textLines.Skip(1).Take(2));
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string DeriveFriendlyAppName(string primaryId)
    {
        if (string.IsNullOrWhiteSpace(primaryId)) return "Windows";
        string id = primaryId.Trim();

        if (id.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase)) return "WhatsApp";
        if (id.Contains("Telegram", StringComparison.OrdinalIgnoreCase)) return "Telegram";
        if (id.Contains("Discord", StringComparison.OrdinalIgnoreCase)) return "Discord";
        if (id.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (id.Contains("Antigravity", StringComparison.OrdinalIgnoreCase)) return "Antigravity";
        if (id.Contains("VisualStudioCode", StringComparison.OrdinalIgnoreCase) || id.EndsWith("\\Code.exe", StringComparison.OrdinalIgnoreCase)) return "VS Code";
        if (id.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (id.Contains("msedge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (id.Contains("SecHealthUI", StringComparison.OrdinalIgnoreCase)) return "Windows Security";
        if (id.StartsWith("Windows.SystemToast", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("NotifyIconGeneratedAumid", StringComparison.OrdinalIgnoreCase)) return "Windows System";
        if (id.Contains("PowerShell", StringComparison.OrdinalIgnoreCase)) return "PowerShell";

        // Package family: "5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App" -> "WhatsAppDesktop"
        int bang = id.IndexOf('!');
        if (bang > 0) id = id[..bang];
        int underscore = id.IndexOf('_');
        if (underscore > 0) id = id[..underscore];
        int slash = Math.Max(id.LastIndexOf('\\'), id.LastIndexOf('/'));
        if (slash >= 0 && slash + 1 < id.Length) id = id[(slash + 1)..];
        int dot = id.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < id.Length)
        {
            string tail = id[(dot + 1)..];
            if (!string.Equals(tail, "exe", StringComparison.OrdinalIgnoreCase))
            {
                id = tail;
            }
            else
            {
                id = id[..dot];
            }
        }
        return string.IsNullOrWhiteSpace(id) ? "Windows" : id;
    }

    private void CleanupWpnSnapshotDir()
    {
        try
        {
            if (!string.IsNullOrEmpty(_wpnSnapshotDir) && Directory.Exists(_wpnSnapshotDir))
            {
                Directory.Delete(_wpnSnapshotDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup lock races.
        }
    }
}

