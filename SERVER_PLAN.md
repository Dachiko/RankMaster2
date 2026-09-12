# Rank Master 2 — headless server + mobile API

**Status: approved. Phase 0 complete — skeleton builds and all 45 tests pass on Linux.**

Goal: a mobile client on the local network can perform the entire ranking cycle against a
folder on the PC, with nothing shown on the PC screen. The WPF app is **frozen** until this
server is functional. Rename-by-rank is deliberately **not** exposed.

`SPEC.md` remains the source of truth for ranking behaviour. This document is the source of
truth for the server. Where they overlap (TrueSkill, pair selection, JSON persistence,
discard/special/undo semantics), `SPEC.md` wins and the server must not reimplement it.

---

## 1. What we already have

`Core`, `Ranking` and `Catalog` are plain `net8.0` with no WPF and no UI assumptions. They
hold all of the ranking behaviour: TrueSkill, pair selection, session state, the v1 JSON
format, and the file moves. They build and their 44 tests run on Linux today. **The server
reuses them as-is.** This is the single biggest reason this project is tractable.

What is *not* reusable: `MediaPipeline`, `StillDecoder` and `VlcFramePlayer` are WPF/LibVLC
and stay in the app. The server needs its own media path.

`LibraryActions` sits in the App project but only depends on `IMediaPipeline` (an interface
in Core) and the file system — there is nothing WPF about it. It moves to a new `net8.0`
library so both hosts share one implementation of discard / special / undo.

---

## 2. Architecture

```
RankMaster2.Core          net8.0    types + interfaces            (unchanged)
RankMaster2.Ranking       net8.0    TrueSkill, PairSelector       (unchanged)
RankMaster2.Catalog       net8.0    JSON + file ops               (unchanged)
RankMaster2.Actions       net8.0    NEW - moved out of App, unchanged behaviour
RankMaster2.Server        net8.0    NEW - ASP.NET Core, headless
RankMaster2.Server.Tests  net8.0    NEW - integration tests over the real HTTP surface
rm2ctl                    net8.0    NEW - headless CLI client, drives a full cycle
RankMaster2.App           net8.0-windows  FROZEN
```

The only change to existing code is moving one file (`LibraryActions.cs`) and adding a
project reference. The app's behaviour does not change; its tests must still pass.

---

## 3. The hard problems, and how we solve them

### 3.1 Two writers to one JSON file

`RankingSession` is not thread-safe and `JsonCatalog.Save` is a read-modify-write of a file.
Today nothing stops the desktop app and the server from clobbering each other.

- The server takes an **exclusive OS file lock** on `<folder>/.rankmaster.lock` for the life
  of a session. If it cannot, the API returns `423 Locked` naming the holder.
- Inside the server, every session is guarded by a `SemaphoreSlim`; all mutations are
  serialised. Reads of immutable snapshots are lock-free.
- A later app change teaches the WPF app to respect the same lock. Until then: **do not run
  both against one folder.** This is documented, not enforced on the app side.

**Decided: one at a time.** Sole user, no concurrent access expected, so there is no
multi-client session sharing and no shared-cursor design. The lock stays anyway — it is about
twenty lines, it stops two server instances fighting, and it is what a future app change will
respect.

### 3.2 Double-votes from a flaky phone

A phone retrying a request must not vote twice. Every state-changing call carries the
`pairToken` of the pair it acted on. The server rejects a token that is not the current pair
with `409 Conflict` **and returns the current state in the body**, so the client self-heals
in one round trip instead of polling.

`pairToken` is an opaque server-generated value that changes on every advance.

### 3.3 Serving stills

WPF is not available here, so the server needs its own decoder. **ImageSharp** —
fully managed, no native dependencies, handles EXIF orientation and ICC profiles directly.

> Licence note: ImageSharp is under the Six Labors Split License — free for personal and
> open-source use, paid above a revenue threshold. Fine for this project; flagging it because
> it is a dependency choice that is annoying to reverse. SkiaSharp is the alternative if the
> licence is ever a problem, at the cost of per-platform native binaries.

- `GET /media/{id}/still?w=1080` re-encodes to the requested width, capped to a small set of
  allowed widths so the cache cannot be blown up by arbitrary values.
- Strong `ETag` from filename + size + mtime, `Cache-Control: private, immutable`. The phone
  downloads each image once.
- An on-disk cache under the server's own data directory, never inside the user's media
  folder. Bounded by size, LRU eviction.

### 3.4 Serving video

**Decided: originals only, no transcoding.** The library's files already play on the target
phone, and this is a personal project with one user and one device, so there is no reason to
carry a codec matrix for hardware we will never point at it.

This is the cheapest decision in the plan and it removes an entire dependency class:

- `GET /media/{id}/video` streams the file with HTTP Range support. That is all.
- **No FFmpeg.** No transcoding, no segment cache, no start-up latency.
- **No poster frames and no duration/codec probing** — both would require decoding video
  server-side, which is the only thing that would have dragged FFmpeg or LibVLC back in.
  The client plays the stream directly; it does not need a still of it first.

The server therefore never decodes a video frame. If a device that chokes on these files ever
matters, transcoding slots in behind the same URL without a client change.

### 3.5 Security on the LAN

"On the local network" is not the same as safe: any device on the Wi-Fi, including a guest
phone or a compromised IoT device, can reach the port.

- **TLS with a self-signed certificate** generated on first run and persisted. No CA, no
  browser trust store.
- **Pairing** out of band: `rm2ctl pair` prints a QR code and a short numeric code. The
  payload carries the host, port, certificate **SHA-256 fingerprint**, and a one-time pairing
  secret. The pairing window is short-lived, single-use, and rate-limited.
- The mobile client **pins that fingerprint**. This is what makes a self-signed certificate
  genuinely secure rather than theatre — there is no CA to trust and no warning to click
  through.
- After pairing, a long-lived per-device bearer token. Sent in the `Authorization` header,
  never in a query string, so tokens do not land in logs or proxy history. Tokens are
  revocable per device.
- Bind to an explicit LAN address from config, not blindly to `0.0.0.0`.
- **mDNS** (`_rankmaster._tcp`) so the phone finds the PC without anyone typing an IP.

**Decided: the client is Phase 2; the server is built first.** So the design stays
pinning-friendly and client-agnostic — the certificate fingerprint is exposed at pairing and in
`/ping`, ready for a native client to pin, and nothing is built specifically to accommodate a
browser. If the client later turns out to be a browser/PWA, the gap is that browsers cannot pin
and will show a certificate warning; that is solved then, by installing a trusted cert on the
phone, and not by weakening anything now.

### 3.6 Which folders the client may open

**Decided: full filesystem access.** The client can browse anywhere the server process can
reach, including drive roots and network shares. No allowlist.

That makes the bearer token the only thing standing between the LAN and every file on the PC,
which raises the stakes on §3.5 considerably — the auth has to be right, because there is no
second line of defence behind it.

One guardrail that costs nothing and takes nothing away: **`/media/*` only ever serves files
whose extension is in the still or video list, and only from inside the currently open session
folder.** Browsing is unrestricted; byte-serving is not. So a stolen token still cannot be
pointed at `C:\Users\...\passwords.kdbx` through the media endpoints.

## 4. API surface

All under `/api/v1`, all authenticated except pairing. JSON except media bodies.

**Pairing / auth**
```
POST   /pair                      one-time code -> device token
DELETE /pair/{deviceId}           revoke
GET    /ping                      liveness + server version + capabilities
```

**Libraries**
```
GET    /libraries/roots          drives and mount points
GET    /libraries/browse?path=    folders under any path, with media counts
```

**Session** — the whole cycle lives here
```
POST   /session                   {folder} -> open/resume; 423 if locked
GET    /session                   folder, counts, session votes, progress, cues, current pair
DELETE /session                   close and release the lock
POST   /session/save              explicit save (Ctrl+S equivalent)
```

**Pair + actions** — every action takes `pairToken`, every response returns the new state
```
GET    /session/pair              current pair + warm pairs for client prefetch
POST   /session/vote              {pairToken, winner: left|right}
POST   /session/skip              {pairToken}
POST   /session/discard           {pairToken, side: left|right}
POST   /session/special           {pairToken, side: left|right}
POST   /session/undo              undo last move
```

**Media**
```
GET    /media/{id}/meta           kind, dimensions (stills only), file size
GET    /media/{id}/still?w=       sized JPEG/WebP, ETag, immutable
GET    /media/{id}/thumb
GET    /media/{id}/video          Range-capable stream of the original bytes
```

Deliberately absent: **rename**. No endpoint, no plumbing, no flag.

Warm pairs are returned so the client prefetches exactly as the desktop pipeline does. The
session response is a complete snapshot — folder name, unranked count, session votes,
confidence progress, and the last-10 cue strip — so a phone that has been asleep resyncs in
one call.

---

## 5. Sub-agent team

I orchestrate; agents own disjoint directories so they never edit the same file. No agent may
touch `RankMaster2.App` — it is frozen.

**Phase 0 — me, alone.** Solution skeleton, all new `.csproj` files, project references, the
`LibraryActions` move, CI-less build script. Doing this first means no agent ever edits the
`.sln` or a shared `.csproj`, which is where parallel agents usually collide.

**Phase 1 — contract, alone.** One agent writes `SERVER_SPEC.md` and the OpenAPI document:
every endpoint, every status code, the `pairToken` rules, the error envelope. This must land
before anything is implemented, because it is what the other four agents code against.
I review it against `SPEC.md` before Phase 2 starts.

**Phase 2 — four agents in parallel, each in its own folder.**

| Agent | Owns | Deliverable |
|---|---|---|
| Session | `Server/Sessions/` | session registry, folder locking, pair tokens, action orchestration over `RankingSession` |
| Media | `Server/Media/` | ImageSharp still pipeline, cache + ETag, range streaming, probe, poster frames |
| Security | `Server/Security/` | cert generation, pairing, token store, auth middleware, rate limiting, mDNS |
| Harness | `Server.Tests/`, `rm2ctl/` | synthetic media fixtures, integration tests, the headless CLI client |

The Harness agent works from the contract, not from the implementations, so its tests are an
independent check rather than a mirror of whatever got built.

**Phase 3 — integration, me.** Wire the four together, make `rm2ctl` complete a full cycle
against a real folder.

**Phase 4 — adversarial review.** One agent reviews the whole server against the contract and
`SPEC.md`, hunting for the failure classes this codebase has already shown: state mutated off
the owning thread, errors attributed to the wrong item, resources freed while in use. A
second agent reviews only the security surface.

Six agents total, never more than four at once.

### Acceptance gate

The server is "completely functional" when `rm2ctl` can, against a scratch folder of real
media, with the desktop app never launched:

1. pair from cold, over TLS, with a pinned fingerprint
2. open a folder, get a pair, fetch both stills at display size
3. vote, skip, discard, special, undo — and see `rankmaster_db.json` correct after each
4. retry every one of those calls with a stale `pairToken` and get `409` with recoverable state
5. kill the server mid-session and resume with no lost or double-counted votes
6. run a 200-vote cycle and have the resulting JSON load unchanged in the desktop app

Point 6 is the one that matters: **the existing app must be able to open the file the server
wrote, with ratings intact.** That is the compatibility contract.

---

## 6. Risks

| Risk | Mitigation |
|---|---|
| A future device cannot play the originals | out of scope by decision; transcoding can slot in behind the same URL |
| Server and app corrupt the JSON together | exclusive lock file; documented until the app respects it |
| Token is the only barrier to the whole filesystem | media endpoints restricted to media extensions inside the open session folder; pairing rate-limited and single-use |
| Large libraries slow to scan | `Scan` is O(n) with a `File.GetAttributes` per file; measure at 20k before optimising |
| ImageSharp licence | flagged above; SkiaSharp is the escape hatch |
| Agents drift from the contract | contract lands in Phase 1 and is reviewed before any implementation |

---

## 7. Decisions on record

Settled before Phase 0, and the reasoning that goes with them:

| Question | Decision | Consequence |
|---|---|---|
| Mobile client platform | Deferred — Phase 2 | Design stays pinning-friendly and client-agnostic |
| Video handling | Originals only | **No FFmpeg, no poster frames, no video probing.** Server never decodes video |
| Folder access | Full filesystem | No allowlist; media endpoints restricted by extension + session folder instead |
| Coexistence | One at a time | No multi-client design; lock file retained as cheap insurance |

Decisions 2 and 4 together removed roughly a third of the original scope, which is why the
Media agent's job is now stills-plus-range-streaming rather than a media subsystem.

---

## 8. Phase 0 — complete

Done directly, not delegated, so no agent ever has to edit a shared `.sln` or `.csproj`:

- `RankMaster2.Actions` (`net8.0`) created; `LibraryActions.cs` moved into it from the App
  project and the App given a project reference. Behaviour unchanged — it only ever depended
  on `IMediaPipeline`, which is an interface in Core.
- `RankMaster2.Server` (ASP.NET Core), `rm2ctl`, and `RankMaster2.Server.Tests` created and
  added to the solution.
- `RankMaster2.Server.slnf` — a solution filter excluding the Windows-only App project, so the
  whole server side builds and tests on any machine.
- Fixed `JsonCatalogTests.SaveAndScan_RoundTripsRatings`, which selected a record by index and
  so depended on `Directory.EnumerateFiles` ordering. It passed on NTFS and failed on ext4. A
  suite that is red for environmental reasons is a suite people stop reading.

**Verified: `dotnet build RankMaster2.Server.slnf` succeeds and 45/45 tests pass.**

The one thing that is *not* verified is the App project itself — it is `net8.0-windows` and
cannot be compiled here. The change to it is one added `ProjectReference` and one moved file,
but it needs a build on Windows to confirm.
