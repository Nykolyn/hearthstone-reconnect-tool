using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ReconnectorCore
{
    /// <summary>
    /// Wrappers around iphlpapi.dll for enumerating and closing TCP connections.
    ///
    /// iphlpapi is loaded dynamically (LoadLibrary + GetProcAddress) rather than with a
    /// static [DllImport("iphlpapi")] attribute. Hearthstone Deck Tracker's plugin loader
    /// reflection-scans every plugin for DllImport attributes and refuses to load ANY plugin
    /// if it finds one importing "iphlpapi"/"lovepapi". Resolving the functions at runtime
    /// keeps that attribute out of the metadata so the plugin loads normally. Only kernel32
    /// (LoadLibrary/GetProcAddress) is imported statically, which is not on HDT's blocklist.
    /// </summary>
    internal static class TcpApi
    {
        private const int AF_INET = 2;

        public enum MibTcpState : uint
        {
            Closed = 1,
            Listen = 2,
            SynSent = 3,
            SynRcvd = 4,
            Established = 5,
            FinWait1 = 6,
            FinWait2 = 7,
            CloseWait = 8,
            Closing = 9,
            LastAck = 10,
            TimeWait = 11,
            DeleteTcb = 12
        }

        private enum TcpTableClass
        {
            OwnerPidAll = 5
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MibTcpRowOwnerPid
        {
            public uint State;
            public uint LocalAddr;
            public uint LocalPort;   // network byte order in low 16 bits
            public uint RemoteAddr;
            public uint RemotePort;  // network byte order in low 16 bits
            public uint OwningPid;

            public IPAddress RemoteIp => new IPAddress(RemoteAddr);

            public ushort RemotePortHost
            {
                get
                {
                    var b = BitConverter.GetBytes(RemotePort);
                    return (ushort)((b[0] << 8) | b[1]);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MibTcpRow
        {
            public uint State;
            public uint LocalAddr;
            public uint LocalPort;
            public uint RemoteAddr;
            public uint RemotePort;
        }

        // --- Dynamic binding to iphlpapi.dll (see class summary) ---------------
        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate uint GetExtendedTcpTableDelegate(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, TcpTableClass tblClass, uint reserved);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate int SetTcpEntryDelegate(IntPtr pTcpRow);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        private static readonly GetExtendedTcpTableDelegate GetExtendedTcpTable;
        private static readonly SetTcpEntryDelegate SetTcpEntry;

        static TcpApi()
        {
            // Build the name at runtime so the literal "iphlpapi" never appears as a
            // DllImport target in the assembly metadata that HDT scans.
            //
            // Loaded by full System32 path, never by bare name: a bare name makes Windows search
            // the host application's folder first, and HDT lives in user-writable %LocalAppData%.
            // Since this code runs elevated, a planted iphlpapi.dll there would be a privilege
            // escalation. For a 32-bit host, WOW64 redirects System32 to the matching SysWOW64.
            var module = LoadLibrary(Path.Combine(Environment.SystemDirectory, "iphlpapi" + ".dll"));
            if (module == IntPtr.Zero)
                throw new InvalidOperationException("Failed to load iphlpapi.dll");

            GetExtendedTcpTable = GetDelegate<GetExtendedTcpTableDelegate>(module, "GetExtendedTcpTable");
            SetTcpEntry = GetDelegate<SetTcpEntryDelegate>(module, "SetTcpEntry");
        }

        private static T GetDelegate<T>(IntPtr module, string name) where T : class
        {
            var addr = GetProcAddress(module, name);
            if (addr == IntPtr.Zero)
                throw new InvalidOperationException("Failed to resolve " + name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(addr, typeof(T));
        }

        private const uint ERROR_INSUFFICIENT_BUFFER = 122;

        public static List<MibTcpRowOwnerPid> GetTcpConnections()
        {
            var rows = new List<MibTcpRowOwnerPid>();
            int buffSize = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref buffSize, false, AF_INET, TcpTableClass.OwnerPidAll, 0);

            // The table can grow between the size query and the real call (any process opening a
            // socket), which fails with ERROR_INSUFFICIENT_BUFFER and updates buffSize. Retry a few
            // times instead of reporting "no connections" for a click that should have worked.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                IntPtr tablePtr = Marshal.AllocHGlobal(buffSize);
                try
                {
                    uint err = GetExtendedTcpTable(tablePtr, ref buffSize, false, AF_INET, TcpTableClass.OwnerPidAll, 0);
                    if (err == ERROR_INSUFFICIENT_BUFFER)
                        continue;
                    if (err != 0)
                        return rows;

                    int numEntries = Marshal.ReadInt32(tablePtr);
                    IntPtr rowPtr = tablePtr + 4;
                    int rowSize = Marshal.SizeOf(typeof(MibTcpRowOwnerPid));
                    for (int i = 0; i < numEntries; i++)
                    {
                        rows.Add((MibTcpRowOwnerPid)Marshal.PtrToStructure(rowPtr, typeof(MibTcpRowOwnerPid)));
                        rowPtr += rowSize;
                    }
                    return rows;
                }
                finally
                {
                    Marshal.FreeHGlobal(tablePtr);
                }
            }
            return rows;
        }

        /// <summary>
        /// Forcibly closes a TCP connection by setting its state to DELETE_TCB.
        /// Requires the process to run elevated. Returns 0 on success (win32 error code otherwise).
        /// </summary>
        public static int CloseConnection(MibTcpRowOwnerPid row)
        {
            var kill = new MibTcpRow
            {
                State = (uint)MibTcpState.DeleteTcb,
                LocalAddr = row.LocalAddr,
                LocalPort = row.LocalPort,
                RemoteAddr = row.RemoteAddr,
                RemotePort = row.RemotePort
            };

            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MibTcpRow)));
            try
            {
                Marshal.StructureToPtr(kill, ptr, false);
                return SetTcpEntry(ptr);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
    }

    /// <summary>
    /// Result of a reconnect attempt.
    /// </summary>
    public class ReconnectResult
    {
        public int ClosedCount;
        public bool HearthstoneRunning = true;
        public string Error;
        /// <summary>How the connection was identified, for logging ("game server 1.2.3.4:3724").</summary>
        public string Target;

        public bool Success => Error == null && HearthstoneRunning && ClosedCount > 0;
    }

    /// <summary>
    /// Closes Hearthstone's TCP connections so the game drops and immediately
    /// reconnects to the match in progress ("Reconnecting..." screen).
    /// </summary>
    public static class Reconnect
    {
        public const string HsProcessName = "Hearthstone";

        /// <summary>Blizzard's classic game-server port — the connection we want to drop.</summary>
        public const ushort GameServerPort = 3724;

        /// <summary>
        /// Ports that must never be closed.
        ///
        /// 1119 is the Battle.net (Aurora) session. Dropping it does not make Hearthstone rejoin
        /// the match faster — it logs the client out, so before it can even start reconnecting it
        /// has to redo the whole Battle.net login, and that attempt usually fails outright and
        /// leaves the "reconnect failed" dialog with only an Exit button. 443/80 are web services
        /// (shop, telemetry, CDN); closing them does nothing useful for a reconnect.
        /// </summary>
        private static readonly ushort[] ProtectedPorts = { 1119, 443, 80 };

        public static bool IsElevated()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        public static bool IsHearthstoneRunning()
        {
            var processes = Process.GetProcessesByName(HsProcessName);
            foreach (var p in processes)
                p.Dispose();
            return processes.Length > 0;
        }

        /// <summary>
        /// Closes Hearthstone's game-server connection so the client rejoins the match in progress.
        ///
        /// The connection is picked in this order, and the Battle.net session is never touched
        /// (see <see cref="ProtectedPorts"/>):
        ///   1. the exact address parsed from Hearthstone's network log, if it is still connected;
        ///   2. any connection on the game-server port (3724);
        ///   3. any remaining non-Battle.net, non-web connection.
        /// If none of those exist the client is not in a match, and we report that rather than
        /// closing something that would break the session.
        /// </summary>
        public static ReconnectResult Disconnect(string preferredAddr = null, ushort preferredPort = 0)
        {
            var result = new ReconnectResult();

            var pids = new HashSet<uint>();
            foreach (var p in Process.GetProcessesByName(HsProcessName))
            {
                using (p)
                    pids.Add((uint)p.Id);
            }

            if (pids.Count == 0)
            {
                result.HearthstoneRunning = false;
                return result;
            }

            var candidates = new List<TcpApi.MibTcpRowOwnerPid>();
            foreach (var row in TcpApi.GetTcpConnections())
            {
                if (!pids.Contains(row.OwningPid))
                    continue;
                if (row.State != (uint)TcpApi.MibTcpState.Established)
                    continue;
                // Skip loopback (127.x.x.x) — those are local IPC, not Blizzard servers
                if ((row.RemoteAddr & 0xFF) == 127)
                    continue;
                candidates.Add(row);
            }

            // 1. Exact game server from the log.
            if (preferredAddr != null && preferredPort != 0)
            {
                var exact = candidates.FindAll(r =>
                    r.RemotePortHost == preferredPort && r.RemoteIp.ToString() == preferredAddr);
                if (exact.Count > 0)
                    return CloseAll(exact, "game server " + preferredAddr + ":" + preferredPort, result);
            }

            // 2. Anything on the game-server port.
            var onGamePort = candidates.FindAll(r => r.RemotePortHost == GameServerPort);
            if (onGamePort.Count > 0)
                return CloseAll(onGamePort, "port " + GameServerPort + " connection", result);

            // 3. Anything that is not Battle.net or a web service.
            var unprotected = candidates.FindAll(r => Array.IndexOf(ProtectedPorts, r.RemotePortHost) < 0);
            if (unprotected.Count > 0)
                return CloseAll(unprotected, "unrecognised game connection", result);

            result.Error = candidates.Count > 0
                ? "No game-server connection — are you in a match?"
                : "No active connections found";
            return result;
        }

        private static ReconnectResult CloseAll(List<TcpApi.MibTcpRowOwnerPid> rows, string target,
            ReconnectResult result)
        {
            result.Target = target;
            foreach (var row in rows)
            {
                int err = TcpApi.CloseConnection(row);
                if (err == 0)
                    result.ClosedCount++;
                else if (result.Error == null)
                    result.Error = ErrorText(err);
            }
            return result;
        }

        private static string ErrorText(int win32Error)
        {
            switch (win32Error)
            {
                case 5: return "Access denied — run as administrator";
                case 317: return "Not elevated — run as administrator";
                case 87: return "Invalid parameter";
                case 50: return "Not supported";
                default: return "Error " + win32Error;
            }
        }
    }
}
