# Connecting a second machine

Written 2026-09-10 for the first two-PC test. Everything before this was two game
processes on one PC. **This page is for the person — or the Claude session — on the
OTHER machine**: how to get the code, build it, run it, and connect to the host.

The host is mitch's PC. Its details are in [The host](#the-host-this-pc). The rest of
this page is about your end.

> **Read this first if you are a Claude session.** The launcher is a WinForms window with
> no command-line mode. Either the human at the keyboard clicks through
> [Connecting](#connecting), or you drive the window with computer-use. Everything before
> and after that step — checking versions, building, reading logs — is shell work and is
> written as commands you can run.

---

## What you need on your machine

| | Requirement | How to check |
|---|---|---|
| OS | Windows 10/11, x64 | — |
| Game | **Distant Worlds 2 on Steam, build 1.3.6.3** | `(Get-Item "$env:ProgramFiles (x86)\Steam\steamapps\common\Distant Worlds 2\DistantWorlds2.exe").VersionInfo.FileVersion` — but see the note below the table |
| .NET SDK | **9.0 or later** | `dotnet --list-sdks` |
| git | any | `git --version` |
| GitHub access | **none needed** — the repo is public (since 2026-09-10; it was private when this page was written) | `git clone` works without signing in |
| RAM | ~4 GB free for the game | a small galaxy costs ~3.5 GB; the 50-star test galaxy peaked at 8.7 GB |
| Network | same LAN as the host, **or** Tailscale on both ends | see [Reaching the host](#reaching-the-host) |

**The game version must match exactly.** The handshake compares it and logs
`*** GAME VERSION MISMATCH ***` and stops if it does not. State transfer is DW2's own
serialised galaxy, so different builds exchange bytes that deserialise into nonsense.
The host is on **1.3.6.3**. If yours differs, one of you updates through Steam; there is
no workaround.

**The game must be installed before you build.** The mod compiles against
`DistantWorlds.Types.dll` and `DistantWorlds.Core.dll` from the game folder. If Steam put
DW2 somewhere other than `C:\Program Files (x86)\Steam\steamapps\common\Distant Worlds 2`,
set `DW2_PATH` to the install folder before building:

```powershell
$env:DW2_PATH = 'D:\SteamLibrary\steamapps\common\Distant Worlds 2'
```

**You do not need a save file, and you do not need to install anything into the game
folder.** The mod is loaded by path with `--low-level-inject`; nothing is copied into
Program Files, and the host sends you its galaxy on join.

---

## Get the code

```powershell
git clone https://github.com/mitchb6-lab/dw2-inspect.git
cd dw2-inspect
git rev-parse HEAD
```

**Both machines must build the SAME COMMIT.** Send the host that hash and compare. The
handshake checks the game version and a protocol number, but the delta format
underneath has its own version that the handshake does not carry — two builds from
different commits can shake hands and then fail to read each other's deltas. Same
commit removes the question.

## Build

Two projects. Build the mod first; the launcher finds it by relative path afterwards.

```powershell
dotnet build src/Dw2Mp/Dw2Mp.csproj -c Debug
dotnet build src/Dw2MpLobby/Dw2MpLobby.csproj -c Debug
```

Both should end in `0 Error(s)`. What you should now have:

```
src\Dw2Mp\bin\Debug\Dw2Mp.dll                                  the mod
src\Dw2MpLobby\bin\Debug\net9.0-windows\Dw2MpLobby.exe         the launcher
```

The launcher looks for `Dw2Mp.dll` beside itself first, then walks up to
`src\Dw2Mp\bin\Debug\` — so **running it from the source tree needs no copying**.

If the mod build fails with `could not resolve DistantWorlds.Types`, the game was not
found: set `DW2_PATH` as above and build again.

If it fails with `MSB3027 ... file is locked`, a `DistantWorlds2.exe` from an earlier run
is still alive and holding the DLL:

```powershell
Get-Process DistantWorlds2 -ErrorAction SilentlyContinue | Stop-Process -Force
```

## Run

```powershell
src\Dw2MpLobby\bin\Debug\net9.0-windows\Dw2MpLobby.exe
```

The window should report finding the game and loading **23 races**. If instead it says
*"Could not find Distant Worlds 2"*, pass the install folder as the first argument or set
`DW2_PATH`.

## Connecting

**The lobby comes first and the games launch together.** *(Corrected 2026-09-10, at the
first live attempt — the previous text said to wait until the host was in-game. That was
the old flow; the launcher now owns the connection.)* The host opens a lobby, you join it,
both empires appear in the player list, and the **host** presses Start — which launches
**both** games at once. You do not launch anything yourself.

1. **Role → Join.**
2. **Host address** — paste what the host sent you (see [Reaching the host](#reaching-the-host)).
   **Port** stays `47800`.
3. Click **Test** (beside the address). The result appears in the log panel within a few seconds:
   - `Connected to …:47800 — the host is listening.` — go on.
   - `No answer from …` or `Could not connect … ConnectionRefused` — the host is not in-game yet, the address is wrong, or the host's
     firewall is blocking the launcher (see [The host](#the-host-this-pc)). Do not launch;
     a four-minute game load to discover a typo is the thing this button exists to prevent.
4. Configure your empire — name, race, government, colour — **before** the next step; it is
   sent when you join the lobby.
5. Click **Open lobby.** The log panel should show `Connected to <host>:47800. Sending your
   empire...` and then `Session received: 2 player(s), ...`. The **Lobby** tab lists both
   empires. Tell the host you are in.
6. **Wait.** When the host presses **Start session**, your log shows `Host started the
   session.` then `Launched. The game connects back on 127.0.0.1:<port>.` and DW2 starts by
   itself. Expect **2–4 minutes** to load: your game generates a throwaway galaxy for a game
   context, connects through the launcher, receives the host's galaxy (~4 MB compressed),
   and adopts it. After that your world moves only when the host's deltas say so.

**Leave the launcher window open for the whole session.** It is the transport: your game
talks only to it on loopback, and it talks to the host. Closing it ends the session.

## Did it work?

Both ends write to `%LOCALAPPDATA%\Dw2Mp\`. Yours is `determinism-lobbyClient.log`.

```powershell
Get-Content "$env:LOCALAPPDATA\Dw2Mp\determinism-lobbyClient.log" | Select-String -NotMatch '^# watchdog' | Select-Object -Last 40
```

Three lines, in this order, mean it worked:

```
# net: handshake OK — protocol and game version match
# net[client]: APPLIED sync #1  ...B  read=...ms  apply=...ms  ships=... empires=...
# net[client]: delta #51 applied — ...  [totals: ...]
```

- **`handshake OK`** — the builds agree. A `MISMATCH` line here is the failure you want,
  because it stops before corrupting anything.
- **`APPLIED sync #1`** — the host's galaxy is now running on your machine. This is the join.
- **`delta … applied`** — the steady state. The first two without this means connected and
  frozen.

After that, the number to watch is the `[totals: …]` on every 50th delta — cumulative
counts by kind. Prefer it to the per-delta line, because the log samples every 50th delta
and research only runs on every 5th, so the sampler never lands on a research delta.

**What to send back to the host** after a run, whether it worked or not:

```powershell
Compress-Archive -Force -Path "$env:LOCALAPPDATA\Dw2Mp\*.log", "$env:LOCALAPPDATA\Dw2Mp\session.json" -DestinationPath "$env:USERPROFILE\Desktop\dw2mp-client-logs.zip"
```

Plus: `git rev-parse HEAD`, your DW2 version, and how many files match
`data\Logs\*CrashDump*` in the game folder (should be **0**).

---

## Reaching the host

The transport is plain TCP to the host's launcher on port **47800**. The address depends on
where you are.

**Same LAN as the host** — use the host's LAN address, `192.168.1.166` at the time of
writing. The host's lobby lists its current addresses under **Your address** with a Copy
button; ask for the one labelled LAN.

**Anywhere else — use Tailscale.** The host's internet is **Starlink, which is behind
carrier-grade NAT: port forwarding is not possible, so a direct connection over the
internet cannot work.** A virtual LAN is the only path, and neither end needs to change
anything else: the joiner types the host's Tailscale address (`100.x.y.z`) instead of the
LAN one. Both install Tailscale, sign in to the same account or share the node, and the
host reads the Tailscale entry from **Your address** — it is detected by adapter name and
sorted above the LAN entry. *Tailscale is not yet installed on the host as of 2026-09-10.*

**Do not run a full-tunnel VPN (NordVPN and similar) on either end during a session.** It
captures the route and the connection fails in a way that looks like the host is not
listening.

---

## The host (this PC)

For mitch, or a session on the host machine. What the host has to have right, in the order
it will bite:

1. **The firewall does not cover the launcher.** *(Found 2026-09-10.) **Resolved the same day,
   the easy way:** the first **Open lobby** raised the Windows Security Alert and allowing it
   created two inbound Allow rules for `dw2mplobby.exe` (Public profile). The manual rule
   below is only needed if that prompt was dismissed.* The inbound Allow
   rules on this PC are for `DistantWorlds2.exe`. Since the launcher took over the
   transport, **the listener on 47800 is in `Dw2MpLobby.exe`**, which has no rule — and the
   active network profile is **Public**, where Windows blocks unsolicited inbound to an
   unlisted program. Loopback testing never saw this because loopback is not filtered. Add
   a rule once, from an elevated PowerShell (this is a security setting, so it is done by a
   person, not by a Claude session):

   ```powershell
   New-NetFirewallRule -DisplayName "Dw2Mp lobby (TCP 47800)" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 47800 -Program "C:\Users\mitch\OneDrive\Documents\dw2-inspect\src\Dw2MpLobby\bin\Debug\net9.0-windows\Dw2MpLobby.exe" -Profile Any
   ```

   Verify: `Get-NetFirewallRule -DisplayName "Dw2Mp lobby*" | Select-Object Enabled, Action`.
   The joiner's **Test** button is the end-to-end check.

2. **NordVPN off.** On 2026-09-10 the NordLynx adapter held an address (`10.5.0.2`), which
   means it was connected. Disconnect it for the session.

3. **Same commit as the joiner.** `git rev-parse HEAD` on both, compared.

4. **Open the lobby first, launch last.** Role → Host, empire, mode, galaxy size, then
   **Open lobby** — the log says `Lobby open on port 47800. Waiting for a player to join...`
   and the listener is up from this moment (the joiner's Test button answers now). When
   the joiner appears in the player list, **Start session** becomes enabled; pressing it
   launches both games. *(Corrected 2026-09-10: this said "Host and launch, wait until
   in-game, then say go" — the old flow.)*

5. **The host's log** is `%LOCALAPPDATA%\Dw2Mp\determinism-lobbyHost.log`. The lines that
   say the joiner arrived:

   ```
   # net: handshake OK — protocol and game version match
   # net[host]: client requested a resync
   # net[host]: full state #1 tick=... (client asked) raw=...B packed=...B
   # net[host]: delta #1 tick=30 ...
   ```

---

## What this test is for

Everything measured so far is loopback. Loopback cannot show **latency, jitter, MTU, or a
real TCP stack**, and a delta stream is more sensitive to all four than the old
bulk-transfer design was. The specific things to look for on the first real run, in the
joiner's log:

- **`holding delta for full state #N`** — a delta arrived before the full state it was
  built against. Expected at join (198 held on loopback, then released); a steady count
  that grows without `released` after it means the full state is not landing.
- **`dropped delta for superseded full state`** — should stay at 0 or single digits.
- **Time between `APPLIED sync #1` and the first `delta … applied`** — on loopback this is
  under a second. Over a real link it is the first real latency number.
- **`# delta: MUTATION FAILED`** lines, or a non-zero `mutation failures=` in the `[…]` bracket — should be 0; the last
  three loopback runs were 0.
- **The `absent` count** in the delta lines — 10 of 53 sampled deltas still show 1–20
  absent updates on loopback, unattributed. A real link changes ordering, so this number
  may move in either direction and is worth recording.

If it all works, the next thing is to **play** — give orders on both sides and watch them
land — because none of the automated runs had anybody at the keyboard.
