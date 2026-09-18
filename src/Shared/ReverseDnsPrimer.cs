using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ReconnectorCore
{
    /// <summary>
    /// Removes the ~15 second stall that Hearthstone incurs every time it connects to a game
    /// server, by giving Windows a local answer for the reverse-DNS lookup the client performs.
    ///
    /// What happens without this: on both a fresh match join and a reconnect, Hearthstone logs
    ///     TcpConnection - possible ip address: 5.42.176.253
    /// and then blocks its own network pump until the lookup finishes:
    ///     W Network.ProcessNetwork not called for 15s 600ms
    /// It is resolving the server IP back to a name (Dns.GetHostEntry on an IP literal does a
    /// PTR lookup). Blizzard's game-server ranges have no reverse records, and typical resolvers
    /// never answer the query at all, so the client waits out the full Windows resolver timeout -
    /// measured at 15.6s, matching the client's own stall to within 30ms. Windows does not
    /// negatively cache a timed-out query, so every single connect pays it again.
    ///
    /// The fix is to make the query never reach DNS. The Windows DNS client answers reverse
    /// lookups straight from the hosts file, so a stub entry per address resolves instantly.
    /// Measured on a primed range: 0.08s. Unprimed, same session: 15.7s.
    ///
    /// Entries are added a /22 at a time. A /24 is too narrow: match servers are drawn from a
    /// wider block, and one account's logs showed servers spread across 5.42.176.x AND
    /// 5.42.177.x, so seeding only the /24 that was seen left the neighbouring range slow.
    /// Priming the containing /22 also covers addresses not yet seen, which matters because the
    /// first connect to a range happens before this code can react to it.
    ///
    /// Everything lives in one marked block; deleting the block reverts the change completely.
    /// Requires elevation, which both front-ends already have.
    /// </summary>
    public static class ReverseDnsPrimer
    {
        private const string BeginMarker = "# BEGIN HsReconnector - reverse-DNS stubs";
        private const string EndMarker = "# END HsReconnector";
        private const string RangeMarker = "# range ";

        /// <summary>Bits of network prefix per primed range. 22 => 1024 addresses.</summary>
        private const int PrefixBits = 22;

        /// <summary>Number of /24s covered by one range (4 for a /22).</summary>
        private const int BlocksPerRange = 1 << (24 - PrefixBits);

        /// <summary>How many ranges to keep. Oldest is dropped first; 4 ranges is ~4096 lines.</summary>
        private const int MaxRanges = 4;

        private static readonly object Sync = new object();
        private static List<string> _primed;   // "5.42.176" third-octet bases, oldest first
        private static bool _disabled;

        /// <summary>Set when the hosts file could not be written (no admin, antivirus, ...).</summary>
        public static string LastError { get; private set; }

        /// <summary>The range most recently confirmed present, for status text.</summary>
        public static string LastPrimedRange { get; private set; }

        /// <summary>
        /// Built from %SystemRoot% rather than SpecialFolder.System, which resolves differently
        /// for a 32-bit process (HDT hosts the plugin). System32\drivers\etc is exempt from WOW64
        /// file redirection, so this literal path is correct from either bitness.
        /// </summary>
        public static string HostsPath =>
            Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows",
                @"System32\drivers\etc\hosts");

        /// <summary>
        /// Ensures the range containing <paramref name="gameServerIp"/> has stub entries.
        /// Cheap and safe to call repeatedly - after the first call it is a list lookup.
        /// Returns true only when the hosts file was actually rewritten.
        /// </summary>
        public static bool EnsurePrimed(string gameServerIp)
        {
            var baseAddr = RangeBaseOf(gameServerIp);
            if (baseAddr == null)
                return false;

            lock (Sync)
            {
                if (_disabled)
                    return false;

                try
                {
                    if (_primed == null)
                        _primed = ReadPrimedRanges();

                    if (_primed.Contains(baseAddr))
                    {
                        // Already covered, possibly by an earlier run or prime-dns.ps1.
                        LastPrimedRange = baseAddr + ".0/" + PrefixBits;
                        return false;
                    }

                    _primed.Add(baseAddr);
                    while (_primed.Count > MaxRanges)
                        _primed.RemoveAt(0);

                    Write(_primed);
                }
                catch (Exception ex)
                {
                    // Never let this break a reconnect - the tool still works, just slower.
                    LastError = ex.Message;
                    _primed = null;
                    // Permission problems will not fix themselves; stop retrying every scan.
                    if (ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                        _disabled = true;
                    return false;
                }
            }

            FlushDnsCache();
            LastError = null;
            LastPrimedRange = baseAddr + ".0/" + PrefixBits;
            return true;
        }

        /// <summary>
        /// "5.42.177.156" -> "5.42.176" (the base of its /22).
        /// Null if not a plain IPv4 literal.
        /// </summary>
        private static string RangeBaseOf(string ip)
        {
            if (string.IsNullOrEmpty(ip))
                return null;
            var parts = ip.Split('.');
            if (parts.Length != 4)
                return null;

            var octets = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                if (!byte.TryParse(parts[i], out octets[i]))
                    return null;
            }

            if (!IsPublicUnicast(octets))
                return null;

            int third = octets[2] & (0xFF - (BlocksPerRange - 1));
            return octets[0] + "." + octets[1] + "." + third;
        }

        /// <summary>
        /// Only public unicast ranges are primed. The address comes from a log file in the
        /// Hearthstone folder, which ordinary users can write to, so a forged log line must not be
        /// able to steer this elevated code into writing entries for loopback, LAN or other
        /// special-purpose ranges. Blizzard's game servers are always public addresses.
        /// </summary>
        private static bool IsPublicUnicast(byte[] o)
        {
            if (o[0] == 0 || o[0] == 10 || o[0] == 127 || o[0] >= 224)
                return false;                                   // this-network, private, loopback, multicast/reserved
            if (o[0] == 100 && (o[1] & 0xC0) == 64)
                return false;                                   // 100.64.0.0/10 carrier-grade NAT
            if (o[0] == 169 && o[1] == 254)
                return false;                                   // link-local
            if (o[0] == 172 && (o[1] & 0xF0) == 16)
                return false;                                   // 172.16.0.0/12 private
            if (o[0] == 192 && o[1] == 168)
                return false;                                   // 192.168.0.0/16 private
            return true;
        }

        private static List<string> ReadPrimedRanges()
        {
            var ranges = new List<string>();
            if (!File.Exists(HostsPath))
                return ranges;

            bool inBlock = false;
            foreach (var line in File.ReadAllLines(HostsPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith(BeginMarker, StringComparison.OrdinalIgnoreCase))
                {
                    inBlock = true;
                }
                else if (trimmed.StartsWith(EndMarker, StringComparison.OrdinalIgnoreCase))
                {
                    inBlock = false;
                }
                else if (inBlock && trimmed.StartsWith(RangeMarker, StringComparison.OrdinalIgnoreCase))
                {
                    // "# range 5.42.176.0/22"
                    var value = trimmed.Substring(RangeMarker.Length).Trim();
                    int slash = value.IndexOf('/');
                    if (slash <= 0)
                        continue;

                    // Ranges written at a different width (an older /24 block) are treated as
                    // absent, so the next write replaces them with the current wider range.
                    int bits;
                    if (!int.TryParse(value.Substring(slash + 1), out bits) || bits != PrefixBits)
                        continue;

                    var baseAddr = RangeBaseOf(value.Substring(0, slash));
                    if (baseAddr != null && !ranges.Contains(baseAddr))
                        ranges.Add(baseAddr);
                }
            }
            return ranges;
        }

        private static void Write(List<string> bases)
        {
            var kept = new List<string>();
            bool inBlock = false;

            if (File.Exists(HostsPath))
            {
                // Keep one pristine copy of the file as it was before we ever touched it.
                var backup = HostsPath + ".hsreconnector.bak";
                if (!File.Exists(backup))
                    File.Copy(HostsPath, backup);

                foreach (var line in File.ReadAllLines(HostsPath))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith(BeginMarker, StringComparison.OrdinalIgnoreCase))
                    {
                        inBlock = true;
                        continue;
                    }
                    if (trimmed.StartsWith(EndMarker, StringComparison.OrdinalIgnoreCase))
                    {
                        inBlock = false;
                        continue;
                    }
                    if (!inBlock)
                        kept.Add(line);
                }
            }

            // Drop trailing blank lines so the block does not drift down the file on each rewrite.
            while (kept.Count > 0 && kept[kept.Count - 1].Trim().Length == 0)
                kept.RemoveAt(kept.Count - 1);

            var sb = new StringBuilder();
            foreach (var line in kept)
                sb.AppendLine(line);

            sb.AppendLine();
            sb.AppendLine(BeginMarker);
            sb.AppendLine("# Stub names for Hearthstone game-server ranges. Without these, Windows");
            sb.AppendLine("# never answers the reverse lookup the game does on connect and the client");
            sb.AppendLine("# freezes for ~15s every match join and every reconnect.");
            sb.AppendLine("# Delete this whole block to undo. Original file: hosts.hsreconnector.bak");
            foreach (var baseAddr in bases)
            {
                sb.AppendLine(RangeMarker + baseAddr + ".0/" + PrefixBits);

                var octets = baseAddr.Split('.');
                int third = int.Parse(octets[2]);
                for (int block = 0; block < BlocksPerRange; block++)
                {
                    for (int host = 0; host <= 255; host++)
                    {
                        var ip = octets[0] + "." + octets[1] + "." + (third + block) + "." + host;
                        sb.AppendLine(ip + "\ths-gs-" + ip.Replace('.', '-') + ".hsreconnector.invalid");
                    }
                }
            }
            sb.AppendLine(EndMarker);

            // hosts must stay plain ASCII/UTF-8 without a BOM.
            File.WriteAllText(HostsPath, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>
        /// Makes the new entries take effect immediately. Uses ipconfig rather than a P/Invoke to
        /// dnsapi - the plugin deliberately keeps its DllImport surface to kernel32 only.
        ///
        /// Started by full System32 path: a bare "ipconfig" is looked up in the host application's
        /// folder and the current directory before System32, and this runs elevated.
        /// </summary>
        private static void FlushDnsCache()
        {
            try
            {
                var ipconfig = Path.Combine(Environment.SystemDirectory, "ipconfig.exe");
                var psi = new ProcessStartInfo(ipconfig, "/flushdns")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p != null)
                        p.WaitForExit(5000);
                }
            }
            catch
            {
                // The DNS client also picks up hosts changes on its own; a failed flush is not fatal.
            }
        }
    }
}
