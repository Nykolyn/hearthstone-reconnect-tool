using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ReconnectorCore
{
    /// <summary>
    /// Finds the address of the game server Hearthstone is currently playing on, by reading
    /// Hearthstone's own network log.
    ///
    /// Why this matters: Hearthstone keeps several connections open at once — the game server
    /// (port 3724), the Battle.net/Aurora session (port 1119) and assorted HTTPS services.
    /// Only the game-server one may be closed. Killing the Battle.net socket forces the client
    /// through a full re-login before it can even start rejoining the match, which is slow and
    /// usually ends in "reconnect failed" with nothing but an Exit button. So we work hard to
    /// identify the right connection instead of closing everything.
    ///
    /// The log line looks like this (note Blizzard's own spelling — "GotoGameServe", no 'r'):
    ///   I 22:38:26.8852240 Network.GotoGameServe() - address= 37.244.26.45:3724, game=7500, ...
    /// It is written to Logs\Hearthstone_&lt;timestamp&gt;\GameNetLogger.log and mirrored into
    /// Hearthstone.log with a [GameNetLogger] prefix.
    ///
    /// Scanning is cached and throttled: callers refresh it in the background so that pressing
    /// the reconnect button costs no disk I/O at all.
    /// </summary>
    public static class GameServerLocator
    {
        // Accepts both Blizzard's "GotoGameServe" and a future corrected "GotoGameServer",
        // with or without the [GameNetLogger] prefix used in the combined Hearthstone.log.
        private static readonly Regex GotoGameServer = new Regex(
            @"Network\.GotoGameServe\w*\s*\(\)\s*-\s*address\s*=\s*(\d{1,3}(?:\.\d{1,3}){3})\s*:\s*(\d{1,5})",
            RegexOptions.Compiled);

        // Hearthstone's network log is small, but Hearthstone.log can grow to tens of MB in a
        // long session. Only the tail is read so a scan stays in the single-digit milliseconds.
        private const int TailBytes = 512 * 1024;

        private static readonly object Sync = new object();
        private static string _addr;
        private static ushort _port;
        private static DateTime _lastScanUtc = DateTime.MinValue;
        private static int _scanning;

        /// <summary>Minimum time between background disk scans.</summary>
        public static TimeSpan MinScanInterval = TimeSpan.FromSeconds(3);

        /// <summary>
        /// Optional Hearthstone install directory, tried first. The HDT plugin sets this from
        /// Config.Instance.HearthstoneDirectory; the standalone app leaves it null and the
        /// directory is discovered from the running process.
        /// </summary>
        public static string HearthstoneDirectoryHint { get; set; }

        /// <summary>Last known game server. Pure cache read — never touches the disk.</summary>
        public static bool TryGetCached(out string addr, out ushort port)
        {
            lock (Sync)
            {
                addr = _addr;
                port = _port;
                return addr != null && port != 0;
            }
        }

        /// <summary>
        /// Starts a background rescan if the cache is older than <see cref="MinScanInterval"/>.
        /// Returns immediately; safe to call from a UI thread on a timer.
        /// </summary>
        public static void BeginRefresh()
        {
            lock (Sync)
            {
                if (DateTime.UtcNow - _lastScanUtc < MinScanInterval)
                    return;
            }
            if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0)
                return;

            Task.Run(() =>
            {
                try
                {
                    string addr;
                    ushort port;
                    Scan(out addr, out port);
                }
                catch { /* locating the server is best effort */ }
                finally { Interlocked.Exchange(ref _scanning, 0); }
            });
        }

        /// <summary>
        /// Reads the log now and updates the cache. Blocking, but only reads the tail of one file.
        /// </summary>
        public static bool Scan(out string addr, out ushort port)
        {
            addr = null;
            port = 0;
            try
            {
                foreach (var logFile in FindNetworkLogs())
                {
                    if (TryParse(logFile, out addr, out port))
                    {
                        lock (Sync)
                        {
                            _addr = addr;
                            _port = port;
                            _lastScanUtc = DateTime.UtcNow;
                        }

                        // Give Windows a local answer for the reverse lookup Hearthstone does on
                        // connect, or the client freezes for ~15s before it rejoins the match.
                        // Always runs on a background thread via BeginRefresh.
                        ReverseDnsPrimer.EnsurePrimed(addr);
                        return true;
                    }
                }
            }
            catch
            {
                // fall through — caller falls back to port-based detection
            }

            lock (Sync)
            {
                _lastScanUtc = DateTime.UtcNow;
            }
            return false;
        }

        private static bool TryParse(string logFile, out string addr, out ushort port)
        {
            addr = null;
            port = 0;

            string text;
            try
            {
                text = ReadTail(logFile);
            }
            catch
            {
                return false;
            }

            // Scan backwards: the last GotoGameServe line is the current match.
            var lines = text.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var m = GotoGameServer.Match(lines[i]);
                if (!m.Success)
                    continue;
                ushort parsed;
                if (!ushort.TryParse(m.Groups[2].Value, out parsed) || parsed == 0)
                    continue;
                addr = m.Groups[1].Value;
                port = parsed;
                return true;
            }
            return false;
        }

        /// <summary>Reads the last <see cref="TailBytes"/> bytes; Hearthstone keeps the file open.</summary>
        private static string ReadTail(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - TailBytes);
                fs.Seek(start, SeekOrigin.Begin);

                var buffer = new byte[fs.Length - start];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = fs.Read(buffer, read, buffer.Length - read);
                    if (n <= 0)
                        break;
                    read += n;
                }
                return Encoding.UTF8.GetString(buffer, 0, read);
            }
        }

        /// <summary>Candidate log files, most specific first.</summary>
        private static string[] FindNetworkLogs()
        {
            var hsDir = FindHearthstoneDirectory();
            if (string.IsNullOrEmpty(hsDir))
                return new string[0];

            var logsDir = Path.Combine(hsDir, "Logs");
            if (!Directory.Exists(logsDir))
                return new string[0];

            // Modern layout: Logs\Hearthstone_<timestamp>\{GameNetLogger,Hearthstone}.log
            var newestSession = new DirectoryInfo(logsDir)
                .GetDirectories("Hearthstone_*")
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .FirstOrDefault();

            if (newestSession != null)
            {
                return new[] { "GameNetLogger.log", "Hearthstone.log" }
                    .Select(f => Path.Combine(newestSession.FullName, f))
                    .Where(File.Exists)
                    .ToArray();
            }

            // Older layout: Logs\Hearthstone.log
            var direct = Path.Combine(logsDir, "Hearthstone.log");
            return File.Exists(direct) ? new[] { direct } : new string[0];
        }

        private static string FindHearthstoneDirectory()
        {
            var hint = HearthstoneDirectoryHint;
            if (!string.IsNullOrEmpty(hint) && Directory.Exists(hint))
                return hint;

            // The running process is the only source that is always right — the uninstall
            // registry key still points at the old location after the game is moved.
            var fromProcess = FindDirectoryFromProcess();
            if (fromProcess != null)
                return fromProcess;

            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var key = baseKey.OpenSubKey(
                               @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Hearthstone"))
                    {
                        var path = key?.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                            return path;
                    }
                }
                catch
                {
                    // registry unreadable — try the next view
                }
            }

            return null;
        }

        private static string FindDirectoryFromProcess()
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(Reconnect.HsProcessName))
            {
                using (p)
                {
                    // QueryFullProcessImageName instead of Process.MainModule: MainModule throws
                    // across bitness boundaries, this call does not.
                    IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                    if (handle == IntPtr.Zero)
                        continue;
                    try
                    {
                        var sb = new StringBuilder(1024);
                        int size = sb.Capacity;
                        if (QueryFullProcessImageName(handle, 0, sb, ref size))
                        {
                            var dir = Path.GetDirectoryName(sb.ToString());
                            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                                return dir;
                        }
                    }
                    finally
                    {
                        CloseHandle(handle);
                    }
                }
            }
            return null;
        }

        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags,
            StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
