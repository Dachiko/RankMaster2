# Part F — rename by rank, on the server

**Status: plan, nothing built.** Reverses a pinned product decision and adds the first long-running
operation in the contract. Owner decisions: rename by rank survives into the new world and lives
**on the server** (2026-09-16); and rename **must show a progress bar and offer a cancel button**
(2026-09-16, later the same day). The second decision settles the sync-versus-polled question in the
brief: a single synchronous request that returns only when finished can neither report progress nor
be stopped, so it is off the table. Rename is a **started / observed / cancellable** operation.

Owns edits in `src/RankMaster2.Server/Sessions/`, additive overloads in `src/RankMaster2.Actions/`
and `src/RankMaster2.Catalog/`, `SERVER_SPEC.md`, `SERVER_PLAN.md`, `openapi.yaml`,
`tests/RankMaster2.Server.Tests/`, the two affected audit suites, `tests/RankMaster2.Catalog.Tests`,
and `src/rm2ctl/`.

**The project's rule is spec first.** The contract changes and is reviewed (Phase 1), then it is
implemented (Phase 2+). A weak executor should not re-decide anything below.

Read in this order: `SPEC.md` § Rename by rank, § File actions, § Persistence; `SERVER_SPEC.md`
§ 1.1, § 6, § 7 (all, especially § 7.4), § 8, § 9, § 10.1, § 10.8, § 10.10, § 11, § 12.2, § 12.5,
§ 13, § 14, § 16; `src/RankMaster2.Actions/LibraryActions.cs` (`RenameByRank` and its
restore-on-failure path); `src/RankMaster2.Ranking/RankingSession.cs` (`ReplaceAll`, and what it
clears); `src/RankMaster2.Server/Sessions/SessionRegistry.cs` (`MoveAsync`, `WithLockAsync`,
`CurrentForMedia` — the last is how a read runs without the session lock); `SessionEndpoints.cs`;
`src/RankMaster2.Catalog/FileOps.cs` (`BackupLibrary`, `RestoreLibrary`, `RenameByConservativeScore`).

---

## 1. Decisions, settled before any code

| Question | Decision | Why |
|---|---|---|
| Behaviour source | **`LibraryActions.RenameByRank`, extended, not reimplemented** — a new overload threads progress + cancellation through the same scan / backup / two-phase rename / remap / save / restore-on-failure | `SPEC.md` § Rename by rank does not change; progress and cancel are observability and an interruption point, not new behaviour. The desktop's parameterless `RenameByRank()` is left exactly as it is |
| Shape on the wire | **A small operation resource, three routes**: `POST /session/rename` starts (returns `202`), `GET /session/rename` observes, `POST /session/rename/cancel` stops. Not a `SessionSnapshot` while it runs | § 2.5. The first long-running operation in the contract; kept as close to § 10 as the requirement allows — `/session` keeps its one snapshot shape, the operation is a separate resource |
| What cancel does | **Rolls the library all the way back to the original filenames** — cancel is the restore-from-backup path, the same one a failure takes | § 2.1. There is no consistent place to "stop where it is": mid-two-phase-rename the folder is a mix of original, temp and numeric names and the JSON is untouched — the forbidden disagreement. Rollback is the only consistent terminus |
| Who owns the run | **The server, not the request.** The op runs to a consistent terminus even if the client disconnects; a disconnect is not a cancel; cancel is explicit | § 2.4 |
| Progress unit | **Files, with named phases**: `backing_up`, `renaming` (of `2 × N` moves), then `saving` (a short labelled phase, not on the file bar), and `restoring` on cancel/failure | § 2.3. A bar that sits at 100 % while a database writes is a bar that lies; the `saving` phase is why it does not |
| What runs it to the end | **On success it holds the session lock only briefly, at the start and at the final apply**; the long file work runs off the lock, observed lock-free like `CurrentForMedia` | § 2.5 |
| Concurrency | **Nothing else mutates the session while a rename runs**: a mutating `/session*` call gets `409 rename_in_progress`. Reads and the status poll are unaffected | § 2.5 |
| `features.rename` in `/ping` | **`true`** | § 2.6 |
| New error codes | **`rename_in_progress` (409), `rename_failed` (500), `no_rename_operation` (404)** | § 3, § 2.1 |
| Who may offer it | **PC client: yes. Phone: no** (a recommendation to the client authors; the endpoint itself is open to any token) | § 2.6 |

### 1.1 Deliberately absent

- **No "stop cleanly where it is."** Cancel restores originals; there is no half-renamed terminus
  (§ 2.1).
- **No undo of a completed rename.** The backup folder is the only way back, exactly as on the
  desktop; `undoAvailable` is `false` in the post-rename snapshot.
- **No `rename 2`, no options, no pattern, no dry run.** `μ − 3σ` descending, then filename, to
  `000001.ext` — fixed by `SPEC.md`.
- **No action journal, so no auto-recovery from a mid-rename server kill.** Same scope line as
  § 13.4 / § 16.1: the backup on disk is the manual recovery (§ 2.4).
- **No change to ranking behaviour.** The overloads added to `Actions`/`Catalog` are additive and
  behaviour-preserving for the desktop; if anything about the *result* of a rename would change,
  `SPEC.md` changes first — it does not here.
- **No client UI in this part.** Whether and how a client draws the bar and the cancel button lives
  in `PC_CLIENT_PLAN.md` § 6.6 and `pc/plans/E-ranking-surface.md` § E6.

---

## 2. The hard problems, and their answers

### 2.1 What "cancel" means with three thousand files already renamed

This is the whole problem, and it has exactly one safe answer: **cancel rolls the library back to its
original filenames.** The owner must be told this *before* he presses it — the button reads "Cancel
and restore original names," not a bare "Cancel," and the contract states plainly that cancel is a
rollback so the client can say so.

Why "stop cleanly where it is" is rejected: `FileOps.RenameByConservativeScore` is two-phase — every
file is first moved to a temp name `__rm2_<guid>.ext`, then from the temp name to `000001.ext …` —
and `rankmaster_db.json` is **not** rewritten until every move has completed. At any moment mid-run
the folder holds a mix of original names, meaningless temp GUIDs, and final numeric names, while the
JSON still lists the original names. That *is* the disagreement between files and database the brief
says must remain impossible. There is no coherent point to stop at.

`RenameByRank` already backs the library up before touching a file and, on any exception, runs
`RestoreLibrary` (copy the backup's files back, delete every top-level file not in the backup — the
JSON included, since `BackupLibrary` copies it too). Cancel reuses that path exactly: the
cancellation token, checked between file moves, throws `OperationCanceledException` at the next
check, which triggers the same restore a failure would. **Cancel and failure share one
implementation and one guarantee:** the terminus is the original, fully-consistent library.

The one outcome that must remain impossible — files and JSON disagreeing with no way back — cannot
occur while the restore succeeds, and if the restore itself throws (the double fault) the backup
folder is intact and its path is handed to the caller. That is the "way back."

### 2.2 Cancel is not instant either

Rolling back thousands of files is itself thousands of file moves. So cancel has its **own** phase
with its own progress. The operation's state becomes `cancelling` and its phase becomes `restoring`,
carrying `done`/`total` counted in files put back. Between pressing cancel and it being over the
owner sees "Restoring original names… 1,240 of 3,000," a live bar, not a frozen screen. The cancel
button is one-shot: once pressed it disables, and a second `POST /session/rename/cancel` is
idempotent — it returns `200` with the operation already `cancelling`/`cancelled`, never a second
rollback and never an error.

### 2.3 What progress is measured in

Files, in named phases, so the bar never lies. The pre-rename scan fixes `N` = the media file count.

| Phase | `done` / `total` | On the bar? |
|---|---|---|
| `backing_up` | files copied into `rankmaster_backup_<ts>/` of the backup's file count (≈ `N`) | yes |
| `renaming` | moves completed of `2 × N` (both passes counted, so the bar advances continuously through the temp pass and the final pass) | yes |
| `saving` | `RemapIds` + the final `JsonCatalog.Save` — one write | **no**: shown as a labelled "Saving database…" step, not a file bar. This is the fix for a bar stuck at 100 % while the JSON writes |
| `restoring` | files restored of the backup's file count | yes (its own bar, § 2.2) |

The backup copy and the file renames are **inside** the bar; the final database write is **outside**
it and labelled. The client shows the phase label plus the bar; `saving` and `restoring` carry
labels of their own.

### 2.4 Who may cancel, and what happens if nobody is watching

The operation is owned by the **server**, not by the HTTP request that started it. It runs to a
consistent terminus — fully renamed, or fully restored — regardless of who is listening.

- **A client disconnect is not a cancel.** An accidental Wi-Fi drop must not silently undo the
  owner's rename. If the starting client dies mid-run, the rename continues to completion (fully
  renamed). If the owner had already pressed cancel, the rollback continues to completion (fully
  restored). Either way the folder ends consistent.
- **Cancel is an explicit act**, a separate request against the running operation, available to any
  paired device (same token gate). It is honoured at the next between-files checkpoint.
- **What must never happen is the operation halting mid-flight because nobody is listening.** That
  is why the run is detached from the request and why cancel is explicit rather than implied by a
  disconnect.
- **A server process kill mid-run** is the one unrecoverable case, and it is already scoped: session
  state does not survive a restart (§ 13.4), there is no action journal (§ 16.1, extended here), and
  the backup folder on disk is the manual recovery. The plan records this; it does not solve it.

### 2.5 The shape on the wire — the first long-running operation

Kept as close to § 10 as the requirement allows. `/session` keeps its single `SessionSnapshot`
shape; the rename is a **separate, small resource** with three routes.

```
POST   /session/rename          start        -> 202 Accepted + RenameOperation (running)
GET    /session/rename           observe      -> 200 RenameOperation (poll for the bar)
POST   /session/rename/cancel    cancel       -> 200 RenameOperation (cancelling / terminal)
```

`RenameOperation` (the only new wire object):

| Field | Type | Meaning |
|---|---|---|
| `operationId` | string | opaque, per run; new on every start |
| `state` | enum | `running` \| `cancelling` \| `succeeded` \| `cancelled` \| `failed` |
| `phase` | enum | `backing_up` \| `renaming` \| `saving` \| `restoring` \| `done` |
| `done` | integer | units completed in the current phase |
| `total` | integer | units in the current phase |
| `startedAt` | string | RFC 3339 UTC |
| `updatedAt` | string | RFC 3339 UTC, moves as the phase advances |
| `error` | object \| null | on `failed`: `{ code: "rename_failed", restored: bool, backup: string\|null }`; else `null` |

Lifecycle:

- **`POST /session/rename`** — under the session lock: `404 no_session` if closed; `409
  rename_in_progress` if one is already running; otherwise record `N`, create the operation
  (`running`, `backing_up`, `0/N`) and a cancellation source, mark the session rename-in-progress,
  **start the run on a server task, release the lock, and return `202 Accepted`** with the operation
  and `Location: /api/v1/session/rename`. The `202` is an acknowledgement, **not** a completion.
- **`GET /session/rename`** — reads the operation **without taking the session lock** (the same
  lock-free discipline `CurrentForMedia` uses), so the poll never blocks behind the run. `404
  no_session` if closed; `404 no_rename_operation` if a session is open but no rename has run.
- **`POST /session/rename/cancel`** — signals the cancellation source; returns `200` with the
  operation. Idempotent (§ 2.2). `404 no_session` / `404 no_rename_operation` as above. Cancelling
  after the run has already succeeded returns the `succeeded` operation unchanged (too late — the
  rename is done; the owner's recourse is the backup).
- **On `succeeded`** the client fetches the new session with `GET /session`: the session was
  `ReplaceAll`'d, `pairSeq` advanced, `lastAction.type == "rename"`. On `cancelled` or `failed` the
  session is unchanged and the client simply resumes.

**This is the first exception to two standing conventions, made deliberately and named as such in the
spec:** "every endpoint is synchronous" (§ 10) and "a 2xx means the change is durable" (§ 13.1). A
`202` claims no durable change; durability is asserted only when `GET /session/rename` reports
`succeeded`, by which point both saves have fsynced and `ReplaceAll` has run. Until then the folder
is explicitly mid-flight. Everything else about the operation stays inside convention: one error
envelope (§ 4), stable codes (§ 5), the session's own shape untouched.

**Locking, precisely.** The run cannot hold the `SemaphoreSlim` for minutes, or the status poll and
every read would block on it. So: the start takes the lock briefly to create the operation and set
the flag; the long file work runs off the lock on the server task; the status GET is lock-free; and
the **final apply** (`RankingSession.ReplaceAll(remapped)`, `ClearLastMove()`, `PairSeq++`, set
`lastAction`, clear the flag) re-takes the lock briefly, because it mutates the session and must be
serialised. Because the flag is set, any mutating `/session*` call that takes the lock while the run
is off it returns `409 rename_in_progress`; a media `GET` (lock-free) sees files mid-move and answers
`404 media_file_missing`, and afterwards `404 unknown_media_id` for the old ids (§ 11.3) — never
corruption, because a `GET` never mutates.

### 2.6 `/ping`, and who offers it

`features.rename` becomes **`true`** and stays a fixed capability (§ 14). A client reads it to decide
whether to show the option; a server predating this part answers `false` and the client hides it.

**Availability is a server question and this plan answers it:** the endpoint exists and is open to
any paired token, because there is one user and pairing is the trust boundary. **The offering is a
client question, and here the recommendation is PC yes, phone no** — a firmer "no" than the sync
design would have needed, and argued as the owner asked:

- Rename is a deliberate library-maintenance operation, run on the machine where the library lives.
- It is long, destructive, and unundoable except by the backup.
- The owner just asked for a bar he can watch and a button he can press. **A phone is the worst place
  to provide either:** start a rename and walk out of Wi-Fi range and you can neither watch the bar
  nor cancel — and because the run is server-owned (§ 2.4), it completes without you. The PC client,
  sitting in front of the library, is where watch-and-cancel actually works.

So the PC client offers rename (E6), behind the one confirmation `SPEC.md` allows in the whole
program; the phone does not. The server exposes it either way.

---

## 3. The exact spec text to add to `SERVER_SPEC.md`

Phase 1 makes these edits and nothing else. Wording is normative; keep the house voice.

**§ 1.1 — replace the first bullet in full.** Delete "Rename by rank. There is no rename endpoint …".
Add in its place:

> - **Rename by rank is a long-running operation at `/session/rename` (§ 10.16)** — `POST` to start,
>   `GET` to observe, `POST /session/rename/cancel` to stop — and nowhere else. There is no other
>   rename route, no flag, no config key, no debug build. A request to any *other* path containing
>   `rename` MUST fall through to the normal `404 not_found`.

**§ 10.16 — new subsection, appended after § 10.15** (appended, not inserted, so §§ 11–16 keep their
numbers). Its content is § 2 of this plan written as normative spec, and MUST cover, in order:

1. **Purpose and behaviour.** The server side of `SPEC.md` § Rename by rank: back the top-level
   library and its JSON up into `<folder>/rankmaster_backup_<yyyyMMdd_HHmmss>/`; two-phase rename
   every media file to `000001.ext …` by `μ − 3σ` descending, then filename for ties; rewrite the
   JSON so every key and `filename` is the new name, ratings unchanged; then `ReplaceAll` the open
   session with the renamed records. Authenticated like every `/session*` route. Takes no
   `pairToken`; one in the body MUST be ignored (as `undo`, § 10.10).
2. **It is a started / observed / cancelled operation, not a single request** — with the three routes
   and the `RenameOperation` shape of § 2.5, verbatim including the phase and state enums and the
   `error` object.
3. **Start** (`POST /session/rename`): body `{ clientRequestId?: string ≤64 }`, optional; `404
   no_session`; `409 rename_in_progress` if one already runs; otherwise `202 Accepted` +
   `RenameOperation` (`running`), `Location: /api/v1/session/rename`. The `202` is an
   acknowledgement, not a completion (see § 13.1).
4. **Observe** (`GET /session/rename`): `200` + the current `RenameOperation`, read without the
   session lock; `404 no_session`; `404 no_rename_operation` when a session is open but none has run.
5. **Cancel** (`POST /session/rename/cancel`): **cancel rolls the library back to the original
   filenames** — it is the restore path, and the client MUST tell the owner so before he presses it.
   `200` + the `RenameOperation` (now `cancelling`, phase `restoring`); idempotent; a cancel after
   success returns the `succeeded` operation unchanged.
6. **Progress** is measured as § 2.3: `backing_up` and `renaming` (`2 × N` moves) on the bar,
   `saving` a short labelled step off the bar, `restoring` its own bar on cancel/failure.
7. **On success, the effect on the session** is identical to any advance whose token went stale, and
   is described exactly as the old § 10.16 draft did: same `sessionId`, same folder, same lock;
   `pairSeq` +1; a freshly picked pair whose every id is new; `cues` emptied, engine undo cleared,
   move-undo cleared so `undoAvailable` is `false`; `sessionVotes`, `counts`, `progress` unchanged in
   value; `lastAction` a `rename` record (`type:"rename"`, `pairToken:null`, `clientRequestId`
   echoed, every other field null). A client fetches it with `GET /session`. An in-flight action
   arriving after success is answered `409 stale_pair_token` with the new snapshot (§ 8.5) and MUST
   NOT be replayed; every cached media URL now `404`s and MUST be refreshed from the snapshot
   (§ 11.3).
8. **The three termini** (`succeeded`, `cancelled`, `failed`) and their disk/session state:

   | Terminus | Folder | Session | `RenameOperation` |
   |---|---|---|---|
   | `succeeded` | fully renamed; JSON matches | `ReplaceAll`'d; `pairSeq` +1; `lastAction` rename | `state: succeeded`, `phase: done`, `error: null` |
   | `cancelled` | restored to originals | **unchanged**; `pairSeq` unmoved; token still valid | `state: cancelled`, `error: null` |
   | `failed`, restore ok | restored to originals | **unchanged** | `state: failed`, `error: { code:"rename_failed", restored:true, backup }` |
   | `failed`, restore threw (double fault) | inconsistent; backup intact at `backup` | the server closes the session and releases the lock | `state: failed`, `error: { code:"rename_failed", restored:false, backup }` |

9. **Ownership and disconnect** (§ 2.4): the run is the server's, not the request's; it reaches a
   consistent terminus regardless of who watches; a disconnect is not a cancel; cancel is explicit; a
   process kill mid-run leaves the folder mid-flight with the backup for manual recovery (§ 16).
10. **Concurrency** (§ 2.5): while a rename runs, a mutating `/session*` call gets `409
    rename_in_progress`; reads and the status poll are unaffected; media `GET`s answer `404` for ids
    caught by the rename.

**§ 6 — status codes.** Add a `202` row: "`POST /session/rename` accepted a rename to run in the
background (§ 10.16). It is not a completion; durability is asserted by the operation reaching
`succeeded`." Note `409` now also covers `rename_in_progress` and `404` also covers
`no_rename_operation`; `500` also covers `rename_failed`.

**§ 7.2 — transition rows.** Add: rename started (session unchanged, a run begins);
rename succeeded (`+1`, `ReplaceAll`, every id new); rename cancelled or failed-restored (unchanged).

**§ 8.3 — `pairSeq` table.** Add: "rename succeeds → +1 → stale, every id new"; "rename cancelled or
failed → unchanged → still valid". Extend the reset note: a rename does not reset `pairSeq` and does
not change `sessionId`.

**§ 9.4 — `type` enum.** Extend to include `rename` (`pairToken`/`winner`/`side`/`id`/`restoredId`/
`undoneType` all null); note it can appear at a `pairSeq` reached by a rename.

**§ 12.5 — known limit.** Add the sentence that a rename reassigns names to `000001.ext …`, so across
two renames a numeric id can reuse an ETag — the same `(name, size, mtime)` limit; within one rename
it cannot bite, the new numeric ids never matching a client's pre-rename cache keys.

**§ 13.1 — the invariant gets its first, named exception.** Add: "The one exception is
`POST /session/rename` (§ 10.16), which returns `202` before the change is durable, because the owner
requires a progress bar and a cancel button and a synchronous request can provide neither. Durability
for a rename is asserted only when its operation reaches `succeeded`; until then the folder is
explicitly mid-flight, and a `202` claims nothing durable."

**§ 13.2 — ordering row.** `POST /session/rename`: backup, then two-phase rename, then JSON rewrite,
then `ReplaceAll` — or, on cancel/failure, files and JSON restored from the backup; committed only at
`succeeded`.

**§ 13.3 — retry row.** `POST /session/rename`: not blindly retriable; a client that loses the
connection polls `GET /session/rename` — the operation is server-owned and still running or terminal;
never start a second while one runs (`409 rename_in_progress`).

**§ 14 — flip the feature.** `"rename": false` → `"rename": true`; delete "there is no build in which
it is `true`"; replace with "`rename` is `true` when the server exposes the `/session/rename`
operation (§ 10.16)." Update both `/ping` examples.

**§ 16 — known gaps.** Add: "`POST /session/rename` has no progress ceiling the server enforces, and
a server kill mid-run leaves the folder mid-flight — the backup folder is the manual recovery; an
action journal (gap 1) would let the server resolve it, and is out of scope."

**§ 5 — three error codes.** Add:

> | `rename_in_progress` | 409 | a mutating `/session*` call, or a second `POST /session/rename`, while a rename runs | `{ "operationId": string }` |
> | `no_rename_operation` | 404 | `GET`/cancel of `/session/rename` with a session open but no rename recorded | — |
> | `rename_failed` | 500 | the rename threw | `{ "restored": bool, "backup": string\|null }` |

**`SERVER_PLAN.md` — the scope owner changes too.** Remove the top-of-file "Rename-by-rank is
deliberately not exposed." In § 4 delete "Deliberately absent: rename …" and add the `/session/rename`
operation to the session block, noting it is the one long-running operation. In § 7's table change
the rename row to "On the server, started/observed/cancellable `/session/rename` — owner decisions
2026-09-16". Record it as a deliberate scope reversal.

---

## 4. `openapi.yaml` changes

`tests/RankMaster2.Server.Tests/OpenApiContractTests.cs` checks that the document parses and that
documented routes and served routes and types match — it is load-bearing. Make all of:

1. **Three paths.** `/session/rename` with `post` (`operationId: startRename`, `202` →
   `RenameOperation`, plus `400/401/404/409 (rename_in_progress)/413/415/503/default`) and `get`
   (`operationId: getRenameOperation`, `200` → `RenameOperation`, `401/404`); `/session/rename/cancel`
   with `post` (`operationId: cancelRename`, `200` → `RenameOperation`, `401/404`). Each `post`
   description states cancel = rollback, that `202` is not a completion, and that the run is
   server-owned.
2. **`RenameOperation` schema** (`additionalProperties:false`, every field required incl. nullable
   `error`), the `RenameState` and `RenamePhase` enums, and a `RenameStartRequest`
   (`{ clientRequestId? }`).
3. **`ErrorCode` enum**: add `rename_in_progress`, `no_rename_operation`, `rename_failed`.
4. **`ActionType` enum**: add `rename`.
5. **`PingFeatures.rename`**: `const:false` → `const:true`, rewrite its description, update **both**
   `/ping` examples to `rename: true`.
6. **Top-of-file `description`**: rewrite the "Deliberately absent: rename" note to name the operation
   and keep only "no *other* rename route".

`OpenApiContractTests`: add `[InlineData("/session/rename")]` and `[InlineData("/session/rename/cancel")]`
to `Every_route_the_server_maps_is_in_the_document`. The `SessionSnapshot`/`LastAction`/`Counts`/
`MediaRef` shape tests need no change (rename adds no snapshot field; `rename` is a `type` *value*).
Add a `RenameOperation` shape test mirroring the others if the suite gains a served
`RenameOperation` record.

---

## 5. Exactly which existing tests must change, and to what

Deleting an assertion that pins a reversed rule is correct; deleting one that pins a standing rule is
not. The difference is named on every line.

- **`PingContractTests.The_absent_features_are_absent_and_not_configurable`**: `Assert.False(...rename...)`
  → `Assert.True(...)`, delete "There is no build in which rename-by-rank exists." **Reversed rule —
  change it.** The four other asserts pin standing rules — leave them. `PingFeatureKeys` unchanged
  (the key still exists).
- **`TransportTests.There_is_no_rename_route`**: **remove `[InlineData("/session/rename")]`** — now a
  real route; asserting it 404s pins a reversed rule. **Keep `/rename`, `/library/rename-by-rank`,
  `/debug/rename`** — that rule stands. (`/session/rename/cancel` is also real; do not add it to this
  negative theory.)
- **`ErrorCodeTableTests`**: `Every_code_has_exactly_the_forty_the_spec_defines` → **43**, and rename
  the method. Add `[InlineData("rename_in_progress",409)] [InlineData("no_rename_operation",404)]
  [InlineData("rename_failed",500)]`. The agreement tests pass once the three codes are in
  `SERVER_SPEC.md` § 5, the `openapi.yaml` enum, `Contracts/ApiError.cs` (`ErrorCodes`), and
  `tests/RankMaster2.Server.Tests/Harness/ErrorStatuses.cs`.
- **`android/app/src/test/kotlin/com/rankmaster2/phone/net/Rm2Fixtures.kt`** (~line 238): ping
  fixture `"rename": false` → `true`, so the Android contract fixture matches the server.

**New server tests** — `tests/RankMaster2.Server.Tests/RenameTests.cs`, in `Rm2ServerCollection`,
against the real HTTP surface. Determinism is the concern: a six-file folder renames faster than a
poll, so the wire lifecycle uses an **injected catalog** that blocks the final `Save` on a signal
(the registry already accepts an `ICatalog` from DI — that is how tests inject a fake), letting the
test hold the run in the `saving` phase and observe it:

1. `Rename_starts_with_202_and_an_operation` — `POST /session/rename` → `202`,
   `RenameOperation.state == "running"`, `Location` header set.
2. `The_operation_can_be_observed_to_succeed` — with the blocking catalog, `GET /session/rename`
   shows a non-terminal state; release the block; poll to `state == "succeeded"`, `phase == "done"`.
3. `A_succeeded_rename_renumbers_the_folder_and_the_session` — after success, on disk the top-level
   files are `000001.ext …`, JSON keys equal `filename`s equal those names, a `rankmaster_backup_*`
   folder exists with the originals; `GET /session` shows `pairSeq` +1, new numeric pair ids,
   `lastAction.type == "rename"` with `clientRequestId` echoed, `undoAvailable == false`, `cues`
   empty, `sessionVotes` unchanged; the old ids `GET` `404`; a stale pre-rename `pairToken` on
   `vote` → `409 stale_pair_token` with the new snapshot.
4. `Ratings_survive_the_rename` — μ/σ/matches/impressions/lastPlayed carried onto the numeric ids.
5. `A_second_rename_while_one_runs_is_rejected` — with the run held in `saving`, a second `POST
   /session/rename` → `409 rename_in_progress`; a `vote` → `409 rename_in_progress` too.
6. `Cancel_at_the_saving_phase_restores_the_originals` — hold the run just before the final `Save`
   (the injected catalog blocks and, on the *first* `Save` attempt, signals the test then throws or
   waits); `POST /session/rename/cancel` → `200` `cancelling`; release; poll to `state ==
   "cancelled"`; on disk the **original** filenames are back and the JSON lists them; `GET /session`
   still ranks the original ids with `pairSeq` unmoved. (This proves cancel = rollback deterministically
   at the last checkpoint; the mid-file checkpoint is proven at the unit level, item 11.)
7. `Cancel_is_idempotent` — a second cancel → `200`, no second rollback, no error.
8. `Rename_routes_need_a_session` — all three routes with no session → `404 no_session`; `GET`/cancel
   with a session but no run → `404 no_rename_operation`.
9. `Rename_takes_no_pairToken` — a start body carrying a stale `pairToken` still starts.
10. `Rename_route_is_reachable` — `POST /session/rename` is not a 404 route (complements the OpenApi
    route check).

**Unit tests** — `tests/RankMaster2.Catalog.Tests` and/or a new `RankMaster2.Actions.Tests`, where
cancellation is deterministic without the HTTP race:

11. `Cancel_mid_file_restores_original_names` — call the new
    `LibraryActions.RenameByRank(progress, token)` (or the `FileOps` overload directly) with an
    `IProgress` callback that cancels the token the first time it sees a `renaming` report; assert it
    throws `OperationCanceledException`, the folder holds the **original** filenames, the JSON lists
    them, and a `rankmaster_backup_*` folder exists. This is the load-bearing proof that cancel
    restores originals part-way through, free of any timing race.
12. `Progress_reports_backup_then_rename_then_save` — the `IProgress` callback receives phases in
    order `BackingUp`, `Renaming` (with `done` climbing to `2 × N`), then `Saving`.
13. `The_desktop_RenameByRank_is_unchanged` — the parameterless `RenameByRank()` still renames and
    `ReplaceAll`s exactly as before (the existing `FileOpsTests` already cover the naming; add one
    that the parameterless overload behaves identically to the new one followed by a manual
    `ReplaceAll`).

**State-machine audit** (`tests/RankMaster2.Audit.StateMachine/`, later re-derived by
`rm2-audit-state`):

14. `RenameFailureRestoresAndChangesNothing` — `AuditFolder.SixStills().JamSave()`, start the rename,
    poll to `failed`; assert `error.code == "rename_failed"`, `error.restored == true`, `error.backup`
    names an existing backup, the top-level files are the **originals**, and `GET /session` still
    ranks the original ids with an unmoved `pairSeq` and its original token still valid. (The
    `JamSave` lever — a directory on `rankmaster_db.json.tmp` — makes the final `Save` throw while
    file moves succeed, which is exactly the restore path.)
15. `RenameHoldsTheSessionAgainstOtherActions` — while the run is held (jam or blocking catalog), a
    concurrent `vote`/`discard` → `409 rename_in_progress`.
16. `RenameClearsUndo` — discard a side (so `undoAvailable`), rename to success, then `undoAvailable
    == false` and `POST /session/undo` → `409 nothing_to_undo`.

**Compatibility audit** (`tests/RankMaster2.Audit.Compatibility/RoundTripTests.cs`, `rm2-audit-compat`):

17. `A_renamed_library_still_loads_in_the_desktop_app` — open, vote, rename to success, close; then
    `new JsonCatalog().Scan(folder)` (what the desktop reads) returns the same count with keys
    `000001.ext …`, ratings/matches/impressions/lastPlayed intact, `Db.RequireV1Schema` passes, keys
    equal `filename`s. This is the compatibility contract for the renamed file.

---

## 6. The `rm2ctl` change — the acceptance gate for the whole server

`src/rm2ctl/Cycle.cs` drives the whole contract and exits non-zero on any disagreement. Two changes.

- **`UnhappyPathsAsync`, the "rename that does not exist" step (~line 714).** Rename it "the rename
  routes that do not exist"; **drop `/session/rename` from the loop** (now real); keep `/rename` and
  `/library/rename-by-rank`, which must still 404. Update the comment: `/session/rename` is the one
  rename operation, no other rename route exists.
- **A new happy-path step, driven last** (after `SaveAsync`, before `CloseAsync`, because rename
  renumbers everything): `RenameAsync`. On the scratch library it:
  1. records the pre-rename ids and each file's rating (`GET /media/{id}/meta`);
  2. `POST /session/rename` with a `clientRequestId` → checks `202` and a `running` `RenameOperation`;
  3. **polls `GET /session/rename`** until a terminal state, checking the phase only ever advances
     (`backing_up` → `renaming` → `saving` → `done`) and never goes backwards, and that it reaches
     `succeeded`;
  4. `GET /session` → checks `pairSeq == before + 1`, new `pairToken`, `lastAction.type == "rename"`
     with the `clientRequestId` echoed, `undoAvailable == false`;
  5. checks the new `pair` ids match `^\d{6}\.`, the old ids now `GET` `404`, a `rankmaster_backup_*`
     folder appeared, and the on-disk JSON still loads with ratings intact.

  Add `Rm2Api.StartRenameAsync(clientRequestId)`, `Rm2Api.GetRenameAsync()`,
  `Rm2Api.CancelRenameAsync()` in `src/rm2ctl/Rm2Api.cs`, and teach `src/rm2ctl/Snapshot.cs` the
  `rename` `lastAction.type`. `ScratchLibrary.Create()`'s six PNGs are enough. `rm2ctl` need not
  exercise cancel in the cycle (it would need a large folder to catch mid-run); cancel is proven by
  the deterministic unit and server tests (§ 5 items 6, 11).

The server's acceptance gate — `rm2ctl cycle` green against a real folder with the desktop app never
launched — now includes starting, observing to completion, and verifying a rename.

---

## 7. Phases

**Phase 1 — contract, alone, reviewed first.** All of § 3 (SERVER_SPEC.md + SERVER_PLAN.md) and § 4
(openapi.yaml). Reviewed against `SPEC.md` § Rename by rank and against `LibraryActions.RenameByRank`.
Gate: the suite builds; `OpenApiContractTests.The_document_parses` and `ErrorCodeTableTests` are
updated in lockstep so the contract triangle stays green; the new route/feature/lifecycle tests may
be red until Phase 3.

**Phase 2 — the operation.** Additive overloads first, in the shared libs (behaviour-preserving; a
one-line `SPEC.md` note that rename may be observed and cancelled, cancel restoring from the backup):
`FileOps.BackupLibrary/RestoreLibrary/RenameByConservativeScore` gain `(…, IProgress<int>?,
CancellationToken)` overloads, the old signatures delegating with `null, default`;
`LibraryActions.RenameByRank(IProgress<RenameProgress>, CancellationToken)` performs scan / pre-save /
`ReleaseAll` / backup / two-phase rename / remap / final save with progress and a between-files
cancellation check, restores from the backup on cancel or failure, and **returns the remapped
records without touching the session** (so the session apply can happen under the server lock). The
parameterless `RenameByRank()` stays as the desktop's, unchanged.

Then the server, in `Sessions/`: a `RenameOperation` record and a `RenameProgress` type; on
`SessionRegistry`, a `StartRenameAsync` (lock → checks → create op + CTS + flag → `Task.Run(run)` →
release → `202`), a lock-free `GetRenameAsync`, a `CancelRenameAsync` (signal CTS, return op), and
the `run` body (call the new `RenameByRank` overload off-lock, marshalling progress into the op via
volatile writes; on success re-take the lock and `ReplaceAll`/`ClearLastMove`/`PairSeq++`/set
`lastAction`/clear flag; on `OperationCanceledException` mark `cancelled` and clear the flag under the
lock, session untouched; on other exceptions mark `failed` with `restored`/`backup` parsed from the
exception, and on the double fault close the session under the lock). Distinguish the two failure
messages `RenameByRank` throws to fill `restored`; if message-parsing is judged brittle, the fallback
is a typed `RenameFailedException(bool Restored, string Backup)` from `Actions` — a shared-lib change,
so spec-note first. Register `POST/GET /session/rename` and `POST /session/rename/cancel` in
`SessionEndpoints.cs`. Add the three error codes to `ErrorCodes`. Flip `PingFeatures.Rename` to `true`
in `Security/SecurityEndpoints.cs`. Gate: route/feature/error tests green.

**Phase 3 — tests and `rm2ctl`.** `RenameTests.cs` (§ 5 items 1–10), the unit tests (11–13), and the
`rm2ctl` changes (§ 6). Gate: the whole `RankMaster2.Server.slnf` suite green on Linux, and
`rm2ctl cycle` green end to end including the observed rename.

**Phase 4 — audits.** State-machine (§ 5 items 14–16) and compatibility (item 17). Gate: both green.

---

## 8. Acceptance gate

Rename-by-rank on the server is done when, on this Linux box, with the desktop app never launched:

1. `dotnet build RankMaster2.Server.slnf` succeeds and the entire suite passes — the pre-existing
   count plus `RenameTests`, the unit tests, the audit additions, and the flipped ping/transport/
   error-count/OpenApi tests. The only deletions are the reversed-rule assertions named in § 5.
2. `rm2ctl cycle` is green and its run **starts a rename, polls the operation to `succeeded` with
   the phases only advancing, and verifies** the renumbering, the backup, the dead old ids and the
   still-loadable JSON.
3. A server test **observes a rename through its phases** (`backing_up` → `renaming` → `saving` →
   `done`) using the injected blocking catalog, and a unit test proves **cancel mid-file restores the
   original names** deterministically.
4. A server test proves a **cancel** returns the folder to its original names with the session
   unchanged, and the state-machine audit proves a **jammed-save failure** does the same via
   `rename_failed` with `restored: true`.
5. A server test proves a mutating call during a rename gets **`409 rename_in_progress`**.
6. The compatibility audit proves a **renamed** library loads through `JsonCatalog.Scan` with ratings
   intact and keys `000001.ext …` — the desktop app opening the folder after the phone renamed it.
7. `SERVER_SPEC.md`, `SERVER_PLAN.md` and `openapi.yaml` agree with the server on the three routes,
   the three error codes, the `rename` `ActionType`, the `202`, and `features.rename == true`, and the
   contract-triangle tests enforce it.

---

## 9. What is verifiable here, and what is not

**Fully verifiable on this build machine.** Rename is filesystem plus JSON — no WPF, no native media,
no video decode. The naming and remap already run in `RankMaster2.Catalog.Tests` on Linux; the
server, its 500-plus tests, `rm2ctl` and both audit suites build and run on Linux today
(`SERVER_PLAN.md` § 8). The progress phases, the cancel-restores-originals guarantee (unit-tested
with an injected cancel), the `rename_in_progress` gate, the failure/restore path (via `JamSave`),
and the wire lifecycle (via an injected blocking catalog) are all checkable here with `dotnet test`
and `rm2ctl cycle`.

**Not verifiable here.** Whether the PC client draws the bar and puts the confirmation in front of the
button, and whether the phone hides the option — client work in `PC_CLIENT_PLAN.md` § 6.6 and
`pc/plans/E-ranking-surface.md` § E6. A true many-minute rename on tens of thousands of files on real
hardware — a scale property, recorded as § 16 gap, not asserted by a test. The Windows-only edge of
§ 2.5 (a media `GET` holding a brief read handle over a `File.Move` mid-rename, absorbed by
`MoveWithRetry`) — a no-op on Linux, where an open handle does not block a move. A server-process kill
mid-run — unrecoverable by design (§ 2.4), with the backup as manual recovery; the plan does not test
a kill because there is nothing for the server to do about it.

---

## 10. For the coordinator — decisions this plan made that touch a shared document

- **Reverses `SERVER_SPEC.md` § 1.1 and `SERVER_PLAN.md` § 4/§ 7**, flips `features.rename`, and adds
  the **first long-running operation and the first `202`** to the contract — named as such in
  § 13.1. Whatever the next long operation is, this is the precedent: a separate small resource with
  start/observe/cancel, `/session` keeping its one snapshot shape.
- **Cancel is a rollback to original names** (§ 2.1), because there is no consistent half-renamed
  terminus. If a future reader wants a resumable/partial rename, that is a different operation and a
  bigger design; do not confuse it with cancel.
- **The run is server-owned and survives a client disconnect** (§ 2.4); a disconnect is not a cancel.
- **Additive overloads in `RankMaster2.Actions` and `RankMaster2.Catalog`** carry progress and
  cancellation; the desktop's parameterless `RenameByRank()` is untouched. This is a shared-lib change
  and takes a one-line `SPEC.md` note (rename may be observed and cancelled; cancel restores from the
  backup) — behaviour, not result, is what changes.
- **`rename_failed` distinguishes `restored` by parsing `RenameByRank`'s exception message** (§ 7,
  Phase 2); the clean alternative is a typed exception from `Actions`, a shared-lib change needing a
  spec note first. The coordinator may prefer to take that now and simplify Phase 2.
- **The phone should not offer rename** (§ 2.6) — a recommendation to the client authors, argued from
  the fact that a phone is the worst place to watch a bar and press cancel, not from convenience. The
  endpoint stays open to any token because availability is a server concern.
