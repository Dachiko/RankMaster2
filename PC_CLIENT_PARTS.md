# The PC client, broken into parts

`PC_CLIENT_PLAN.md` is the architecture and does not change here. This is the division of labour:
five parts, each owned by one agent, each with a plan of its own written before anything is built.

The rule that makes parallel work possible is the one that worked for the server and the phone:
**an agent owns a folder, and the seams between folders are agreed before anyone starts.** No agent
edits a shared file; where two parts meet, the meeting point is named below and frozen by me.

---

## A — Startup and shell

**Owns** `pc/src/RankMaster2.Pc/App/`, the project files, and the publish scripts.

The owner's actual complaint. Process start to first pair on screen: the window, full screen, the
startup path, and the packaging that decides how much work a launch does. `PC_CLIENT_PLAN.md` § 3
identifies the suspects — an eagerly initialised video engine, no ReadyToRun, a 320-DLL plugin scan
with no index — and this part owns all of them, including pruning the plugin set to what this app
actually uses and generating the index at publish time.

Also owns the measurement: a launch is not "fast" because someone says so.

## B — The server link

**Owns** `pc/src/RankMaster2.Pc/Link/`.

Every call to the local server, the session lifecycle, and the retry protocol of `SERVER_SPEC.md`
§ 13.3 — the rules that make a lost response impossible to turn into a double vote. The phone's
equivalent is `android/…/net/`, and its `RankViewModel.act` is the shape to copy: the retry lives
inside the call, where the token cannot have changed.

This part decides nothing about pixels and nothing about windows.

## C — Stills

**Owns** `pc/src/RankMaster2.Pc/Stills/`.

Pixels from the local disk, which is the PC client's one advantage over the phone: decode at the
size the pane will draw, honour EXIF orientation and the embedded colour profile, prefetch the warm
pairs the server hands out. `SPEC.md` § Pipeline is the behaviour; `src/RankMaster2.Server/Media/
StillRenderer.cs` is a working implementation of the same job to read before inventing another.

## D — Video

**Owns** `pc/src/RankMaster2.Pc/Video/`.

The owner's library is AV1, up to 4K, in five containers. The old app needed LibVLC because WPF
would not play AV1, and video has already killed two designs on the phone. Frame callbacks into a
bitmap the UI can draw, initialised **only when a folder actually contains video** — that last
clause is half of part A's startup problem and belongs to this part to honour.

`src/RankMaster2.App/Playback/` is the working prior art. `android/MEMORY_PROPOSALS.md` § 1–2 is the
cautionary tale about buffers and simultaneous decoders.

## E — The ranking surface

**Owns** `pc/src/RankMaster2.Pc/Ui/`.

The two-pane compare, driven by the keyboard as `SPEC.md` § Keys defines it, plus the start screen
that picks a folder and resumes the last one. The match strip, the progress figure, and whatever
cancel becomes on a keyboard.

This is where the owner lives. It is also the part with the least freedom: `SPEC.md` fixes the
behaviour, and the phone already settled what the two-pane surface should and should not carry.

---

## Settled by the owner, 2026-09-16

**`Ctrl+Z` undoes whatever the server allows.** Not the old app's move-only undo: the server's
one-level cancel of the last action of any kind — vote, skip, discard or special
(`SERVER_SPEC.md` § 10.10).

This is a deliberate deviation from `SPEC.md` § Keys, which describes the frozen desktop app and
predates the server having a wider undo. The reasoning is the same as it was for the phone: a mis-hit
key is a real vote, and the only way back used to be to keep voting and hope. There is now a way
back, and it should be under the key the owner's fingers already reach for.

The consequence part E must carry: the key now does something *more* than it used to, so what it just
undid has to be said out loud. The server names it — `lastAction.undoneType` is exactly `vote`,
`skip`, `discard` or `special` — and a person who presses undo expecting one thing and gets another
without being told has been lied to by the interface.

---

## The seams, frozen before anyone starts

Named here so that five plans agree about where they meet. I write these as real types before
execution begins; the planners may argue for changes, but not invent their own.

| Seam | Between | What crosses it |
|---|---|---|
| `ISessionLink` | B → A, E | open a folder, the snapshot, vote/skip/discard/special/cancel/save/close |
| `Snapshot` and friends | B → everyone | the § 9 wire types, read-only to everyone but B |
| `IStillSource` | C → E | "give me this id at this pane size", plus prefetch and failure states |
| `IVideoSurface` | D → E | create, start, stop, release, current frame, and "this file will not play" |
| `IMediaProbe` | C, D → A | "does this folder contain video" — the question that keeps the video engine asleep |

## Verification, for every part

Development is on Linux. .NET compiles and publishes for Windows here; **nothing Windows runs
here.** So: unit tests are the only executable proof, every part is expected to carry them, and
anything that can only be true on Windows is reported as unverified rather than implied. The owner's
PC is the only place this can be seen working, and his time is the scarcest thing in the project.
