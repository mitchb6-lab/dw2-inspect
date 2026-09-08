# Joining a Distant Worlds 2 multiplayer session

How a second player connects to a session hosted on **this PC**.

> ### Status: 2026-09-08 — works on a LAN, with caveats
>
> | | |
> |---|---|
> | Two instances on one PC | **Working** (verified) |
> | Two PCs on a LAN | **Should work — not yet tested** |
> | Over the internet | Needs port forwarding today; Steam sockets planned |
> | Joiner's empire choice reaching the host | **Not yet** — see [Limitations](#limitations) |
>
> The networking is proven: state syncs host→client and commands relay client→host between
> two live games. What is *not* finished is the lobby handing the joiner's empire to the
> host, so right now both players end up in the host's galaxy as configured by the host.

---

## What the other person needs

| Item | Where from | Notes |
|---|---|---|
| **Distant Worlds 2** | Steam | Must be the **same build**. This PC runs **1.3.6.3**. |
| **`Dw2MpLobby.exe`** | You send it | The launcher — it finds their game and starts it |
| **`Dw2Mp.dll`** | You send it | The mod. Put it **beside** the lobby exe |
| **A save file** | You send it | Temporary — see [Limitations](#limitations) |

They do **not** need .NET installed: DW2 ships its own .NET 8 runtime and the mod loads
into it.

### Where to get the files on this PC

```
Lobby : C:\Users\mitch\OneDrive\Documents\dw2-inspect\src\Dw2MpLobby\bin\Debug\net9.0-windows\
Mod   : C:\Users\mitch\OneDrive\Documents\dw2-inspect\src\Dw2Mp\bin\Debug\Dw2Mp.dll
Saves : C:\Program Files (x86)\Steam\steamapps\common\Distant Worlds 2\data\SavedGames\
```

Send them the whole `net9.0-windows` folder with `Dw2Mp.dll` copied into it. The lobby
looks for the mod beside itself first.

---

## Your network details

| | |
|---|---|
| **This PC's LAN address** | `192.168.1.166` |
| **Port** | `47800` (TCP) |

Re-check the address before a session — it can change:

```powershell
Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' }
```

**Firewall is already handled.** Windows has inbound Allow rules for
`DistantWorlds2.exe` (TCP, any local port, all profiles). The listener runs *inside* that
process, so port 47800 is already permitted — nothing to configure.

---

## Step by step — you (host)

1. **Pick the save.** Both players load the same one for now. `MPTestSmall.DWGame` (21 MB)
   is the light one; a late-game save works but costs ~15 GB of RAM per instance instead
   of ~3.5 GB.
2. **Send the joiner** the lobby folder, `Dw2Mp.dll`, and that save file. They drop the
   save into their own `data\SavedGames\`.
3. **Run `Dw2MpLobby.exe`.**
4. Set **Role → Host a session**.
5. Configure your empire — name, race, government, colour.
6. Choose **Mode**: *Co-op — shared empire* (both drive one empire) or *Competitive*.
7. Leave **Port** at `47800`.
8. Click **Host and launch**. DW2 starts; wait until you are actually in the galaxy.
9. **Tell the joiner to connect only once you are in-game.** The host does not listen until
   it has loaded.

## Step by step — them (joining)

1. Copy `Dw2Mp.dll` next to `Dw2MpLobby.exe`, and the save into their
   `...\Distant Worlds 2\data\SavedGames\`.
2. **Run `Dw2MpLobby.exe`.** It should report finding their game and loading 23 races.
3. Set **Role → Join a session**.
4. Enter **Host address** `192.168.1.166` and **Port** `47800`.
5. Configure their empire (see [Limitations](#limitations) — this does not reach you yet).
6. Click **Join and launch**.
7. Their game loads, connects, and begins receiving the galaxy.

## Confirming it worked

Both machines write a log to `%LOCALAPPDATA%\Dw2Mp\`. Open the newest `determinism-*.log`:

**Host should show**

```
# net[host]: listening on 47800, waiting for a client
# net[host]: client connected from 192.168.1.xxx:#####
# net: handshake OK — protocol and game version match
# net[host]: sync #1 tick=1200 raw=23,138,161B packed=4,207,851B ...
```

**Joiner should show**

```
# net[client]: connected
# net: handshake OK — protocol and game version match
# net[client]: APPLIED sync #1  23,138,167B  read=161ms  apply=42ms  ships=90 empires=6
```

`APPLIED` is the one that matters — it means a galaxy simulated on your PC is now running
on theirs.

---

## Troubleshooting

**"Could not find Distant Worlds 2"** — the lobby checked `DW2_PATH` and every Steam
library. Either pass the install folder as the first argument, or set `DW2_PATH`.

**`*** GAME VERSION MISMATCH ***` in the log** — different DW2 builds. State transfer is
the game's own serialised galaxy, so mismatched builds exchange bytes that deserialise
into nonsense. Both must be on the same version; this PC is on 1.3.6.3.

**Joiner connects then nothing happens** — the host had not finished loading. It does not
listen until it is in-game. Reconnect after the host is in the galaxy.

**Connection refused** — check the host is actually in-game, the address is current, and
both machines are on the same network. If a **VPN is connected on either end it will
break LAN discovery** — NordVPN is installed on this PC (currently disconnected); turn it
off for a session.

**Client never finishes loading (both games on one PC)** — they starve each other on disk.
Start the host, wait until it is in the galaxy, *then* start the client. This was a real
failure: 450 seconds with zero simulation ticks.

**Out of memory with both games on one PC** — a late-game 40 MB save costs ~15 GB per
instance. Use a small save (~3.5 GB each). This PC has 31.4 GB.

**Host stutters every sync** — expected. Serialising and compressing the galaxy runs on the
simulation thread: ~150–200 ms on a small save, up to ~700 ms on a late-game one.

---

## Limitations

Things that will surprise you if you do not know them:

- **The joiner's empire choice does not reach the host.** The lobby collects it but does
  not yet send it. Both players currently end up in the host's galaxy as the host
  configured it. Slot exchange is the next piece of work.
- **Everyone needs the same save.** The host has to send its galaxy on connect instead —
  the mechanism exists, it just is not wired to the join flow yet. This is what will remove
  file-sharing from the process entirely.
- **Internet play needs port forwarding.** Port 47800 TCP to this PC. Steam sockets
  (`SteamNetworkingSockets`, already reachable in DW2's process) will remove that.
- **Competitive mode leaks information.** Every client receives the whole galaxy. The UI
  filters it correctly per empire, so it *plays* right, but the hidden data is present in
  memory and a determined player could read it. Fine among people who will not; not
  cheat-proof.
- **The host has authority.** If the host quits, the session ends. No reconnection.

---

## Changelog

- **2026-09-08** — First version. Loopback verified; LAN untested. Lobby, handshake, state
  sync and command relay all in place.
