# Part F — rename by rank, on the server

**Status: plan, nothing built.** Reverses a pinned product decision, adds the first long-running
operation in the contract, and replaces the desktop app's file-backup with a small durable journal.

Settled owner decisions, in order given (2026-09-16):

1. Rename by rank survives, and lives **on the server**, not in a client.
2. Rename **shows a progress bar and offers a cancel button** — so it is a started / observed /
   cancellable operation, not one synchronous request.
3. **No file backup.** The copy `RenameByRank` makes today is gigabytes of untouched pixels and a
   temporary doubling of disk; it goes. Safety is a **journal**, not a copy.
4. **The database is the only asset.** *"If the machine dies mid-renaming, the only thing which will
   matter is if the database is still true, or can be recovered and contain the truth again. Images
   are there, but the hours of matching is what we can, but shouldn't lose."* The whole crash-safety
   design serves this ranking and nothing else.

The project rule is **spec first**: SERVER_SPEC.md changes and is reviewed (Phase 1), then it is
built (Phase 2+). A weak executor must not re-decide anything below.

Owns: new journal-based rename code in `src/RankMaster2.Actions/` (or `Catalog/`), edits in
`src/RankMaster2.Server/Sessions/`, `SERVER_SPEC.md`, `SERVER_PLAN.md`, `SPEC.md` (a scoping note
only), `openapi.yaml`, `tests/RankMaster2.Server.Tests/`, `tests/RankMaster2.Catalog.Tests`, the two
affected audit suites, and `src/rm2ctl/`. It does **not** touch the frozen desktop app or its
existing `RenameByRank()`.

Read first: `SPEC.md` § Rename by rank, § File actions, § Persistence ("Never write an empty
database"), § 40 (scan exclusions), § 66 (screens); `SERVER_SPEC.md` § 1.1, § 6, § 7, § 8, § 9,
§ 10.1, § 11.1–11.3, § 12.5, § 13, § 14, § 16; `src/RankMaster2.Catalog/JsonCatalog.cs` (`Scan` and
`Save` — **`Save` merges by filename against what is on disk**, the fact the whole design turns on);
`src/RankMaster2.Catalog/FileOps.cs` (`RenameByConservativeScore` — the ordering and the two-phase
move); `src/RankMaster2.Actions/LibraryActions.cs` (`RenameByRank`, left alone);
`src/RankMaster2.Ranking/RankingSession.cs` (`ReplaceAll`); `src/RankMaster2.Server/Sessions/SessionRegistry.cs`
(`OpenAsync`, `MoveAsync`, `WithLockAsync`, `CurrentForMedia`).

---

## 1. The one fact everything follows from

`JsonCatalog.Save` keys by filename and **merges against the files currently on disk**: it keeps a
row for every media file present and drops rows for names no longer present (`JsonCatalog.cs`, and
`SPEC.md` § Persistence). `Scan` does the same in reverse — a file with no matching row gets a
**fresh** default rating (25.0 / 8.333).

Therefore the catastrophe to design against is precisely this: **files renamed, database not.** Every
row then names a file that no longer exists, and the *next ordinary save* — a single later vote —
silently drops every rating and leaves a folder that looks like it was never ranked. Silent, total,
and indistinguishable from starting over. `SPEC.md` already forbids the neighbour of this case
("Never write an empty database"); this is the same fear one filename along.

The images are never at risk: a rename moves directory entries, the bytes are untouched, and after
any crash every photograph is on disk under *some* name. A half-renamed folder is untidy, not
damaged. So the entire safety problem reduces to one sentence:

> **Every rating must always be able to find its file, at whatever moment the machine dies.**

The journal exists for exactly that, and the acceptance gate (§ 8) tests exactly that.

---

## 2. Decisions, settled before any code

| Question | Decision | Where |
|---|---|---|
| Safety mechanism | **A journal, not a file backup.** A small file in the folder holding, before the first move, every file's old name, its intended new name, and its full rating row (`mu, sigma, matches, impressions, lastPlayed`) — fsynced before anything moves | § 3 |
| What the journal protects | **The ratings.** It is a durable copy of the tiny irreplaceable data, not of the gigabytes of replaceable pixels | § 1, § 3 |
| Recovery direction | **Forward, and automatic.** On the next open, before any scan or save, recovery completes the rename toward the new names where it can and **reunites every rating with its file at that file's current name**. It never needs the owner | § 3.3 |
| A half-renamed folder | **An acceptable resting state.** Recovery reunites ratings even if it cannot finish the moves; mixed names is a success, and the owner can re-run rename | § 3.3, owner decision 4 |
| Shape on the wire | **A started/observed/cancellable operation**: `POST /session/rename` (→ `202`), `GET /session/rename`, `POST /session/rename/cancel` | § 3.4 |
| The slow phase | **There isn't one, for a normal folder.** With no backup, the work is directory-entry moves plus two small JSON writes — near-instant. The bar and cancel matter only for a folder with enough entries to take perceptible time; a small folder simply finishes | § 3.1 |
| Cancel | **Stop and reunite in place** — stop issuing moves, reunite the ratings at the files' current names, leave a valid (possibly half-renamed) folder. Never a database-risking rollback. Effective mainly in the first instants; a small folder is done before the button can land | § 3.5 |
| Ownership on disconnect | **Server-owned.** The run reaches a consistent terminus even if the client vanishes; a crash mid-run is now recoverable by the journal on next open | § 3.3, § 3.4 |
| Concurrency | Nothing else mutates the session while a rename runs → `409 rename_in_progress`; reads and the poll are unaffected | § 3.6 |
| `features.rename` | **`true`** | § 3.7 |
| New error codes | `rename_in_progress` (409), `no_rename_operation` (404), `rename_failed` (500) | § 4 |
| Who offers it | **PC client yes, phone no** — a recommendation; the endpoint is open to any token | § 3.7 |
| Shared code | The server gets a **new** journal-based rename path; the desktop's `RenameByRank()` (backup-based) is **untouched**; `SPEC.md`'s rename steps are scoped to the app | § 5, § 3.8 |

### 2.1 Deliberately absent

- **No file backup, and no `rankmaster_backup_*` folder created by the server.** Existing ones from
  past desktop renames stay ignored (`SPEC.md` § 40 exclusion is kept — § 5).
- **No database-risking rollback.** Cancel and recovery both reunite ratings; neither tidies
  filenames at the ratings' expense.
- **No undo of a completed rename**, no `rename 2`, no options, no dry run. `μ − 3σ` descending,
  then filename, to `000001.ext` — fixed by `SPEC.md`.
- **No client UI here** (`PC_CLIENT_PLAN.md` § 6.6, `pc/plans/E-ranking-surface.md` § E6).
- **No change to ranking behaviour** or to the desktop app's rename.

---

## 3. The hard problems, and their answers

### 3.1 What the slow phase actually is

Two earlier framings of this plan were wrong in opposite directions; state the truth in the spec.
`FileOps.RenameByConservativeScore` renames **within one directory** via temporaries — directory-entry
updates, no bytes copied, near-instant even for thousands of files. The desktop's `BackupLibrary`
was the slow, space-doubling part, and it is gone (owner decision 3). So the server's rename is:
compute a plan, write a small journal, do the moves, rewrite a small JSON, delete the journal — all
fast. **For the owner's library it finishes in well under a second.** The progress bar and the cancel
button exist for a folder with tens of thousands of entries, where even directory-entry churn takes
perceptible time; a small folder is over before a human can react, and the spec and the client copy
must say so plainly rather than pretend otherwise.

### 3.2 The journal — a durable copy of the asset, not of the pixels

Before the first file moves, the server writes `<folder>/.rankmaster-rename.json` and fsyncs it (the
same write-tmp / `Flush(true)` / atomic-replace discipline `JsonCatalog.Save` uses). It contains:

```
{ "format": 1, "state": "renaming", "createdAt": "…Z",
  "plan": [ { "old": "DSC_0123.jpg", "new": "000001.jpg",
              "mu": 27.1, "sigma": 4.2, "matches": 9, "impressions": 9, "lastPlayed": 1726… }, … ] }
```

- It lives **in the folder**, so a process that did not write it finds it when that folder is opened.
- Its extension is not a media extension, so `JsonCatalog.Scan` never lists it (like `.rankmaster.lock`).
- It carries **every rating row**, so recovery can rebuild a correct database from the journal alone,
  independent of what state the on-disk `rankmaster_db.json` is in. This is the "second copy" the
  owner allowed — of the kilobytes that matter, never of the gigabytes that do not.
- The **new** names are collision-safe and the **temporaries are deterministic**: phase 1 moves each
  `old` to `__rm2_<new>` (e.g. `__rm2_000001.jpg`), phase 2 moves `__rm2_<new>` to `<new>`. Every
  temporary on disk names its own target, so recovery can identify any file it finds without a
  per-file journal write. (This replaces the random-GUID temporaries of the desktop path, which a
  crash would leave unidentifiable.)

### 3.3 Ordering, and what recovery does at every interruption point

The live forward operation, in order:

1. **`preparing`** — under the session lock: compute the plan (order by `μ − 3σ` desc, then
   filename; assign `000001…`) from the current records and their ratings; **write and fsync the
   journal** (`state: renaming`); mark the session rename-in-progress. Release the lock; run the rest
   on a server task (off the lock), observable and cancellable.
2. **`renaming`** — phase 1 (`old` → `__rm2_<new>`) then phase 2 (`__rm2_<new>` → `<new>`), reporting
   moves-done of `2 × N`, checking for cancel between files.
3. **`saving`** — `RemapIds` + `JsonCatalog.Save` writes the database with the **new** keys (atomic).
4. **delete the journal** (the commit point).
5. **apply to the session** under the lock: `ReplaceAll(remapped)`, clear the move-undo, `pairSeq++`,
   `lastAction.type = "rename"`, clear the flag.

**Recovery** runs on the next `POST /session`, under the folder lock, **before** `RankingSession.Start()`
scans anything. If `<folder>/.rankmaster-rename.json` is present, a rename was interrupted. Recovery
is forward and total:

- **Finalize (best-effort):** for each plan entry, move whatever is on disk toward `<new>`
  (`old` → `__rm2_<new>` → `<new>`, skipping any entry whose file is entirely gone). Directory moves,
  fast. If one cannot be finalized, that is not a failure (owner decision 4).
- **Reunite (the part that matters):** list the media files actually on disk now; for each, take its
  rating from the journal (matched by whichever of `new` / `__rm2_<new>` / `old` the file currently
  bears) and write a database keyed by the file's **current** name, atomically. A file with no journal
  entry gets a default rating (as `Scan` already does); a journal entry whose file is gone is dropped
  (its image no longer exists — the only rating "loss", and unavoidable).
- **Delete the journal.** Done.

Because the on-disk `rankmaster_db.json` is only ever written atomically, and because the journal
carries the ratings independently, **every interruption point resolves to "every rating finds its
file":**

| Killed at | On disk when reopened | Recovery reunites because |
|---|---|---|
| after the journal fsync, before any move | files original, DB original, journal present | files are still `old`; reunite writes the same DB; harmless |
| mid phase 1 / phase 2 | mix of `old`, `__rm2_<new>`, `<new>`; DB still original (old keys) | the journal maps every current name back to its rating; reunite keys the DB to current names |
| after all moves, before the DB save | files all `<new>`, DB still old keys | **the dangerous case** — the ordinary `Scan`/`Save` would drop every rating; recovery instead reunites from the journal, so ratings follow the files |
| after the DB save, before the journal delete | files `<new>`, DB new keys, journal present | reunite is idempotent (ratings already match); it just deletes the journal |
| after the journal delete | files `<new>`, DB new keys, no journal | nothing to recover; consistent |

The one thing that does **not** heal a mid-rename crash is the ordinary `Scan` merge — it keys by
filename and would assign the renamed files fresh ratings and drop the real ones. That is why
recovery via the journal must run **before** anything scans, and the spec says so in those words.

### 3.4 The shape on the wire — the first long-running operation

```
POST   /session/rename          start    -> 202 Accepted + RenameOperation (running)
GET    /session/rename          observe  -> 200 RenameOperation   (poll for the bar; no lock taken)
POST   /session/rename/cancel   cancel   -> 200 RenameOperation
```

`RenameOperation`: `{ operationId, state, phase, done, total, startedAt, updatedAt, error }` where
`state ∈ running | cancelling | succeeded | cancelled | failed`, `phase ∈ preparing | renaming |
saving | reuniting | done`, and `error` is `null` or `{ code: "rename_failed", reunited: bool,
journal: string|null }`.

- **Start** takes the session lock only to create the operation, write the journal and set the flag,
  then returns `202` with `Location: /api/v1/session/rename`. **`202` is an acknowledgement, not a
  completion** — the first exception in the contract to "every endpoint is synchronous" (§ 10) and
  to "a 2xx means the change is durable" (§ 13.1). Durability is asserted only when the operation
  reaches `succeeded`.
- **Observe** reads the operation without the session lock (the lock-free discipline
  `CurrentForMedia` already uses), so the poll never blocks behind the run.
- **Cancel** — § 3.5.
- On `succeeded`, the client fetches the new snapshot with `GET /session` (`pairSeq` +1, new numeric
  ids, `lastAction.type == "rename"`). On `cancelled`/`failed` the ratings are safe and the client
  resumes; it re-reads `GET /session` because the session was resynced to disk.

Everything else stays inside convention: one error envelope (§ 4), stable codes (§ 5), and `/session`
keeps its single `SessionSnapshot` shape — the operation is a separate small resource.

### 3.5 Cancel — stop and reunite, never a rollback that risks the database

Cancel does not undo to be tidy; it **stops and guarantees the ratings are safe at the current
names** — which is exactly the recovery routine, run on demand rather than after a crash:

- In `preparing`, before the journal is written and before any move: abort cleanly, delete any
  half-written journal, session unchanged. Fully effective.
- In `renaming`: stop issuing moves, run **reunite in place** (do *not* finalize the remaining
  moves) — write the database keyed by the files' current (mixed) names from the journal's ratings,
  delete the journal, then resync the session from a fresh `Scan`. Terminus `cancelled`. The folder
  is consistent and possibly half-renamed; every rating finds its file; the owner may re-run rename.
  No database risk — the reunite `Save` is atomic and writes correct data.
- In `saving` or later: too late; the operation completes. Cancel returns the current state.

Cancel is idempotent (a second press returns the operation, no second action). Because the moves are
near-instant (§ 3.1), for a normal folder cancel almost always lands either in `preparing` (aborts)
or after `succeeded` (too late); the "stop in place" path is reached only on a folder large enough to
still be moving. The client tells the owner cancel restores nothing it has already finished — it
stops where it is and keeps every rating.

### 3.6 Concurrency

The run holds the session semaphore only at the start (create + journal + flag) and at the final
apply (`ReplaceAll`). While the moves run off the lock, the rename-in-progress flag makes every
mutating `/session*` call that takes the lock return `409 rename_in_progress` (`details.operationId`);
a `GET /session` read still works and shows the pre-rename session; the status poll is lock-free.
Media `GET`s (lock-free) see files mid-move and answer `404 media_file_missing`, and afterward
`404 unknown_media_id` for the old ids (§ 11.3) — never corruption, because a `GET` never mutates.

### 3.7 `/ping`, and who offers it

`features.rename` becomes **`true`** (a fixed capability, § 14); a server predating this part answers
`false` and a client hides the option. **Availability is a server question: the endpoint is open to
any paired token.** **The offering is a client question, and the recommendation is PC yes, phone no** —
argued as the owner asked: rename is a deliberate maintenance act on the machine the library lives on;
a phone is the worst place to watch a bar or press cancel (walk out of range and the server-owned run
finishes without you); the PC client, in front of the library, is where the affordances the owner
asked for actually work. The PC client offers it behind the one confirmation `SPEC.md` allows; the
phone does not.

### 3.8 The shared code, and the `SPEC.md` conflict

`SPEC.md` § Rename by rank steps 2 and 6 (copy to backup / restore from backup) and § 66 ("Progress
for backup + two-phase rename") describe the **frozen desktop app**, which is deprioritised and
cannot be built on this machine. They are now wrong for the server, and spec-first forbids leaving
that. Resolution:

- The server gets a **new** journal-based rename path (§ 5); the desktop's `LibraryActions.RenameByRank()`
  is **left exactly as it is** — the frozen app still copies and restores, and its behaviour is not
  changed blind on a machine that cannot build or test it.
- `SERVER_SPEC.md` § 10.16 (new) is the **normative** spec for the server's rename — journal, no
  backup, forward recovery.
- `SPEC.md` gains a **scoping note** at § Rename by rank and § 66: "This describes the desktop app.
  The headless server renames without copying files, using a journal specified in `SERVER_SPEC.md`
  § 10.16." The desktop steps are otherwise left intact.
- `SPEC.md` § 40's exclusion of `rankmaster_backup_*` from scanning is **kept** — the server creates
  no such folder, but old ones on the owner's disk from past desktop renames must still be ignored.

---

## 4. The exact spec text to add to `SERVER_SPEC.md`

Phase 1 makes these edits and nothing else. Keep the house voice.

**§ 1.1 — replace the first bullet.** Delete "Rename by rank. There is no rename endpoint …" and add:

> - **Rename by rank is a journalled, long-running operation at `/session/rename` (§ 10.16)** —
>   `POST` to start, `GET` to observe, `POST /session/rename/cancel` to stop — and nowhere else. It
>   copies no files. A request to any *other* path containing `rename` MUST fall through to `404
>   not_found`.

**§ 10.16 — new subsection, appended after § 10.15** (appended so §§ 11–16 keep their numbers). It
states § 3 as normative spec, and MUST cover, in order: the purpose (the server side of `SPEC.md`
§ Rename by rank, but with **no file backup** — safety is the journal); that it takes no `pairToken`
and ignores one if sent; the three routes and the `RenameOperation` shape (§ 3.4) verbatim including
the enums; that `202` is an acknowledgement, not a completion (cross-reference § 13.1); the journal
(§ 3.2) — its path `<folder>/.rankmaster-rename.json`, that it is written and fsynced before the
first move, carries every rating row and the old→new plan, uses deterministic `__rm2_<new>`
temporaries, and is ignored by `Scan`; the ordering and the crash matrix (§ 3.3) — **every
interruption point resolves to "every rating finds its file," and recovery runs on the next open,
under the folder lock, before anything scans**; recovery is forward and total and never needs the
owner, a half-renamed folder being an acceptable resting state; cancel (§ 3.5) — stop and reunite in
place, never a database-risking rollback; on success the session effect (same `sessionId`, `pairSeq`
+1, new numeric pair ids, `cues` cleared, `undoAvailable` false, `sessionVotes`/`counts`/`progress`
unchanged in value, `lastAction.type == "rename"`; an in-flight action arriving after success gets
`409 stale_pair_token` with the new snapshot and MUST NOT be replayed; every cached media URL now
`404`s — § 11.3); concurrency (§ 3.6); and that the honest slow-phase truth is there is none for a
normal folder (§ 3.1).

**§ 6 — status codes.** Add a `202` row for `POST /session/rename` (not a completion; durability at
`succeeded`). Note `409` now also covers `rename_in_progress`, `404` also `no_rename_operation`,
`500` also `rename_failed`.

**§ 7 — session state machine.** Add a note to § 7.4: a rename does not change `sessionId`; on
success it advances the session one generation with every id new; on cancel/failure the session is
resynced from disk with ratings intact. Add transition rows to § 7.2 for rename started / succeeded /
cancelled-or-failed.

**§ 8.3 — `pairSeq` table.** Add "rename succeeds → +1 → stale, every id new"; "rename cancelled or
failed → resynced from disk → old tokens stale". Extend the reset note: a rename does not reset
`pairSeq` and does not change `sessionId`.

**§ 9.4 — `lastAction.type` enum.** Add `rename` (every other field null).

**§ 12.5 — known limit.** Add the sentence that a rename reassigns names to `000001.ext …`, so across
two renames a numeric id can reuse an ETag — the same `(name, size, mtime)` limit; within one rename
it cannot bite.

**§ 13.1 — the invariant's first, named exception.** Add: "The one exception is `POST /session/rename`
(§ 10.16), which returns `202` before the change is durable, because the owner requires a progress bar
and a cancel button. A rename's durability is asserted only when its operation reaches `succeeded`;
until then the folder is explicitly mid-flight, and its safety rests on the journal (§ 10.16), not on
the response."

**§ 13.2 — ordering row.** `POST /session/rename`: journal fsynced, then two-phase moves, then the
database rewritten to the new names, then the journal deleted; on interruption, recovery on the next
open reunites every rating with its file from the journal, before any scan.

**§ 13.4 — what survives a restart.** Add a row: "a rename interrupted by a restart → **the ratings
survive**: the journal in the folder is found on the next open and reunites every rating with its
file before anything scans (§ 10.16). The filenames may be left mixed; the ratings are not lost." This
is a strict improvement over the general in-flight gap and should be said to be one.

**§ 14 — flip the feature.** `"rename": false` → `true`; delete "there is no build in which it is
`true`"; add "`rename` is `true` when the server exposes the `/session/rename` operation (§ 10.16)."
Update both `/ping` examples.

**§ 16 — known gaps.** Replace any implication that a mid-rename crash is unrecoverable: it is
recoverable for the ratings (the journal). The residual, minor gap: a rename interrupted by a crash
may leave filenames mixed until re-run — untidy, never lossy.

**§ 5 — three error codes.** Add:

> | `rename_in_progress` | 409 | a mutating `/session*` call, or a second `POST /session/rename`, while a rename runs | `{ "operationId": string }` |
> | `no_rename_operation` | 404 | `GET`/cancel of `/session/rename` with a session open but no rename recorded | — |
> | `rename_failed` | 500 | the rename (or recovery on open) could not reunite the ratings | `{ "reunited": bool, "journal": string\|null }` |

`reunited: true` means the ratings are safe (folder possibly half-renamed); `reunited: false` is the
rare double fault — the reunite `Save` itself failed (e.g. disk full), the journal is left at
`journal` and the next open will retry recovery. `POST /session` runs recovery first and, if it
cannot reunite, returns `rename_failed { reunited:false, journal }` and does **not** open the session
(so nothing scans-and-drops before the ratings are safe).

**`SERVER_PLAN.md`.** Remove the top-of-file "Rename-by-rank is deliberately not exposed"; in § 4
delete "Deliberately absent: rename …" and add the journalled `/session/rename` operation; in § 7's
table change the rename row to "On the server, journalled, no file backup — owner decisions
2026-09-16". Record it as a deliberate scope reversal.

**`SPEC.md`.** The scoping note of § 3.8 at § Rename by rank and § 66; keep § 40. No other change.

---

## 5. `openapi.yaml` changes

`OpenApiContractTests` checks the document parses and that documented and served routes and types
match — it is load-bearing. Make all of:

1. **Three paths** — `/session/rename` (`post` → `202` `RenameOperation`, `get` → `200`
   `RenameOperation`) and `/session/rename/cancel` (`post` → `200` `RenameOperation`), each with the
   error responses (`400/401/404/409 rename_in_progress/413/415/500 rename_failed/503`). The `post`
   descriptions state no file backup, `202` is not a completion, and cancel stops-and-reunites.
2. **`RenameOperation`, `RenameState`, `RenamePhase`** schemas (`additionalProperties:false`, every
   field required incl. nullable `error`), and a `RenameStartRequest` (`{ clientRequestId? }`).
3. **`ErrorCode` enum**: add `rename_in_progress`, `no_rename_operation`, `rename_failed`.
4. **`ActionType` enum**: add `rename`.
5. **`PingFeatures.rename`**: `const:false` → `const:true`; rewrite description; update both `/ping`
   examples to `rename: true`.
6. **Top-of-file `description`**: rewrite the "Deliberately absent: rename" note to name the operation
   and say it copies no files; keep only "no *other* rename route".

`OpenApiContractTests`: add `[InlineData("/session/rename")]` and `[InlineData("/session/rename/cancel")]`
to the route theory. The snapshot/lastAction shape tests need no change (`rename` is a `type` value,
not a key). Add a `RenameOperation` shape test mirroring the existing ones.

---

## 6. Which existing tests change, and to what

Reversed-rule assertions are deleted or flipped; standing-rule assertions are kept. Named per line.

- **`PingContractTests.The_absent_features_are_absent_and_not_configurable`**: `Assert.False(rename)`
  → `Assert.True(rename)`; delete "There is no build in which rename-by-rank exists." **Reversed rule.**
  Leave the four other feature asserts (they stand). `PingFeatureKeys` unchanged.
- **`TransportTests.There_is_no_rename_route`**: **remove `[InlineData("/session/rename")]`** (now a
  real route — reversed rule). **Keep `/rename`, `/library/rename-by-rank`, `/debug/rename`** (that
  rule stands). Do not add `/session/rename/cancel` to this negative theory.
- **`ErrorCodeTableTests`**: `Every_code_has_exactly_the_forty…` → **43** and rename it; add the three
  spot-check `[InlineData]`s. The agreement tests pass once the three codes are in `SERVER_SPEC.md`
  § 5, the `openapi.yaml` enum, `Contracts/ApiError.cs` (`ErrorCodes`), and `Harness/ErrorStatuses.cs`.
- **`android/…/net/Rm2Fixtures.kt`** (~line 238): ping fixture `"rename": false` → `true`.

**New server tests — `RenameTests.cs`** (in `Rm2ServerCollection`, real HTTP). Determinism: a small
folder renames faster than a poll, so the wire-lifecycle tests inject an `ICatalog` (the registry
already takes one from DI) whose final `Save` blocks on a signal, to hold the run in `saving`:

1. `Rename_starts_with_202_and_an_operation` — `202`, `state == "running"`, `Location` set.
2. `The_operation_is_observed_to_succeed` — with the blocking catalog, `GET /session/rename` shows a
   non-terminal state; release; poll to `succeeded`/`done`.
3. `A_succeeded_rename_renumbers_and_carries_the_ratings` — files `000001.ext …` on disk; the DB keys
   equal the `filename`s equal those names; every rating/match/impression/lastPlayed present on the
   new ids; **no `rankmaster_backup_*` folder was created**; `GET /session` shows `pairSeq` +1, new
   pair ids, `lastAction.type == "rename"`, `undoAvailable == false`; old ids `GET` `404`; a stale
   pre-rename token on `vote` → `409 stale_pair_token`.
4. `A_second_rename_or_a_vote_while_one_runs_is_rename_in_progress` — held in `saving`, both `409
   rename_in_progress`.
5. `Cancel_before_the_moves_aborts_cleanly` — cancel while held in `preparing` (inject a block in the
   plan/journal step) → `cancelled`, folder and DB original, no journal left.
6. `Cancel_is_idempotent`; `Rename_routes_need_a_session` (`404 no_session` / `404 no_rename_operation`);
   `Rename_takes_no_pairToken`; `Rename_route_is_reachable`.

**New unit/crash-matrix tests — `RankMaster2.Catalog.Tests` (or a new `RankMaster2.Actions.Tests`)**,
where interruption is deterministic without a real kill. **This is the gate that matters** (§ 8):

7. `Every_rating_finds_its_file_after_a_crash_at_each_step` — a `[Theory]` over the interruption
   points of § 3.3 (after journal fsync; mid phase 1; mid phase 2; after all moves before the DB
   save; after the DB save before the journal delete). For each: drive the executor to that point and
   stop (leaving the on-disk state); run `RecoverIfPresent(folder)`; then assert **every original
   rating is present in the database under the file that carries it** — matched through the plan — and
   no rating was dropped except for a file deliberately deleted. Directly encodes the owner's test.
8. `Recovery_runs_before_scan_and_does_not_lose_ratings_to_the_merge` — construct the dangerous state
   by hand (files at `000001…`, DB with old keys, journal present); prove a plain `Scan` would lose
   the ratings, and that `RecoverIfPresent` then `Scan` does not.
9. `Cancel_in_place_leaves_a_valid_half_renamed_folder` — stop mid-`renaming`, reunite in place;
   assert mixed names on disk and every rating keyed to its current name.
10. `The_journal_is_ignored_by_scan_and_carries_the_ratings`; `Deterministic_temps_are_identifiable`.
11. `The_desktop_RenameByRank_is_unchanged` — the parameterless backup-based method still behaves
    exactly as the existing `FileOpsTests` pin.

**State-machine audit** (`tests/RankMaster2.Audit.StateMachine/`, later re-derived by `rm2-audit-state`):

12. `RenameFailureReunitesAndKeepsRatings` — `AuditFolder…JamSave()` so the final `Save` throws; the
    op fails, reunite runs (or, if reunite also throws, the journal is left); assert `rename_failed`
    with `reunited` set truthfully and that reopening the folder reunites every rating.
13. `RenameHoldsTheSessionAgainstOtherActions` — a concurrent mutation while held → `409
    rename_in_progress`.
14. `RenameClearsUndo` — discard then rename to success → `undoAvailable == false`.

**Compatibility audit** (`RoundTripTests.cs`, `rm2-audit-compat`):

15. `A_renamed_library_still_loads_in_the_desktop_app` — open, vote, rename to success, close; then
    `new JsonCatalog().Scan(folder)` returns the same count with keys `000001.ext …`, every rating
    intact, `Db.RequireV1Schema` passes, keys equal `filename`s.

---

## 7. The `rm2ctl` change — the acceptance gate for the whole server

- **`UnhappyPathsAsync`, the "rename that does not exist" step (~line 714):** rename to "the rename
  routes that do not exist"; **drop `/session/rename` from the loop** (now real); keep `/rename` and
  `/library/rename-by-rank`, which still 404.
- **A new happy-path step, driven last** (after `SaveAsync`, before `CloseAsync`, since it renumbers):
  `RenameAsync`. Record the pre-rename ids and ratings (`GET /media/{id}/meta`); `POST /session/rename`
  with a `clientRequestId` → `202` + `running`; **poll `GET /session/rename`** to a terminal state,
  asserting the phase only advances (`preparing → renaming → saving → done`) and reaches `succeeded`;
  `GET /session` → `pairSeq +1`, new token, `lastAction.type == "rename"`, `undoAvailable == false`;
  assert new ids match `^\d{6}\.`, old ids `GET` `404`, **no `rankmaster_backup_*` folder was created**,
  and the on-disk JSON still loads with every rating intact. Add `Rm2Api.StartRename/GetRename/CancelRename`
  and teach `src/rm2ctl/Snapshot.cs` the `rename` `lastAction.type`. Cancel is not exercised in the
  cycle (it needs a large folder to catch); it is proven by the deterministic tests (§ 6 items 5, 9).

`rm2ctl cycle` green — starting, observing to completion, and verifying a rename that lost no
rating — is the server's acceptance gate, and it runs with the desktop app never launched.

---

## 8. Acceptance gate

Rename-by-rank on the server is correct — per owner decision 4, **when no rating has been lost, not
when every file has the name it should** — when, on this Linux box:

1. `dotnet build RankMaster2.Server.slnf` succeeds and the whole suite passes, including the flipped
   ping/transport/error-count/OpenApi tests. The only deletions are the reversed-rule assertions of
   § 6.
2. **The crash-matrix test (§ 6 item 7) is green:** kill the operation at each step of § 3.3, and
   after recovery every rating still finds its file. This is the test that matters.
3. A test proves recovery runs before any scan and that the plain `Scan` merge would otherwise lose
   the ratings (§ 6 item 8) — the catastrophe of § 1 is demonstrably prevented.
4. `rm2ctl cycle` starts a rename, polls it to `succeeded`, and verifies the renumber carried every
   rating, created **no backup folder**, and left a loadable database.
5. A test proves a mutation during a rename gets `409 rename_in_progress`, and a jammed-save failure
   reunites the ratings (§ 6 items 4, 12).
6. The compatibility audit proves a renamed library loads through `JsonCatalog.Scan` with ratings
   intact and keys `000001.ext …`.
7. `SERVER_SPEC.md`, `SERVER_PLAN.md`, `SPEC.md` (scoping note) and `openapi.yaml` agree with the
   server on the three routes, three codes, the `rename` action type, the `202`, the journal, and
   `features.rename == true`; the contract-triangle tests enforce it.

---

## 9. What is verifiable here, and what is not

**Fully verifiable on this build machine.** Rename is filesystem plus JSON — no WPF, no native media.
The journal, the deterministic temporaries, the forward recovery, and the crash matrix are all
directory-and-file logic, driven and asserted deterministically in `RankMaster2.Catalog.Tests`
without a real process kill (drive to a step, stop, recover, assert). The server, its 500-plus tests,
`rm2ctl` and both audit suites build and run on Linux today (`SERVER_PLAN.md` § 8). The one thing to
be honest about is timing (§ 3.1): the operation is near-instant for any real folder, so the progress
bar and cancel are exercised at the seam (injected blocks) rather than by a genuinely slow run.

**Not verifiable here.** The PC client's bar, confirmation and cancel button, and the phone hiding the
option (client work — `PC_CLIENT_PLAN.md` § 6.6, `pc/plans/E-ranking-surface.md` § E6). A real
process kill on real hardware mid-rename (simulated deterministically instead, which is stronger — it
covers every step, not one lucky moment). The Windows-only edge of a media `GET` holding a read handle
over a `File.Move` (a no-op on Linux; `MoveWithRetry` absorbs it).

---

## 10. For the coordinator — decisions this plan made that touch a shared document

- **Reverses `SERVER_SPEC.md` § 1.1 and `SERVER_PLAN.md` § 4/§ 7**, adds the **first `202` and first
  long-running operation** (named as such in § 13.1), and sets the precedent for any future long
  operation: a separate small resource with start/observe/cancel, `/session` keeping its one shape.
- **No file backup; a journal is the safety mechanism** (owner decision 3), and the whole crash design
  serves "the database is the asset" (owner decision 4). Recovery is forward and automatic; a
  half-renamed folder is a success as long as every rating found its file.
- **`SPEC.md` conflict resolved by scoping, not rewriting** (§ 3.8): the desktop steps 2/6 and § 66
  are marked as the frozen app's; the server's normative rename is `SERVER_SPEC.md` § 10.16; the
  desktop `RenameByRank()` is untouched, because it cannot be built or tested here. If the coordinator
  would rather converge the app onto the journal path too, that is a separate, Windows-gated change.
- **`PC_CLIENT_PLAN.md` § 6.6 now needs reconciling** — it plans a *local* rename in the PC client via
  the shared `RenameByRank()`, written before rename moved to the server. With the server owning
  rename, the PC client should call `POST /session/rename` and drop its local path; the shared
  `RenameByRank()` then has only the frozen desktop app as its caller. Flagged for the coordinator;
  not changed here.
- **`SPEC.md` § 40's backup-folder scan exclusion is kept** even though the server creates none — old
  folders from past desktop renames must stay ignored.
- **Three new error codes** and a `rename` action type; the `ICatalog` DI seam is reused to make the
  wire lifecycle deterministic in tests.
