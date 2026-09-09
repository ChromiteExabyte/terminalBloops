using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TerminalBloops.Core;

public sealed record ProcessChange(ProcessRecord? Record, bool NewlyCompleted);

/// <summary>Serializes durable process state and the source bookmark in the same transaction.</summary>
public sealed class HistoryStore : IDisposable
{
    private readonly object gate = new();
    private readonly SqliteConnection db;
    public HistoryStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        db.Open();
        Execute("PRAGMA auto_vacuum=INCREMENTAL; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;");
        Execute("""
            CREATE TABLE IF NOT EXISTS processes (
                id TEXT PRIMARY KEY, pid INTEGER NOT NULL, start INTEGER NOT NULL, end INTEGER,
                user TEXT NOT NULL COLLATE NOCASE, image TEXT NOT NULL, parentImage TEXT NOT NULL,
                commandLine TEXT NOT NULL, terminal INTEGER NOT NULL, host INTEGER NOT NULL,
                clientId TEXT, evidence INTEGER NOT NULL, payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_process_start ON processes(start DESC);
            CREATE INDEX IF NOT EXISTS ix_process_pid ON processes(pid,start,end);
            CREATE INDEX IF NOT EXISTS ix_process_user ON processes(user,start DESC);
            CREATE TABLE IF NOT EXISTS exits (id TEXT PRIMARY KEY, pid INTEGER NOT NULL, end INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS notices (id INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, kind TEXT NOT NULL, message TEXT NOT NULL);
            PRAGMA user_version=1;
            """);
    }

    public RecorderCheckpoint GetCheckpoint()
    {
        lock (gate) return new(GetMetadata("bookmark"), long.TryParse(GetMetadata("recordId"), out var id) ? id : 0, GetMetadata("eventFingerprint"));
    }

    public ProcessChange Apply(CaptureEnvelope envelope)
    {
        lock (gate)
        {
            using var transaction = db.BeginTransaction();
            ProcessRecord? changed = null;
            var newlyCompleted = false;
            if (envelope.Started is { } incoming)
            {
                var prior = GetInternal(incoming.Id, transaction);
                DateTimeOffset? end = prior?.EndUtc;
                if (end is null)
                {
                    using var pending = Command("SELECT end FROM exits WHERE id=$id", transaction, ("$id", incoming.Id));
                    if (pending.ExecuteScalar() is long ticks) end = new DateTimeOffset(ticks, TimeSpan.Zero);
                }
                changed = incoming with
                {
                    EndUtc = end ?? incoming.EndUtc,
                    Evidence = prior?.Evidence ?? incoming.Evidence,
                    WindowTitle = prior?.WindowTitle ?? incoming.WindowTitle,
                    WindowFirstSeenUtc = prior?.WindowFirstSeenUtc,
                    WindowLastSeenUtc = prior?.WindowLastSeenUtc,
                    CorrelatedClientId = prior?.CorrelatedClientId,
                    IsTerminal = incoming.IsTerminal || prior?.IsTerminal == true
                };
                newlyCompleted = prior?.EndUtc is null && changed.EndUtc is not null;
                Upsert(changed, transaction);
                Execute("DELETE FROM exits WHERE id=$id", transaction, ("$id", incoming.Id));
            }
            if (envelope.Exited is { } exit)
            {
                var prior = GetInternal(exit.Id, transaction);
                if (prior is not null)
                {
                    newlyCompleted = prior.EndUtc is null;
                    changed = prior with { EndUtc = prior.EndUtc ?? exit.EndUtc };
                    Upsert(changed, transaction);
                }
                else Execute("INSERT OR IGNORE INTO exits VALUES($id,$pid,$end)", transaction,
                    ("$id", exit.Id), ("$pid", exit.ProcessId), ("$end", exit.EndUtc.UtcTicks));
            }
            SetMetadata("bookmark", envelope.BookmarkXml, transaction);
            SetMetadata("recordId", envelope.RecordId.ToString(), transaction);
            SetMetadata("eventFingerprint", envelope.Fingerprint, transaction);
            transaction.Commit();
            return new(changed, newlyCompleted);
        }
    }

    public ProcessRecord? Get(string id) { lock (gate) return GetInternal(id); }

    public ProcessRecord? FindAt(int pid, DateTimeOffset when)
    {
        lock (gate)
        {
            using var command = Command("SELECT payload FROM processes WHERE pid=$pid AND start<=$at AND (end IS NULL OR end>=$at) ORDER BY start DESC LIMIT 1",
                null, ("$pid", pid), ("$at", when.UtcTicks));
            return ReadRecord(command.ExecuteScalar());
        }
    }

    public bool ApplyObservation(WindowObservation observation)
    {
        if (!observation.IsTerminalWindow) return true;
        lock (gate)
        {
            var host = FindAt(observation.ProcessId, observation.TimestampUtc);
            var client = observation.ClientProcessId is { } clientPid ? FindAt(clientPid, observation.TimestampUtc) : null;
            if (host is null || (observation.ClientProcessId is not null && client is null)) return false;
            // Sysmon's UtcTime is millisecond precision. An OS creation-time snapshot
            // must match that same millisecond before a PID is treated as identity.
            if (!SameCreationTime(host, observation.ProcessStartUtc)
                || (client is not null && !SameCreationTime(client, observation.ClientProcessStartUtc))) return false;
            using var transaction = db.BeginTransaction();
            var target = client ?? host;
            if (observation.IsClosed)
            {
                Upsert(target with { WindowLastSeenUtc = target.WindowLastSeenUtc is { } previous && previous > observation.TimestampUtc ? previous : observation.TimestampUtc }, transaction);
            }
            else
            {
                target = target with
                {
                    IsTerminal = true,
                    Evidence = target.IsHost && client is null ? WindowEvidence.CommandUnresolved : WindowEvidence.Observed,
                    WindowTitle = observation.Title,
                    WindowFirstSeenUtc = target.WindowFirstSeenUtc is { } previous && previous < observation.TimestampUtc ? previous : observation.TimestampUtc
                };
                Upsert(target, transaction);
                if (client is not null && host.Id != client.Id && host.IsHost)
                    Upsert(host with { CorrelatedClientId = client.Id, WindowFirstSeenUtc = host.WindowFirstSeenUtc ?? observation.TimestampUtc,
                        WindowTitle = observation.Title, Evidence = WindowEvidence.Observed }, transaction);
            }
            transaction.Commit();
            return true;
        }
    }

    private static bool SameCreationTime(ProcessRecord record, DateTimeOffset? observed)
        => observed is { } time && Math.Abs(record.StartUtc.UtcTicks-time.UtcTicks) < TimeSpan.TicksPerMillisecond;

    public IReadOnlyList<ProcessRecord> Query(HistoryFilter filter)
    {
        lock (gate)
        {
            var sql = "SELECT payload FROM processes WHERE terminal=1 AND (host=0 OR clientId IS NULL)";
            var parameters = new List<(string, object?)>();
            if (!string.IsNullOrEmpty(filter.User)) { sql += " AND user=$user"; parameters.Add(("$user", filter.User)); }
            if (filter.BriefOnly) sql += " AND end IS NOT NULL AND end>=start AND end-start<=50000000";
            if (filter.Evidence is { } evidence) { sql += " AND evidence=$evidence"; parameters.Add(("$evidence", (int)evidence)); }
            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                sql += " AND (instr(lower(image),$search)>0 OR instr(lower(commandLine),$search)>0 OR instr(lower(parentImage),$search)>0)";
                parameters.Add(("$search", filter.Search.ToLowerInvariant()));
            }
            sql += " ORDER BY start DESC LIMIT $limit";
            parameters.Add(("$limit", Math.Clamp(filter.Limit, 1, 10000)));
            using var command = Command(sql, null, parameters.ToArray());
            using var reader = command.ExecuteReader();
            var records = new List<ProcessRecord>();
            while (reader.Read()) if (ReadRecord(reader.GetString(0)) is { } record) records.Add(record);
            return records;
        }
    }

    public IReadOnlyList<ProcessRecord> GetAncestors(string id)
    {
        lock (gate)
        {
            var ancestors = new List<ProcessRecord>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
            var record = GetInternal(id);
            while (record is not null && !string.IsNullOrEmpty(record.ParentId) && visited.Add(record.ParentId) && ancestors.Count < 32)
            {
                var parent = GetInternal(record.ParentId);
                if (parent is null) break;
                ancestors.Add(parent);
                record = parent;
            }
            return ancestors;
        }
    }

    public void AddNotice(CaptureNotice notice)
    {
        lock (gate)
        {
            Execute("INSERT INTO notices(at,kind,message) VALUES($at,$kind,$message)", null,
                ("$at", notice.AtUtc.ToUnixTimeMilliseconds()), ("$kind", notice.Kind), ("$message", notice.Message));
            Execute("DELETE FROM notices WHERE id NOT IN (SELECT id FROM notices ORDER BY id DESC LIMIT 200)");
        }
    }

    public IReadOnlyList<CaptureNotice> GetNotices()
    {
        lock (gate)
        {
            using var command = Command("SELECT at,kind,message FROM notices ORDER BY id DESC LIMIT 100");
            using var reader = command.ExecuteReader();
            var result = new List<CaptureNotice>();
            while (reader.Read()) result.Add(new(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)), reader.GetString(1), reader.GetString(2)));
            return result;
        }
    }

    public long Prune(DateTimeOffset now, long maxPayloadBytes = 128 * 1024 * 1024)
    {
        lock (gate)
        {
            var cutoff = now.AddDays(-7).UtcTicks;
            using var transaction = db.BeginTransaction();
            using var ageDelete = Command("DELETE FROM processes WHERE start<$cutoff", transaction, ("$cutoff", cutoff));
            long removed = ageDelete.ExecuteNonQuery();
            Execute("DELETE FROM exits WHERE end<$cutoff", transaction, ("$cutoff", cutoff));
            using var size = Command("SELECT COALESCE(SUM(length(CAST(payload AS BLOB))),0) FROM processes", transaction);
            var bytes = (long)size.ExecuteScalar()!;
            while (bytes > maxPayloadBytes)
            {
                using var remove = Command("DELETE FROM processes WHERE id IN (SELECT id FROM processes ORDER BY start LIMIT 1000)", transaction);
                var count = remove.ExecuteNonQuery();
                removed += count;
                if (count == 0) break;
                bytes = (long)size.ExecuteScalar()!;
            }
            transaction.Commit();
            Execute("PRAGMA wal_checkpoint(TRUNCATE); PRAGMA incremental_vacuum(1000);");
            return removed;
        }
    }

    public AppSettings LoadSettings()
    {
        lock (gate)
        {
            var json = GetMetadata("settings");
            try { return json is null ? new() : JsonSerializer.Deserialize<AppSettings>(json) ?? new(); }
            catch (JsonException) { return new(); }
        }
    }

    public void SaveSettings(AppSettings settings) { lock (gate) SetMetadata("settings", JsonSerializer.Serialize(settings)); }

    private ProcessRecord? GetInternal(string id, SqliteTransaction? transaction = null)
    {
        using var command = Command("SELECT payload FROM processes WHERE id=$id", transaction, ("$id", id));
        return ReadRecord(command.ExecuteScalar());
    }

    private static ProcessRecord? ReadRecord(object? payload) => payload is string json ? JsonSerializer.Deserialize<ProcessRecord>(json) : null;

    private void Upsert(ProcessRecord record, SqliteTransaction? transaction = null) => Execute("""
        INSERT INTO processes(id,pid,start,end,user,image,parentImage,commandLine,terminal,host,clientId,evidence,payload)
        VALUES($id,$pid,$start,$end,$user,$image,$parent,$command,$terminal,$host,$client,$evidence,$payload)
        ON CONFLICT(id) DO UPDATE SET pid=excluded.pid,start=excluded.start,end=excluded.end,user=excluded.user,
        image=excluded.image,parentImage=excluded.parentImage,commandLine=excluded.commandLine,terminal=excluded.terminal,
        host=excluded.host,clientId=excluded.clientId,evidence=excluded.evidence,payload=excluded.payload
        """, transaction, ("$id", record.Id), ("$pid", record.ProcessId), ("$start", record.StartUtc.UtcTicks),
        ("$end", record.EndUtc?.UtcTicks), ("$user", record.User), ("$image", record.Image),
        ("$parent", record.ParentImage), ("$command", record.CommandLine), ("$terminal", record.IsTerminal ? 1 : 0),
        ("$host", record.IsHost ? 1 : 0), ("$client", record.CorrelatedClientId), ("$evidence", (int)record.Evidence),
        ("$payload", JsonSerializer.Serialize(record)));

    private string? GetMetadata(string key)
    {
        using var command = Command("SELECT value FROM metadata WHERE key=$key", null, ("$key", key));
        return command.ExecuteScalar() as string;
    }
    private void SetMetadata(string key, string value, SqliteTransaction? transaction = null) => Execute(
        "INSERT INTO metadata VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", transaction, ("$key", key), ("$value", value));
    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private void Execute(string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, transaction, parameters);
        command.ExecuteNonQuery();
    }
    public void Dispose() { lock (gate) db.Dispose(); }
}
