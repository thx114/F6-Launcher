using System.Runtime.InteropServices;

namespace F6Launcher.Background;

internal static class PlayTimeWriter
{
    internal static void Write(GameWatch game, int state, long timestamp)
    {
        if (!game.RecordPlayTime || string.IsNullOrWhiteSpace(game.DatabasePath) || !File.Exists(game.DatabasePath)) return;
        nint db = 0;
        try
        {
            if (sqlite3_open_v2(game.DatabasePath, out db, 2, 0) != 0) throw new IOException("Open playtime database failed");
            sqlite3_busy_timeout(db, 1000);
            string biz = game.GameKey.Replace("'", "''");
            string sql = $"INSERT OR REPLACE INTO PlayTimeItem (TimeStamp, GameBiz, Pid, State, CursorPos, Message) VALUES ({timestamp}, '{biz}', {game.Pid}, {state}, 0, 'F6 lightweight host');";
            if (sqlite3_exec(db, sql, 0, 0, out var error) != 0)
            {
                string reason = Marshal.PtrToStringUTF8(error) ?? "Playtime write failed";
                sqlite3_free(error); throw new IOException(reason);
            }
        }
        catch (Exception ex) { Program.Log("playtime: " + ex.Message); }
        finally { if (db != 0) sqlite3_close(db); }
    }
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out nint db, int flags, nint vfs);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(nint db);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(nint db, int milliseconds);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_exec(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, nint callback, nint data, out nint error);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern void sqlite3_free(nint error);
}
