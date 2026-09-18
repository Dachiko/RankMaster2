# Running the Rank Master 2 server

How to build, configure, start and troubleshoot the server on the Windows PC that holds the photos.
`SERVER_SPEC.md` is the API contract; this is the operating manual.

Everything below was verified on the build box except where it says "Windows only" — those are the
steps only a Windows machine can prove.

---

## 1. What you are deploying

One server, two ways to run it. They are the same program; pick by how you want to see it.

| Host | What it is | Use it when |
|---|---|---|
| `RankMaster2.Tray.exe` | notification-area icon, no console window | normal use — this is the one to run |
| `RankMaster2.Server.exe` | plain console window, logs scrolling live | diagnosing something |

The tray hosts the server inside itself. Do not run both at once: the second one to start cannot
bind the port and will refuse.

**Requirements:** Windows 11 to run — no .NET install needed, the publish is self-contained.
The .NET 8 SDK only to build.

---

## 2. Build

From the repository folder:

```powershell
.\publish-tray.ps1      # -> C:\Utils\RankMaster v3\tray\   (tray + console host together)
.\publish-server.ps1    # -> C:\Utils\RankMaster v3\server\ (console host only)
```

Both write into **Rank Master 3's install folder**, not the frozen Rank Master 2 tree
(`C:\Utils\rank-master-2`). `.\publish-tray.ps1` is the one you want; it ships the console host
alongside.

Both scripts fail loudly if `libSkiaSharp.dll` is missing from the output. It is the native image
decoder and must sit next to the exe — the same arrangement as `libvlc\` for the desktop app.

---

## 3. Configure — the one step that is not optional

**The server listens on `127.0.0.1` until you say otherwise, and no phone can reach that.**

This is deliberate. `SERVER_SPEC.md` § 2 forbids binding every interface: a bearer token is the only
thing between the network and a filesystem browser, so the address is something you state on purpose.
`0.0.0.0`, `::`, `*` and `+` are rejected at startup rather than quietly accepted.

Find the address of the adapter your phone shares a network with:

```powershell
Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.PrefixOrigin -ne 'WellKnown' } |
  Select-Object IPAddress, InterfaceAlias
```

If the PC is on both Wi-Fi and Ethernet, pick the one the phone is on. The server binds exactly one.

Then set it, by any of these three. Later ones win over earlier ones:

| Where | How | Good for |
|---|---|---|
| `appsettings.json` beside the exe | `{ "RankMaster2": { "ListenAddress": "192.168.1.42" } }` | a permanent install |
| Environment variable | `RankMaster2__ListenAddress=192.168.1.42` (two underscores) | a service or a scheduled task |
| Command line | `RankMaster2.Tray.exe --RankMaster2:ListenAddress=192.168.1.42` | trying something once |

The config file is read from the folder the **exe** lives in, not the folder you started it from, so a
Start Menu shortcut, the Startup folder and a scheduled task all honour it.

Everything else has a working default. The full list, if you ever need it:

| Key | Default | Meaning |
|---|---|---|
| `ListenAddress` | `127.0.0.1` | the single address bound. No wildcards |
| `Port` | `18611` | TLS port |
| `DataDirectory` | `%LOCALAPPDATA%\RankMaster2\Server` | certificate, devices, log |
| `SaveDelaySeconds` | `2` | how long a vote may sit in memory before `rankmaster_db.json` is written. **`0` saves on every vote, as before** |
| `MaxUnsavedChoices` | `5` | how many votes may be unsaved before a write is forced, whichever limit comes first |
| `PairingWindowSeconds` | `300` | how long a code lives. Clamped to 5 minutes, downwards only |
| `PairingWindowAttempts` | `5` | wrong guesses **per source address** before that address kills the window |
| `AutoOpenPairingWhenUnenrolled` | `true` | open a code at startup while no phone is paired. Only when no device list exists at all; a device list that cannot be read is moved aside and reported, never acted on |
| `TokenLifetimeDays` | unset | unset means device tokens never expire |

`RM2_DATA_DIR` also moves the data directory, for when you want one command and no config file. The
order is: `DataDirectory` in `appsettings.json`, then `RM2_DATA_DIR`, then the default above.

**About `SaveDelaySeconds` and `MaxUnsavedChoices`.** A vote counts the moment you press it; the
database is written a moment afterwards rather than during the press. Whichever limit comes first
triggers the write — two seconds, or five votes. Anything that moves a file (discard, special, undo
of a move), a rename, an explicit save, closing the folder and shutting the server down are **never**
deferred: those are on disk before they answer. So the most a crash or a power cut can cost is the
last few votes. If you would rather not lose even those, set `SaveDelaySeconds` to `0` — that is
exactly the old behaviour, a save before every vote is answered, and the cost is one fsync per vote,
which on a USB drive is what you feel.

**About `ListenAddress`.** On `127.0.0.1` the server is reachable from this PC only; a phone needs
the PC's LAN address here, and the tray icon says which one it is listening on. The default is
loopback deliberately — nothing is exposed until you say so — but a phone that cannot connect is
almost always this setting and not the Wi-Fi.

---

## 4. Let it through the firewall — Windows only

Windows will prompt on first run. Tick **Private networks**; that is enough. If the prompt was
dismissed, in an elevated PowerShell:

```powershell
New-NetFirewallRule -DisplayName "Rank Master 2" -Direction Inbound `
  -Protocol TCP -LocalPort 18611 -Profile Private -Action Allow
```

Private profile only, on purpose. If your Wi-Fi is marked Public, either change it to Private in
Windows settings or the phone will not get through — and marking a network Private is the correct fix
here, not adding a Public rule.

**Never forward port 18611 through your router.** The whole design assumes a LAN.

---

## 5. First run

Start `RankMaster2.Tray.exe`. On a fresh install it:

1. creates the data directory,
2. mints a self-signed certificate — this is the one the phone pins, and it is valid for the address
   you configured,
3. opens a pairing window automatically, because no phone is enrolled yet.

The icon's menu shows the address it is listening on and, once you open a folder from the phone,
which folder that is.

**Pair the phone:** double-click the icon, or menu → **Show pairing QR…**. The window shows a QR code
and the same credential as six digits. Scan it. The code lasts five minutes, works once, and dies
after five wrong guesses; **New code** replaces it.

That is the whole setup. From here the phone opens folders, ranks, and the PC screen can stay off.

---

## 6. Check it works without a phone

`rm2ctl` is the headless client and needs the SDK, so run it from the repository:

```powershell
dotnet run --project src\rm2ctl -- ping
dotnet run --project src\rm2ctl -- pair --take --name "test"      # prints a device token
$env:RM2_TOKEN = "rm2_…"
dotnet run --project src\rm2ctl -- cycle --folder "D:\Photos\Trip"
```

With no `--pin`, `rm2ctl` accepts the self-signed certificate and prints its fingerprint. Pass
`--pin sha256:…` to make it behave like the phone and refuse anything else. `pair --take` needs a
pairing window to be open — the tray's **Show pairing QR…** opens one.

`cycle` drives the entire ranking cycle — open, fetch, vote, skip, discard, special, undo, save,
close — and then every refusal the contract specifies, checking each answer. It exits non-zero if
anything disagrees. If `cycle` passes against a real folder, the server is working; anything left is
between the phone and the network.

Point `--base https://192.168.1.42:18611` at it when testing across the LAN rather than on the PC.

---

## 7. Where everything lives

**Data directory** — `%LOCALAPPDATA%\RankMaster2\Server` (on anything other than Windows:
`$XDG_DATA_HOME/RankMaster2/Server`, else `~/.local/share/RankMaster2/Server`). One spelling, capital
`S`, everywhere:

| File | What it is | If you delete it |
|---|---|---|
| `certificate.pfx` | the identity the phone pins | a new one is minted; **every phone must re-pair** |
| `devices.json` | issued device tokens | every phone must re-pair |
| `pairing.json` | the current offer, owner-readable only | harmless |
| `pair.request` | sentinel asking for a pairing window | harmless |
| `cache\` | re-encoded stills, bounded and evicted by age | harmless; it refills |
| `devices.json.corrupt-<timestamp>` | a device list that could not be read, moved aside rather than emptied | harmless once you have re-paired; keep it if you want to know what happened |
| `logs\server-YYYY-MM-DD.log` | the tray's log, seven days kept | harmless |

The console host logs to its window instead; only the tray writes the file.

The certificate is minted once, naming the address configured at the time. Changing `ListenAddress`
later does **not** mint a new one, and nothing breaks: every client here pins the fingerprint and
never looks at the name. Delete `certificate.pfx` only if you want one that names the new address —
and expect to re-pair, because the fingerprint changes with it.

**Your media folders** are untouched except for these, exactly as the desktop app leaves them:

| Name | What it is |
|---|---|
| `rankmaster_db.json` | the ratings. Same v1 format as Rank Master 1 |
| `discarded\`, `special 1\` | where discard and special move files |
| `.rankmaster.lock` | held while a session is open on that folder |

---

## 8. Updating

Republish over the same output folder. The data directory is somewhere else entirely, so the
certificate and the paired phone survive an update — you do not re-pair to install a new build.

Bump the version in `Directory.Build.props` in the same change, per the table in `README.md`.

---

## 9. Starting it with Windows — optional

Put a shortcut to `RankMaster2.Tray.exe` in the Startup folder: `Win`+`R`, `shell:startup`, drop the
shortcut in. Configuration beside the exe is still honoured from there.

A scheduled task at logon works too and survives more, but the Startup folder is enough for one PC.

---

## 10. Stopping

Tray → **Exit**, or `Ctrl+C` in the console window.

Exiting is always safe, including mid-session. Every response the server has already sent was written
to disk before it was sent (`SERVER_SPEC.md` § 13.1), so there is no unsaved work and no prompt. The
folder lock is released on the way out; the phone simply finds no session when it comes back and
opens the folder again.

---

## 11. One rule

**Do not run the desktop app and the server on the same folder at the same time.**

The lock only binds the server: a second server respects it, Rank Master 2 does not know about it and
will not be taught to — it is frozen, and you are keeping it (`SERVER_SPEC.md` § 16, gap 6). That is
also why the database format is frozen: both programs must go on reading and writing the same file.
Both writing it **at the same time** can corrupt it, and no API response can warn you. One or the
other, not both. Either one alone is completely safe.

---

## 12. When something is wrong

| Symptom | Cause | Fix |
|---|---|---|
| Phone cannot reach the server at all | still listening on `127.0.0.1` | § 3. Check the icon's menu — it shows the bound address |
| …and the address is right | firewall, or the network is marked Public | § 4 |
| …and both are right | phone is on guest Wi-Fi, or the PC is on a different adapter | put both on the same network |
| Tray says "already running" | a server is already up | use the existing icon; there is only one |
| Startup fails naming the port | something else holds 18611 | close the other server, or set `Port` |
| Phone reports a certificate mismatch | the data directory was deleted or moved, so the certificate is new | re-pair from the tray |
| Everything answers 401 | token revoked, or `devices.json` gone | re-pair |
| Pairing code refused | expired (5 min), already used, or five wrong guesses destroyed the window | **New code** |
| A folder answers 423 | a session is open on it, here or in another server | close the session from the phone, or exit the server |
| Handshake drops instantly on Windows | a build older than `c56bd64`, whose certificate carried an RSA-only key-usage bit that Windows refuses | rebuild from current `master` |
| Nothing to go on | `logs\server-*.log` in the data directory | it records the bound address and the certificate fingerprint at every start |

---

## 13. What the token buys

Worth knowing before you hand a phone a code: an enrolled device can **browse your whole
filesystem** — folder names and media counts, anywhere the Windows account can read. That is a
deliberate product decision (`SERVER_SPEC.md` § 10.15) so you can rank a folder anywhere without a
configured library root.

What it cannot do is read bytes outside the open session folder: `/media/*` serves only files that
are records of the currently open folder, and that boundary is enforced against the real file the
OS opens, links followed.

So: pair phones you own, on networks you trust, and revoke from the phone's settings if one is lost.
