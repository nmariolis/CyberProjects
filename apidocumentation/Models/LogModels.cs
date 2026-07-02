using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web;

namespace Documentation.Models
{
    // ── Internal log: registered-user actions in the admin panel ────────────────

    public class InternalLogData
    {
        [JsonProperty("entries")]
        public List<InternalLogEntry> Entries { get; set; } = new List<InternalLogEntry>();
    }

    public class InternalLogEntry
    {
        [JsonProperty("timestamp")]  public DateTime Timestamp  { get; set; }
        [JsonProperty("userEmail")]  public string   UserEmail  { get; set; }
        [JsonProperty("userRole")]   public string   UserRole   { get; set; }
        [JsonProperty("action")]     public string   Action     { get; set; } // Create | Update | Delete | Approve | Register | ChangeRole | ToggleVisibility | AssignManager | ToggleManager
        [JsonProperty("category")]   public string   Category   { get; set; } // Endpoint | ContentSection | FieldDescription | Service | User
        [JsonProperty("targetId")]   public string   TargetId   { get; set; }
        [JsonProperty("targetName")] public string   TargetName { get; set; }
        [JsonProperty("details")]    public string   Details    { get; set; }
    }

    // ── Documentation log: end-user docs page access ────────────────────────────

    public class DocumentationLogData
    {
        [JsonProperty("entries")]
        public List<DocumentationLogEntry> Entries { get; set; } = new List<DocumentationLogEntry>();
    }

    public class DocumentationLogEntry
    {
        [JsonProperty("timestamp")]    public DateTime Timestamp    { get; set; }
        [JsonProperty("endpointName")] public string   EndpointName { get; set; }
        [JsonProperty("userName")]     public string   UserName     { get; set; }
        [JsonProperty("serviceHref")]  public string   ServiceHref  { get; set; }
        [JsonProperty("serviceName")]  public string   ServiceName  { get; set; }
    }

    // ── Per-day persistence ─────────────────────────────────────────────────────
    //
    // Each log category has its own directory under ~/Logs/, with one JSON file
    // per UTC day:
    //
    //     ~/Logs/internal/2026-05-06.json
    //     ~/Logs/documentation/2026-05-06.json
    //
    // Files persist across sessions, app-pool recycles, IIS restarts and
    // publishes. The directories are created on first write if they don't
    // exist. Per-category locking prevents concurrent writers from clobbering
    // each other.
    //
    // On first access we also migrate any legacy single-file logs from the
    // pre-rotation layout (~/Models/internalLog.json,
    // ~/Models/documentationLog.json) into per-day files, then rename the
    // legacy file with a .migrated suffix so we never run it twice.

    public static class LogStore
    {
        private const string InternalDir    = "~/Logs/internal";
        private const string DocDir         = "~/Logs/documentation";
        private const string LegacyInternal = "~/Models/internalLog.json";
        private const string LegacyDoc      = "~/Models/documentationLog.json";

        private static readonly object InternalLock = new object();
        private static readonly object DocLock      = new object();
        private static bool _internalMigrated = false;
        private static bool _docMigrated      = false;

        // ── Public API: Internal ────────────────────────────────────────────────

        public static void AppendInternal(InternalLogEntry entry)
        {
            if (entry == null) return;
            lock (InternalLock)
            {
                MigrateInternalUnsafe();
                var path = DayFileUnsafe(InternalDir, BucketDate(entry.Timestamp));
                if (path == null) return;
                var data = LoadDayUnsafe<InternalLogData>(path);
                data.Entries.Add(entry);
                File.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented));
            }
        }

        public static InternalLogData LoadInternalForDate(string yyyymmdd)
        {
            lock (InternalLock)
            {
                MigrateInternalUnsafe();
                var path = DayFileUnsafe(InternalDir, yyyymmdd);
                if (path == null) return new InternalLogData();
                return LoadDayUnsafe<InternalLogData>(path);
            }
        }

        public static List<string> ListInternalDates()
        {
            lock (InternalLock)
            {
                MigrateInternalUnsafe();
                return ListDatesUnsafe(InternalDir);
            }
        }

        // ── Public API: Documentation ───────────────────────────────────────────

        public static void AppendDocumentation(DocumentationLogEntry entry)
        {
            if (entry == null) return;
            lock (DocLock)
            {
                MigrateDocUnsafe();
                var path = DayFileUnsafe(DocDir, BucketDate(entry.Timestamp));
                if (path == null) return;
                var data = LoadDayUnsafe<DocumentationLogData>(path);
                data.Entries.Add(entry);
                File.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented));
            }
        }

        public static DocumentationLogData LoadDocumentationForDate(string yyyymmdd)
        {
            lock (DocLock)
            {
                MigrateDocUnsafe();
                var path = DayFileUnsafe(DocDir, yyyymmdd);
                if (path == null) return new DocumentationLogData();
                return LoadDayUnsafe<DocumentationLogData>(path);
            }
        }

        public static List<string> ListDocumentationDates()
        {
            lock (DocLock)
            {
                MigrateDocUnsafe();
                return ListDatesUnsafe(DocDir);
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static string BucketDate(DateTime ts)
        {
            // Ensure a UTC bucket regardless of how the caller built the timestamp.
            var utc = ts.Kind == DateTimeKind.Local ? ts.ToUniversalTime() : ts;
            return utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static string MapSafe(string virtualPath)
        {
            try { return HttpContext.Current?.Server?.MapPath(virtualPath); }
            catch { return null; }
        }

        private static string DayFileUnsafe(string virtualDir, string yyyymmdd)
        {
            if (string.IsNullOrWhiteSpace(yyyymmdd))
                yyyymmdd = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            // Strict format check also blocks any path-traversal trickery.
            DateTime _;
            if (!DateTime.TryParseExact(yyyymmdd, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return null;

            var dir = MapSafe(virtualDir);
            if (dir == null) return null;
            try { Directory.CreateDirectory(dir); } catch { return null; }
            return Path.Combine(dir, yyyymmdd + ".json");
        }

        private static T LoadDayUnsafe<T>(string path) where T : class, new()
        {
            if (!File.Exists(path)) return new T();
            try { return JsonConvert.DeserializeObject<T>(File.ReadAllText(path)) ?? new T(); }
            catch { return new T(); }
        }

        private static List<string> ListDatesUnsafe(string virtualDir)
        {
            var dir = MapSafe(virtualDir);
            if (dir == null || !Directory.Exists(dir)) return new List<string>();
            return Directory.GetFiles(dir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name =>
                {
                    DateTime _;
                    return DateTime.TryParseExact(name, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
                })
                .OrderByDescending(name => name, StringComparer.Ordinal)
                .ToList();
        }

        // ── One-time legacy migration ──────────────────────────────────────────

        private static void MigrateInternalUnsafe()
        {
            if (_internalMigrated) return;
            _internalMigrated = true;
            MigrateLegacy<InternalLogData, InternalLogEntry>(
                LegacyInternal, InternalDir, d => d.Entries, e => e.Timestamp);
        }

        private static void MigrateDocUnsafe()
        {
            if (_docMigrated) return;
            _docMigrated = true;
            MigrateLegacy<DocumentationLogData, DocumentationLogEntry>(
                LegacyDoc, DocDir, d => d.Entries, e => e.Timestamp);
        }

        private static void MigrateLegacy<TData, TEntry>(
            string legacyVirtual,
            string targetDirVirtual,
            Func<TData, List<TEntry>> getEntries,
            Func<TEntry, DateTime> getTimestamp)
            where TData : class, new()
        {
            var src = MapSafe(legacyVirtual);
            if (src == null || !File.Exists(src)) return;
            try
            {
                var data = JsonConvert.DeserializeObject<TData>(File.ReadAllText(src));
                if (data == null) { TryRenameMigrated(src); return; }
                var entries = getEntries(data) ?? new List<TEntry>();
                if (entries.Count > 0)
                {
                    foreach (var grp in entries.GroupBy(e => BucketDate(getTimestamp(e))))
                    {
                        var path = DayFileUnsafe(targetDirVirtual, grp.Key);
                        if (path == null) continue;
                        var dayData = LoadDayUnsafe<TData>(path);
                        var dayEntries = getEntries(dayData);
                        dayEntries.AddRange(grp);
                        File.WriteAllText(path, JsonConvert.SerializeObject(dayData, Formatting.Indented));
                    }
                }
                TryRenameMigrated(src);
            }
            catch
            {
                // Leave legacy file in place; will retry next process restart.
            }
        }

        private static void TryRenameMigrated(string srcPath)
        {
            try
            {
                var dst = srcPath + ".migrated";
                if (File.Exists(dst))
                    dst = dst + "." + Guid.NewGuid().ToString("N").Substring(0, 6);
                File.Move(srcPath, dst);
            }
            catch { /* best-effort */ }
        }
    }
}
