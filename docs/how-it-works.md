# How HS Reconnector works

This page covers the technical details: which connection the tool closes and why, where
Hearthstone's 15-second reconnect delay comes from, and how the app protects itself while running
as administrator.

- [The disconnect](#the-disconnect)
- [Never close the Battle.net connection (port 1119)](#never-close-the-battlenet-connection-port-1119)
- [The 15-second reconnect delay (reverse DNS)](#the-15-second-reconnect-delay-reverse-dns)
- [Running safely as administrator](#running-safely-as-administrator)
- [Source layout](#source-layout)

## The disconnect

1. Find the `Hearthstone.exe` process(es).
2. List all established IPv4 TCP connections they own (`GetExtendedTcpTable`).
3. Pick **only the game-server connection**, in this order:
   1. the exact address from the last `Network.GotoGameServe()` line in
      `Logs\Hearthstone_<timestamp>\GameNetLogger.log`, if that connection is still open;
   2. any connection on the game-server port **3724**;
   3. any remaining connection that is not Battle.net or a web service.

   If none of these exist, the client is not in a match and nothing is closed.
4. Close the chosen connection by setting its state to `DELETE_TCB` via `SetTcpEntry`. That call
   needs administrator rights.

The game shows *Reconnecting…* and rejoins the match in progress. Dropping the connection itself
takes about 30 ms.

The log line looks like this. The spelling is Blizzard's own: `GotoGameServe`, with no *r*:

```
I 22:38:26.8852240 Network.GotoGameServe() - address= 37.244.26.45:3724, game=7500, ...
```

The address lookup is cached and refreshed every two seconds in the background while Hearthstone
runs, so pressing the button costs no disk I/O. Reading the log on the click path would add its
latency straight onto the reconnect.

## Never close the Battle.net connection (port 1119)

Hearthstone keeps several sockets open at once:

| Port | What | Closed? |
|---|---|---|
| 3724 | Game server | **yes**, this is the one |
| 1119 | Battle.net (Aurora) session | never |
| 443 / 80 | Shop, telemetry, CDN | never |

Killing 1119 does **not** make the rejoin faster. It logs the client out, so Hearthstone has to
redo the entire Battle.net login before it can even begin rejoining the match. The reconnect
looks slow and then usually dies on *reconnect failed*, with nothing but an Exit button. Many
simple reconnect tools close every Hearthstone connection and run into exactly this. HS
Reconnector hard-excludes ports 1119, 443 and 80 in `Reconnect.Disconnect`.

## The 15-second reconnect delay (reverse DNS)

If a reconnect still takes about 15 seconds after the disconnect, the time is spent **inside
Hearthstone**, and the cause is DNS:

```
I 18:29:46.870  RpcController.OnSocketError - Receive,ConnectionReset      <- we closed it
I 18:29:46.902  Network.GotoGameServe() - address= 37.244.26.83 reconnecting=True
I 18:29:46.902  TcpConnection - possible ip address: 37.244.26.83
W 18:30:02.497  Network.ProcessNetwork not called for 15s 600ms            <- stalled here
I 18:30:02.561  Network.OnGameServerConnectEvent() - Connected  ERROR_OK
```

The client resolves the server IP back to a name and blocks its own network pump until the lookup
finishes. Blizzard's game-server ranges have no reverse-DNS records, and resolvers commonly never
answer the query at all, so the client waits out the full Windows resolver timeout. Measured on
one test machine:

| Query | Time |
|---|---|
| Forward lookup, `www.google.com` | 265 ms |
| PTR for `8.8.8.8` / `1.1.1.1` | 33 ms |
| PTR for `37.244.26.83` (game server) | **15,624 ms** |

15,624 ms matches the client's own logged stall of 15.6 s, so it is the same wait. Two details
make it worse than it looks. **Windows does not cache a timed-out lookup**, so every connect
pays the full timeout again. And the same stall hits normal match joins too:
`reconnecting=False` connects measured 15.7 s.

`ReverseDnsPrimer` fixes this by adding stub entries to the Windows hosts file. The DNS client
answers reverse lookups from the hosts file without sending any query. The app does this
automatically the first time it sees a new range and writes a single marked block:

```
# BEGIN HsReconnector - reverse-DNS stubs
# range 37.244.24.0/22
37.244.24.0	hs-gs-37-244-24-0.hsreconnector.invalid
...
# END HsReconnector
```

The names live under `.invalid`, a TLD reserved so it can never resolve on the internet
([RFC 2606](https://www.rfc-editor.org/rfc/rfc2606)), so they cannot redirect any real domain.
The block holds at most four ranges and drops the oldest first. Only public unicast addresses
are accepted, so a tampered log file cannot make the app write entries for loopback or LAN
ranges.

### Seed whole /22s, not /24s

The fix works all-or-nothing per range, which shows up as "sometimes instant, sometimes 15 s".
Measured across one week of logs, after seeding two /24s:

| Server range | Seeded? | Connect time |
|---|---|---|
| `37.244.26.x` | yes | 0.06 – 0.09 s |
| `5.42.177.x` | yes | 0.08 – 0.13 s |
| `5.42.176.x` | **no** | **15.6 – 16.8 s** |

Blizzard draws each match's server from a block wider than a /24. The same account saw servers
in both `5.42.176.x` *and* `5.42.177.x`, so seeding only a /24 left the neighboring range
stalling. Ranges are therefore seeded a **/22** at a time (1024 entries), which also covers
addresses not seen yet. That matters because the client resolves a new server's address before
the app can react to it, so only a range that is already covered is fast on the very first
connect.

To apply or undo the fix by hand, run PowerShell as administrator:

```powershell
powershell -ExecutionPolicy Bypass -File tools\prime-dns.ps1
powershell -ExecutionPolicy Bypass -File tools\prime-dns.ps1 -Ranges 24.105.28.0/22
powershell -ExecutionPolicy Bypass -File tools\prime-dns.ps1 -Remove
```

Antivirus products often block hosts-file writes. When that happens the app still reconnects
correctly, just slowly, and the status line says so.

## Running safely as administrator

The app has to run elevated, so it treats everything it loads and reads as potentially hostile:

- **DLL loading is locked to System32.** The first thing `Main` does is
  `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32)`. By default Windows looks for a DLL
  next to the exe first, and this exe often sits in *Downloads*. That would let any DLL dropped
  there run with administrator rights.
- **`iphlpapi.dll` and `ipconfig.exe` are loaded by full System32 path**, never by bare name.
- **Hearthstone's log is untrusted input.** The Hearthstone folder is writable by normal users,
  so the parsed address must match a strict IPv4 pattern, and only public ranges ever reach the
  hosts file.
- **No network access, no settings file, no auto-update.** There's nothing to intercept or
  replace.

This doesn't cover DLLs that Windows loads before `Main` runs, which is why the README suggests
keeping the exe in a folder only administrators can write to.

## Source layout

```
src/HsReconnector/Program.cs     Entry point, DLL-search hardening
src/HsReconnector/MainForm.cs    The window, Ctrl+F12 hotkey, status polling, cooldown
src/HsReconnector/app.manifest   requireAdministrator, DPI awareness
src/Shared/ReconnectCore.cs      TCP enumeration and the disconnect
src/Shared/GameServerLocator.cs  Finds the game server in Hearthstone's log
src/Shared/ReverseDnsPrimer.cs   Removes the 15 s reverse-DNS stall
tools/prime-dns.ps1              Apply or undo the hosts-file fix by hand
build.ps1                        Build, check, package
```

`src/Shared` has no UI or HDT dependencies. The same core also powers the
[Hearthstone Deck Tracker plugin](https://github.com/Nykolyn/hearthstone-reconnect-hdt-plugin).
Keep the two copies in sync.
