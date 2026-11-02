using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MultiClaude;

/// <summary>
/// Reads Claude's notifications from Windows' notification history (wpndatabase.db), through the
/// SQLite that ships with Windows (winsqlite3.dll).
///
/// Claude's notifications carry their session in a header (id="session-local_…") but no link, so a
/// click just starts a plain Claude, which goes to the default account. Windows removes a clicked
/// notification from the history, so the one that disappeared tells which session was clicked.
/// </summary>
internal static class NotificationHistory
{
    static readonly Regex SessionHeader = new(@"id=""session-(local_[0-9a-fA-F-]{36})""", RegexOptions.Compiled);

    static string DbPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Windows", "Notifications", "wpndatabase.db");

    /// <summary>Claude's notifications now in the history: notification id → session id (null if none).</summary>
    public static Dictionary<long, string?>? Read()
    {
        if (!File.Exists(DbPath)) return null;
        IntPtr db = IntPtr.Zero, stmt = IntPtr.Zero;
        try
        {
            if (sqlite3_open_v2(Encoding.UTF8.GetBytes(DbPath + "\0"), out db, SQLITE_OPEN_READONLY, IntPtr.Zero) != 0) return null;
            sqlite3_busy_timeout(db, 200);
            const string sql = "select n.Id, n.Payload from Notification n join NotificationHandler h on h.RecordId = n.HandlerId " +
                               "where h.PrimaryId like 'Claude\\_%' escape '\\'";
            if (sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes(sql + "\0"), -1, out stmt, IntPtr.Zero) != 0) return null;
            var result = new Dictionary<long, string?>();
            while (sqlite3_step(stmt) == SQLITE_ROW)
            {
                long id = sqlite3_column_int64(stmt, 0);
                int len = sqlite3_column_bytes(stmt, 1);
                IntPtr blob = sqlite3_column_blob(stmt, 1);
                string payload = blob == IntPtr.Zero || len <= 0 ? "" : Marshal.PtrToStringUTF8(blob, len);
                var m = SessionHeader.Match(payload);
                result[id] = m.Success ? m.Groups[1].Value : null;
            }
            return result;
        }
        catch (Exception ex) { Log.Write("notification history: " + ex.Message); return null; }
        finally
        {
            if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
            if (db != IntPtr.Zero) sqlite3_close(db);
        }
    }

    const int SQLITE_OPEN_READONLY = 1, SQLITE_ROW = 100;

    [DllImport("winsqlite3.dll")] static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_step(IntPtr stmt);
    [DllImport("winsqlite3.dll")] static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_column_bytes(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_close(IntPtr db);
}
