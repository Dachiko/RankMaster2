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

**Rename-by-rank stays, and lives on the server** (2026-09-16). It is a deliberate reversal of
`SERVER_SPEC.md` § 1.1, which forbids a rename endpoint in unusually strong terms; the contract
changes first, then the implementation. Planned separately as part F.

**Rename does not copy the library** (2026-09-16). `FileOps.BackupLibrary` copies every file in the
folder byte for byte before renaming anything — gigabytes on a library of 4K video, and a temporary
doubling of the space it occupies. The renames themselves are directory-entry moves and are nearly
instant, so the copy was the whole cost of the operation and it bought an undo that a record of the
renames provides for nothing.

Out, by the owner's decision. What replaces it is part F's problem: a durable journal written before
the first move, and an answer for the database, which a map of filenames does not protect.

`SPEC.md` § Rename by rank steps 2 and 6 specify the copy; that text now describes only the frozen
desktop app and part F says what the spec becomes. The `rankmaster_backup_*` scan exclusion stays —
folders from past renames are still on the owner's disk.

**The ratings are the asset; the filenames are cosmetic** (2026-09-16, the owner). A rename moves
directory entries and never touches a byte of a photograph, so after any crash every image is on
disk under some name. `rankmaster_db.json` is the only irreplaceable thing in the folder — it holds
hours of pairwise judgement that nothing can reconstruct.

So the failure to design against is not a messy folder. It is **a rating that loses its file**: the
database keys by filename, `JsonCatalog.Save` merges against what is on disk, and a database left
naming files that have been renamed can be quietly emptied by the next ordinary save. Silent, total,
and indistinguishable from never having ranked at all. `SPEC.md` already forbids the neighbouring
case; this is the same fear one step along.

A half-renamed folder is therefore an acceptable resting state, and recovery's job is to reunite
ratings with files rather than to tidy names. The test that matters is not "does rename work" but
"kill it at each step, and does every rating still find its file".

**Rename shows progress and can be cancelled** (2026-09-16). That makes it the first long-running
operation in a contract where every endpoint is synchronous, and it puts one hard question at the
centre of part F: what cancel means when three thousand files have already been renamed. Rolling
back is itself thousands of file moves, so the cancel needs its own progress and must not look
frozen. The owner has to know *before* he presses it whether cancel undoes the work or stops where
it stands.

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

## Rulings, where two plans disagreed

Both flagged by part A on the way in. Settled here so nobody works around them.

**`IMediaProbe` splits in two, because it was two questions wearing one name.** Part C asked for an
async, folder-level answer; part D for a synchronous per-file one. Both are right about their own
question:

- *"Does this folder contain video?"* enumerates a directory. It is I/O, it is async, part C owns it,
  and part A runs it **concurrently** with the server open so the video engine can begin waking
  before the snapshot lands.
- *"Is this file a video?"* is a string and a table. It is `Core.MediaExtensions.KindOf`, it already
  exists, it is already the server's answer, and it does not need a seam at all. Anyone who wants it
  calls Core.

A seam is for a thing one part does on another's behalf. Classifying an extension is not that.

**The HEVC packetizer ships.** Part D listed it; part A excluded it, citing `SPEC.md`'s non-goals.
The non-goal is real but it is about *images* — it sits in a list with HEIC, RAW, TIFF and AVIF, and
those are still formats.

What settles it is that the video policy is **by extension**: `mp4 webm mkv avi mov` (`SPEC.md`
§ Media policy). So an `.mp4` is paired and shown whatever codec is inside it, and if that is HEVC
and the packetizer is absent, the owner gets a file he can see in his folder, cannot play, and
cannot do anything about. One DLL against that is a trade worth making. It is not support for HEVC
as a feature; it is not failing on a file the product already claims to handle.

## Settled by the owner, 2026-09-17 (after the independent audit)

**Rename names carry a per-run suffix** (2026-09-17, the owner). Each run of rename draws four
hexadecimal characters of its own and every file it produces carries them: `000001-7f3a.jpg`, not
`000001.jpg`. His reasoning, and it is the better answer than either of the two the auditor proposed:
he does not care what a file is called, only that sorting by name gives rank order — so the cheapest
way to make an interrupted rename unambiguous is to make the names of one run impossible to confuse
with the names already in the folder. With the two sets disjoint there is one move per file and no
temporary names at all, and recovery never has to work out whether a given file has already moved: its
name says which set it belongs to. The full scheme is `SERVER_SPEC.md` § 10.16 (and `SPEC.md`
§ Rename by rank); the naming rules are normative and not to be re-decided.

**The progress bar and the cancel button are for a slow drive, not a big folder** (2026-09-17, the
owner). On a local disk a rename is over before he can read the bar. What he actually wants is to be
able to **stop a rename on a USB drive the moment it looks wrong**, because he does not know how one
behaves there. That is a requirement on cancel's latency, not on the bar: cancel is honoured before
every move *and between the retries of a move waiting on a locked file*, so it lands within one retry
interval rather than after the whole retry budget. Cancel stops where it is, undoes nothing, and loses
no rating. Part E's screen must offer Cancel from the first instant and show `done / total`, never a
bare spinner.

**Skip is removed from the PC client** (2026-09-17, the owner: he does not use it). `↓` and `S` map
to nothing and the help sheet does not list them. This is a surface change in this client only —
`ISessionLink.SkipAsync` stays and the link tests keep exercising the endpoint, because the server
keeps `POST /session/skip` and every test of it. The phone never offered it; the frozen desktop app
keeps it.

**Durability is relaxed by contract** (2026-09-17, the owner: *"I'm not afraid of losing a couple of
votes, it's non-consequential."*). A vote or skip is applied and answered at once; the database is
written within two seconds or five choices, whichever comes first. Everything that moves a file, a
rename, an explicit save, closing the session and shutting down are still written before they answer.
This is `SERVER_SPEC.md` § 13.1, which it replaces rather than bends, and `SaveDelaySeconds = 0` in
`appsettings.json` restores the old behaviour exactly. For the PC client it changes one thing worth
knowing: **a `200` on a vote no longer means the file has been written**, so anything that needs a
durability point — before telling him it is safe to unplug the drive, or before a test reads
`rankmaster_db.json` — calls `POST /session/save` and waits for its `200`.

---

**Part A's two seam additions are granted** (`LibVlcLayout`, `StartupClock.Mark`) — both are part A's
own surface rather than anything another part must honour.

---

**Rename is the server's, not the client's.** `PC_CLIENT_PLAN.md` § 6.6 predates the owner's
decision and has the PC renaming files itself under the lock convention, using
`RankMaster2.Actions`. That is now wrong: rename is part F, on the server, behind
`POST /session/rename`. The PC client shows the progress and the cancel button and does not move a
single file. § 6.6 is superseded.

This matters beyond tidiness — the whole reason rename moved to the server is that the server is the
only writer. A client that renames locally is a second writer wearing a disguise.

---

## Verification, for every part

Development is on Linux. .NET compiles and publishes for Windows here; **nothing Windows runs
here.** So: unit tests are the only executable proof, every part is expected to carry them, and
anything that can only be true on Windows is reported as unverified rather than implied. The owner's
PC is the only place this can be seen working, and his time is the scarcest thing in the project.
