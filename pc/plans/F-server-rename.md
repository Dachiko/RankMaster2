# Part F — rename by rank, on the server

**Status: plan, nothing built.** Reverses a pinned product decision. Adds one endpoint,
`POST /api/v1/session/rename`, and the spec, `openapi.yaml`, tests and `rm2ctl` changes that go
with it. Owns edits in `src/RankMaster2.Server/Sessions/`, `SERVER_SPEC.md`, `SERVER_PLAN.md`,
`openapi.yaml`, `tests/RankMaster2.Server.Tests/`, the two affected audit suites, and `src/rm2ctl/`.
It writes **no** new ranking or file-operation code: the behaviour already exists in
`RankMaster2.Actions.LibraryActions.RenameByRank`, unchanged since the desktop app.

**The owner decided this on 2026-09-16:** rename by rank survives into the new world, and it lives
**on the server**, not in a client. That reverses `SERVER_SPEC.md` § 1.1, which today forbids the
endpoint in the strongest terms the document contains. The project's rule is **spec first**: the
contract changes and is reviewed (Phase 1), and only then is anything implemented (Phase 2). This
plan covers both, in that order, and a weak executor should not re-decide anything below.

Read in this order before touching a file: `SPEC.md` § Rename by rank, § File actions, § Persistence;
`SERVER_SPEC.md` § 1.1, § 7 (all, especially § 7.4), § 8, § 9, § 10.1, § 10.8, § 10.10, § 11, § 12.2,
§ 12.5, § 13, § 14; `src/RankMaster2.Actions/LibraryActions.cs` (`RenameByRank` and its
restore-on-failure path); `src/RankMaster2.Ranking/RankingSession.cs` (`ReplaceAll`, and what it
clears); `src/RankMaster2.Server/Sessions/SessionRegistry.cs` (`MoveAsync` and `WithLockAsync` are
the shape a new action follows) and `SessionEndpoints.cs`; `src/RankMaster2.Catalog/FileOps.cs`
(`BackupLibrary`, `RestoreLibrary`, `RenameByConservativeScore`).

Where this plan and `SERVER_SPEC.md` disagree once Phase 1 has landed, the spec wins and this plan
is stale.

---

## 1. Decisions, settled before any code

| Question | Decision | Why |
|---|---|---|
| Where does the behaviour come from | **`LibraryActions.RenameByRank`, called verbatim.** No second implementation | `SPEC.md` § Rename by rank does not change; the code already scans, saves, releases, backs up, two-phase renames, remaps ids, saves and `ReplaceAll`s the session, with restore-on-failure. It is already wired into every open session as `OpenSession.Actions` |
| Route | **`POST /api/v1/session/rename`** | It is a session-scoped mutation. Appended to `SERVER_SPEC.md` as **§ 10.16** so no existing section renumbers (a weak executor must not have to renumber a 1314-line document) |
| Sync or async | **Synchronous, holding the session lock for the whole operation, with a documented time ceiling.** Not a started-and-polled operation | § 2.2 |
| What happens to the open session | **In-place transition via `RankingSession.ReplaceAll`.** Same `sessionId`, same `sessionSecret`, same folder, same lock; `pairSeq` **+1**; a brand-new pair whose every id is new | § 2.1 |
| Who may call it | **Any paired device.** Availability is a server question; the endpoint is open to any bearer token | § 2.4 |
| `pairToken` in the body | **None. Ignored if sent** — exactly like `undo` (§ 10.10). Rename is not pair-scoped | § 2.4 |
| `features.rename` in `/ping` | **`true`.** A client reads it to decide whether to offer the option | § 2.6 |
| New error code | **`rename_failed` (500).** `details: { restored: bool, backup: string\|null }` | § 2.3 |
| Backup accumulation | **Kept, one `rankmaster_backup_<ts>/` per successful or failed rename**, exactly as the desktop app leaves them. Not pruned | matches `SPEC.md`; `JsonCatalog.Scan` never descends into a subfolder, so backups never re-enter a session |

### 1.1 Deliberately absent

- **No progress reporting, no cancellation.** The operation is atomic to the client: one request,
  one final answer. A client that gives up waiting does not stop the rename (§ 2.2).
- **No `rename 2`, no options.** No target pattern, no width, no "dry run". `μ − 3σ` descending,
  then filename for ties, to `000001.ext` — fixed by `SPEC.md`, not a knob.
- **No `undo` of a rename.** `ReplaceAll` clears the engine's one-level undo and this plan clears
  the move-undo too (§ 2.1); the backup folder is the only way back, exactly as on the desktop.
  `undoAvailable` is `false` in the snapshot rename returns.
- **No change to `RankMaster2.Ranking`, `RankMaster2.Catalog` or `RankMaster2.Actions`.** If any of
  them turns out to need a change, `SPEC.md` changes first — the same rule the rest of the server
  lives by.
- **No client work.** Whether the phone or the PC client shows a rename button lives in
  `PC_CLIENT_PLAN.md` § 6.6 and `pc/plans/E-ranking-surface.md` § E6. This part makes the endpoint
  exist and say so through `/ping`; the offering is theirs.

---

## 2. The hard problems, and their answers

### 2.1 Every id changes at once

`RenameByRank` renames every file to `000001.ext …` and rewrites `rankmaster_db.json` so every key
and `filename` is the new name; ratings stay with the same bytes (`SPEC.md` § Rename by rank). An id
is a filename (§ 11.1), so **after the call every `pairToken`, every `links.*` URL, every ETag and
every id any client is holding refers to a file that no longer exists.**

The mechanism is `RankingSession.ReplaceAll(remapped)`, which `RenameByRank` already calls on the
live session when its folder matches. Read what it clears: `_undo` (the engine's one-level undo),
`_records` (replaced), `_warm`, `_recent`, `_recentOrder`, `_cues`; then `Current = Pick()` and
`FillWarm()`. It does **not** touch `SessionVotes`.

**What the server does to the open session, precisely:**

1. Call `open.Actions.RenameByRank()` inside the session lock.
2. On success, `RenameByRank` has already `ReplaceAll`'d the session with the remapped records, so
   `Current` is a freshly picked pair of **new** ids, `warmPairs` are new, `cues` is empty, and the
   engine undo is gone.
3. The registry then, exactly as every other successful action does: clears the move-undo
   (`open.Actions.ClearLastMove()` — see below), does `open.PairSeq++`, sets
   `open.LastSavedAt`, and sets `open.LastAction` to a **rename** record.
4. Materialise and return the snapshot, inside the lock.

**Why `ClearLastMove()` is required.** `ReplaceAll` clears the engine's undo but not
`LibraryActions.LastMove`. A discard done just before the rename would leave `LastMove` pointing at
`discarded/DSC_0123.jpg`; after rename the folder holds `000001.jpg …` and a later `undo` would try
to move `DSC_0123.jpg` back and `Restore` a record whose id predates the renumber — a genuine
inconsistency. Clearing it makes `undoAvailable` honestly `false` after a rename, matching the
engine undo that `ReplaceAll` already dropped.

**What the snapshot says afterwards:** same `sessionId`; `state` `ranking` (or `exhausted` if the
folder somehow left fewer than two eligible files, which a rename cannot cause — it renames, it does
not remove); `pairSeq` one higher; a new non-null `pairToken`; `pair` and `warmPairs` carrying the
new numeric ids and their fresh `links`/`mediaVersion`; `cues` empty; `undoAvailable` false;
`sessionVotes` unchanged (rename is not a vote); `counts` unchanged in every number (the same files,
renamed); `lastAction.type == "rename"`.

**Why keep the same `sessionId` rather than mint a new one.** A `sessionId` change is the contract's
signal for "everything you cached is void" (§ 9.1), and a rename does void everything — that is the
argument for minting one. It is rejected because the session object, folder, lock and secret are all
genuinely unchanged: it is the same session, one generation further on, and the brief's own pointer
to `ReplaceAll` is the in-place mechanism. Every consequence a client needs is already carried by
existing machinery, with **no new invariant**:

- **A client mid-vote** sent its vote with the pre-rename `pairToken`. If it arrives while the rename
  holds the lock, it waits up to 5 s and gets `503 session_busy` (§ 2.5). If it arrives after, the
  token is validated inside the lock against the new current token (§ 8.4 step 4), fails, and the
  client gets `409 stale_pair_token` carrying the **complete post-rename snapshot** in
  `error.session` (§ 8.5). Its `lastAction` disambiguation reads `type == "rename"`,
  `pairToken == null`, so "neither the id nor the token matches → the request never landed, the pair
  moved on for another reason → continue from the snapshot, do not replay." Exactly the behaviour
  § 8.5's table already prescribes. **No double vote is possible.**
- **A client holding cached bytes** finds every URL it cached now answers `404` (the old ids are no
  longer records → `unknown_media_id`, or mid-move → `media_file_missing`). § 11.3 already tells
  clients to treat a media `404` as "this id is gone, refresh from the snapshot," never as a
  transport error. The new numeric ids never collide with an original filename, so no stale bytes
  are served **within** this rename. The one residual — a numeric id reused across a *second* rename
  landing on a same-size, same-mtime file — is precisely the `(name, size, mtime)` fingerprint limit
  already recorded in § 12.5, and § 3 adds one sentence noting rename as the case that reuses a name.

### 2.2 It is slow and destructive — synchronous, with a ceiling

`RenameByRank` copies the whole top-level library into `rankmaster_backup_<ts>/`, then performs two
file moves per media file (to a temp name, then to `000001.ext`), then rewrites the JSON. On a large
library this is thousands of moves plus a full-size backup copy and can run for many seconds to
minutes — longer than a default HTTP client timeout.

**Decision: synchronous, holding the session lock, with a documented ceiling and a client that sets
a long timeout for this one call.** Rejected: a started-and-polled `202 + operationId` resource.

Justification against the shape of the API:

- **Every endpoint in § 10 is synchronous, and § 13.1 is absolute:** when a 2xx leaves the server the
  change is already durable. Synchronous rename honours this trivially — the `200` is sent only
  after `RenameByRank` returns, and it returns only after the second `JsonCatalog.Save` has fsynced
  and `File.Replace`d. A polled operation would have to invent a second notion of "done" and a second
  durability story; § 13.1 gives us one already.
- **A started-and-polled operation is a new resource shape** ("every `/session*` 2xx is a
  `SessionSnapshot`" — § 9 — would gain an exception) and a background worker that holds the session
  lock off the request thread. For one user on a LAN with a native client (no browser, no proxy, no
  CORS — § 2), that is cost with no buyer.
- **The transport has no intermediary that imposes a timeout** the server cannot see. The native
  client owns its own timeout and raises it for `POST /session/rename`. `rm2ctl` does the same.

**Consequences that must be spelled out in the spec:**

- The operation runs on the request thread, inside the session lock, start to finish.
- **Aborting the HTTP request does not abort the rename.** `RenameByRank` is a synchronous library
  call and is not handed the request's `CancellationToken`; a client that disconnects leaves it
  running to its own completion or restore. This is deliberate — a half-run rename must never be
  left half-run because a phone's Wi-Fi blinked.
- Therefore rename is **not** in § 13.3's "safe to retry blindly" set. A client that times out
  MUST resolve, not guess: `GET /session`, and compare `lastAction` — if `lastAction.type == "rename"`
  and `lastAction.clientRequestId` equals the id it sent, the rename landed; the ids in the snapshot
  will already be sequential. A blind re-`POST /session/rename` is safe from a data standpoint (it
  re-sorts identically and produces the same names) but does a second full backup-and-rename for
  nothing, so the client should resolve first. `clientRequestId` is therefore STRONGLY RECOMMENDED,
  the same as for vote (§ 10.6).
- **Known gap (added to § 16):** there is no progress and no ceiling the server enforces; a
  pathologically large library can exceed any client's patience. This is the same class as § 16.5
  (slow `browse`) and is recorded, not solved.

### 2.3 Safety — exactly what a partial failure leaves behind

`RenameByRank` backs the library up **before** it touches a file, rewrites the JSON to the new ids
**only after** every file has physically been renamed, and on any exception runs `RestoreLibrary`
(copy the backup's files back over the folder, delete every top-level file not in the backup — which
includes the JSON, since `BackupLibrary` copies `rankmaster_db.json` too). The ordering is the whole
safety argument: the JSON on disk is never rewritten to new ids unless the files already carry them,
and if a move fails midway the catch restores files **and** JSON together to the pre-rename state.

The server maps the three outcomes to exactly these answers:

| Outcome | Folder on disk | The open session | HTTP |
|---|---|---|---|
| **Success** | fully renamed; JSON matches | `ReplaceAll`'d to the renamed records; `pairSeq` +1; new pair | `200` + snapshot, `lastAction.type: "rename"` |
| **Rename threw, restore succeeded** | back to pre-rename; files and JSON agree | **unchanged** — `RenameByRank` did not `ReplaceAll` on the failure path, and the restored disk still matches the in-memory records; `pairSeq` **not** advanced | `500 rename_failed`, `details: { restored: true, backup: "<path>" }`, `error.session` = the unchanged snapshot |
| **Rename threw and restore also threw** (the double fault) | inconsistent; the backup folder is intact | the server **closes the session** (releases the lock) so nothing acts on a folder it can no longer trust | `500 rename_failed`, `details: { restored: false, backup: "<path>" }`; `error.session` omitted (no session is open after the close) |

**The outcome the brief says must be impossible** — a folder whose files and whose
`rankmaster_db.json` disagree with no way back — cannot occur, because the backup is taken first and
its path is named in `details.backup` on every failure. In the double-fault the folder *is* left
inconsistent, but the way back exists: it is the backup, and the response hands the caller its path.
The message on that path MUST name the backup and MUST NOT leak a stack trace or a path outside the
session folder (§ 4). `RenameByRank`'s own double-fault exception already carries the backup path;
the server surfaces it into `details.backup` and a generic message, and logs the rest by
`requestId`.

`recordsChanged`/`fileMoved` are not used here; `rename_failed`'s `details` is `restored` +
`backup`, which is what a client actually needs.

### 2.4 Who may call it

**Any paired device.** The endpoint sits behind the same bearer-token gate as every other
`/session*` route (§ 3). There is one user and pairing is the trust boundary; a second guard would
protect nothing this one does not. The endpoint takes **no** `pairToken` — it acts on the whole
folder, not on a pair — and MUST ignore one if a client sends it, exactly as `undo` does (§ 10.10).

Availability is a server question and this plan answers it: the endpoint exists and is open to any
token. **The offering is a client question and this plan does not answer it.** My recommendation to
the client authors, recorded for them and not binding here: the phone *should* offer it, behind the
one confirmation `SPEC.md` allows in the whole program ("the only confirmation in the program is
rename's"), because it is destructive and unundoable; and it should read `features.rename` from
`/ping` first so it degrades on a server that has not shipped this.

### 2.5 Concurrency — the existing lock, unchanged

Rename acquires the session semaphore through the same `WithLockAsync` every action uses and holds
it for the entire operation. Nothing needs to change in the locking code.

- **It will not run while another action is in flight:** it must acquire the gate, and the gate is
  held by whatever is running.
- **Nothing acts while it runs:** any concurrent `/session*` mutation or snapshot read waits on the
  gate and, at 5 s, gets `503 session_busy` with `Retry-After: 1` (§ 7.3) — for the whole duration
  of a long rename, that is what a client asking "at the wrong moment" receives, and it is already
  specified. Media `GET`s do not take the gate (they read the lock-free `CurrentForMedia` view), so
  during the rename they see files mid-move and answer `404 media_file_missing`, and afterwards
  `404 unknown_media_id` for the old ids — never corruption, because a `GET` never mutates (§ 11.3).
- The server does not hold decoded-file handles across requests (the still cache stores encoded
  bytes, not open streams), so there are no handles for `NoOpMediaPipeline.ReleaseAll` to release;
  a transient reader that a media `GET` opens mid-move on Windows is absorbed by
  `FileOps.MoveWithRetry` (20 × 50 ms). On Linux, where this is developed and verified, a move over
  an open handle is a non-issue.

### 2.6 What `/ping` says

`features.rename` becomes **`true`** and stays a fixed capability (not configurable — § 14 still
governs). A client reads it to decide whether to show the rename option; a server that predates this
part answers `false` and the client hides the option. The `openapi.yaml` `PingFeatures.rename`
`const: false` becomes `const: true`, and every fixture and test that asserts `false` flips to
`true` (§ 5). The sentence "there is no build in which rename-by-rank exists" is deleted from the
test and the spec — it pins a rule the owner has reversed.

---

## 3. The exact spec text to add to `SERVER_SPEC.md`

Phase 1 makes these edits and nothing else. Wording is normative; keep the house voice.

**§ 1.1 — replace the first bullet.** Delete the "Rename by rank. There is no rename endpoint …"
bullet in full. It does not move to a new list; it is gone, because the thing it forbade now exists.
Add, in its place:

> - **Rename by rank lives at `POST /session/rename` (§ 10.16)** and nowhere else. There is no other
>   rename route, no flag, no config key, no debug build and no `GET`. A request to any *other* path
>   containing `rename` MUST fall through to the normal `404 not_found`.

**§ 10.16 — new subsection, appended after § 10.15.** (Appended, not inserted, so §§ 11–16 do not
renumber.) Full text:

> ### 10.16 `POST /session/rename`
>
> Rename every file in the open folder by rank and rewrite `rankmaster_db.json` to match — the
> server side of `SPEC.md` § Rename by rank. Authenticated like every `/session*` route. Takes **no**
> `pairToken`: it acts on the whole folder, not on a pair, so no pair generation owns it; a
> `pairToken` in the body MUST be ignored, exactly as for `undo` (§ 10.10).
>
> ```json
> { "clientRequestId": "1f0c…" }
> ```
>
> | Field | Type | Required |
> |---|---|---|
> | `clientRequestId` | string, ≤64 chars | no, but STRONGLY RECOMMENDED — this call is not safe to retry blindly (§ 13.3), and `lastAction.clientRequestId` is how a client confirms it landed |
>
> The body is optional; an absent body is a bare rename.
>
> **What it does**, inside the session lock, calling `LibraryActions.RenameByRank` (`SPEC.md`
> § Rename by rank) verbatim: copy the whole top-level library and its JSON into
> `<folder>/rankmaster_backup_<yyyyMMdd_HHmmss>/`; two-phase rename every media file to
> `000001.ext`, `000002.ext`, … by `μ − 3σ` descending, then filename for ties; rewrite the JSON so
> every key and `filename` is the new name, ratings unchanged; then `RankingSession.ReplaceAll` the
> open session with the renamed records.
>
> **Because an id is a filename (§ 11), a successful rename changes every id at once.** Every
> `pairToken`, every `links.*` URL and every ETag a client holds now points at a file that no longer
> exists. The session stays the same session — same `sessionId`, same folder, same lock — one
> generation further on:
>
> - `pairSeq` is incremented by exactly 1.
> - A new pair is picked from the renamed records; `pair`, `pairToken` and `warmPairs` all carry the
>   new numeric ids.
> - `cues` is emptied and the engine's one-level undo is cleared (`ReplaceAll`); the move-undo is
>   cleared too, so `undoAvailable` is `false`.
> - `sessionVotes`, `counts` and `progress` are unchanged in value (the same files, renamed).
> - `lastAction` is a `rename` record (`type: "rename"`, `pairToken: null`, the `clientRequestId`
>   echoed, every other field `null`). This is the one case `lastAction.type` is `rename`.
>
> A client MUST treat a rename as it treats any advance whose token went stale: an in-flight action
> arriving afterwards is answered `409 stale_pair_token` with the complete post-rename snapshot
> (§ 8.5) and MUST NOT be replayed; every cached media URL now answers `404` and MUST be refreshed
> from the snapshot (§ 11.3), never retried.
>
> | Outcome | Status | State |
> |---|---|---|
> | renamed, JSON rewritten, session replaced | `200` | `pairSeq` +1; a new pair of numeric ids; `lastAction.type: "rename"` |
> | the rename threw and the library was restored from the backup | `500 rename_failed`, `details: { restored: true, backup }` | **nothing changed**: files and JSON are back to before, the session is untouched, `pairSeq` did not move, the client's token is still valid |
> | the rename threw and the restore also threw | `500 rename_failed`, `details: { restored: false, backup }` | the folder is inconsistent and the backup at `backup` is the way back; the server closes the session and releases the lock, so no session is open and `error.session` is omitted |
>
> **Safety.** The backup is taken before any file is touched, and the JSON is rewritten to the new
> ids only after every file has been renamed, so the JSON on disk never disagrees with the files
> except during the restore itself — and the restore puts both back together. `details.backup` names
> the backup folder on both failure rows; `error.message` MUST name it too and MUST NOT leak a stack
> trace or a path outside the folder (§ 4).
>
> **It is synchronous and can be slow.** Thousands of file moves plus a full-library backup copy may
> outlast a default client timeout; a client MUST set a longer timeout for this one call. The server
> runs it on the request thread inside the lock (§ 13.1: the `200` is sent only once both saves have
> fsynced). **Disconnecting the request does not abort the rename** — it runs to completion or to
> restore — so rename is not in § 13.3's blind-retry set; a client that loses the connection MUST
> resolve with `GET /session` and `lastAction`, never by voting or by assuming.
>
> **Concurrency.** It holds the session semaphore for its whole duration; any other `/session*` call
> waits and, at 5 s, gets `503 session_busy` (§ 7.3). Media `GET`s do not take the lock and answer
> `404 media_file_missing`/`unknown_media_id` for ids caught by the rename (§ 11.3); they never
> mutate.

**§ 7.2 — add one transition row** (after the `undo` row):

> | ranking\|exhausted | `POST /session/rename` ok | ranking (or exhausted) | +1 | files renamed, JSON rewritten, `ReplaceAll`; every id is new |
> | ranking\|exhausted | `POST /session/rename` failed, restored | unchanged | — | library restored from backup; nothing changed |

**§ 8.3 — add rows to the `pairSeq` table** and one caveat sentence:

> | rename succeeds | files renamed, JSON rewritten, session replaced | **+1** | stale — and every id is new |
> | rename fails, library restored | nothing changed | unchanged | still valid |
>
> After the table, extend the "resets to 0 only when a new session opens" note: a rename does **not**
> reset `pairSeq` and does **not** change `sessionId` — it is the same session advanced one
> generation, even though every id changed.

**§ 9.4 — extend the `type` enum** to `vote | skip | discard | special | undo | drop_missing |
rename`, and add: "`rename` — a `POST /session/rename` (§ 10.16) succeeded; `pairToken`, `winner`,
`side`, `id`, `restoredId` and `undoneType` are all `null`. It is the one `lastAction` that can be
present at `pairSeq` values reached by a rename rather than by opening the session."

**§ 12.5 — one sentence on the known limit** (append to the "Known limit" paragraph):

> A rename (§ 10.16) reassigns filenames to `000001.ext …`, so a numeric id can, across two renames,
> land on a different file of the same size and mtime and reuse an ETag. This is the same
> `(name, size, mtime)` limit; within a single rename it cannot bite, because the new numeric ids
> never match a client's pre-rename cache keys.

**§ 13.2 — add a per-endpoint ordering row:**

> | `POST /session/rename` | library backed up, files renamed, JSON rewritten, then `ReplaceAll` — or, on failure, files and JSON restored from the backup | yes; the backup precedes any change and the JSON is rewritten only after every file is renamed |

**§ 13.3 — add a retry row:**

> | `POST /session/rename` | **no** | disconnecting does not abort it; `GET /session` and read `lastAction` — `type: "rename"` with your `clientRequestId` means it landed. A blind re-POST re-backs-up and re-renames for nothing |

**§ 14 — flip the feature and delete the reversed sentence.** `"rename": false` becomes
`"rename": true`. Delete "`rename` is `false` and there is no build in which it is `true`." Replace
with: "`rename` is `true` when the server exposes `POST /session/rename` (§ 10.16)."

**§ 16 — add a known gap:**

> 10. **`POST /session/rename` has no progress and no server-enforced ceiling.** A very large library
>     can outlast a client's patience; the client must set a long timeout. Same class as gap 5.

**§ 5 — add the error code.** Under § 5.4 (or a short new § 5.7 "Rename"), add:

> | `rename_failed` | 500 | `POST /session/rename` threw | `{ "restored": bool, "backup": string\|null }` |

`restored: true` means files and JSON were put back and the session is unchanged; `restored: false`
is the double fault — the backup at `backup` is the way back and the server has closed the session.

Also mention in § 6's `500` row that it now covers `rename_failed` alongside `save_failed`,
`move_failed` and `internal_error`.

**`SERVER_PLAN.md` — the scope owner must change too.** Remove the top-of-file line "Rename-by-rank
is deliberately **not** exposed." In § 4, delete "Deliberately absent: **rename**. No endpoint, no
plumbing, no flag." and add `POST /session/rename` to the session action list. In § 7's decisions
table, change the rename row from a non-goal to "Rename by rank | **On the server**, `POST
/session/rename` | owner decision 2026-09-16". This is a deliberate scope reversal, recorded as one.

---

## 4. `openapi.yaml` changes

`tests/RankMaster2.Server.Tests/OpenApiContractTests.cs` now checks that the document parses and
that documented routes and served routes and types match — the document is load-bearing, not
decoration. Make all of:

1. **New path `/session/rename`** under `paths:`, `post:`, `tags: [actions]`,
   `operationId: renameByRank`, summarising § 10.16. `requestBody` `required: false` referencing a
   new `RenameRequest` schema (`{ clientRequestId?: string ≤64 }`, `additionalProperties: false`).
   Responses: `200` → `SessionSnapshot` (reuse `#/components/responses/Snapshot`); `400`
   BadRequest; `401` Unauthorized; `404` NoSession; `413` PayloadTooLarge; `415`
   UnsupportedContentType; `500` a new `RenameFailed` response (or inline) whose example is
   `rename_failed` with `details: { restored: true, backup: "…/rankmaster_backup_20260916_120000" }`;
   `503` SessionBusy; `default` InternalError. The prose must say it takes no `pairToken`, is
   synchronous and slow, and that a `200` means the JSON is already rewritten (§ 13.1).
2. **`RenameRequest` schema** in `components/schemas`.
3. **`ErrorCode` enum**: add `rename_failed`.
4. **`ActionType` enum** (used by `LastAction.type`): add `rename`.
5. **`PingFeatures.rename`**: change `const: false` to `const: true` and rewrite its `description`
   to "True when `POST /session/rename` (§ 10.16) is exposed." Update **both** `/ping` response
   examples (`public` and `authenticated`) so `features.rename` reads `true`.
6. Update the top-of-file `description`'s "Deliberately absent: **rename by rank**. No endpoint, no
   flag, no debug route" — it now names the endpoint and keeps only "no *other* rename route".

`OpenApiContractTests` needs one new `[InlineData("/session/rename")]` in
`Every_route_the_server_maps_is_in_the_document` (§ 5). The record-shape tests
(`SessionSnapshot`/`LastAction`/`Counts`/`MediaRef`) need no change: rename returns the ordinary
snapshot and adds no field — `rename` is a new *value* of `type`, not a new key.

---

## 5. Exactly which existing tests must change, and to what

Deleting an assertion that pins a rule the owner reversed is correct; deleting one that pins a rule
still standing is not. The difference is called out on every line.

- **`tests/RankMaster2.Server.Tests/PingContractTests.cs`**,
  `The_absent_features_are_absent_and_not_configurable`: change
  `Assert.False(features.GetProperty("rename")…)` to `Assert.True(...)`, and delete the message
  "There is no build in which rename-by-rank exists (SERVER_SPEC.md § 1.1)." **This pins a reversed
  rule — change it.** The other four asserts in that test (`videoTranscoding`, `posterFrames`,
  `videoProbe`, `browse`, `maxConcurrentSessions`) pin rules that still stand — leave them.

- **`tests/RankMaster2.Server.Tests/Harness/ContractShape.cs`**, `PingFeatureKeys`: unchanged
  (the *key* `rename` still exists; only its value changed).

- **`tests/RankMaster2.Server.Tests/TransportTests.cs`**, `There_is_no_rename_route` theory: **remove
  the `[InlineData("/session/rename")]` case** — that path is now a real route, and asserting it
  404s pins a reversed rule. **Keep `/rename`, `/library/rename-by-rank`, `/debug/rename`** — the
  rule "no *other* rename route, no flag, no debug route" still stands, so those must still 404. This
  is the line the brief draws: three of the four cases survive, one is deleted.

- **`tests/RankMaster2.Server.Tests/ErrorCodeTableTests.cs`**:
  `Every_code_has_exactly_the_forty_the_spec_defines` → **41**, and rename the method. The three
  agreement tests (`The_servers_code_table_matches_the_contract`, `The_openapi_enum_lists_the_same_codes`)
  pass automatically once `rename_failed` is in `SERVER_SPEC.md` § 5, `openapi.yaml`'s `ErrorCode`
  enum, `src/RankMaster2.Server/Contracts/ApiError.cs` (`ErrorCodes`), and
  `tests/RankMaster2.Server.Tests/Harness/ErrorStatuses.cs` — add `rename_failed = 500` to both code
  tables. Add an `[InlineData("rename_failed", 500)]` spot-check.

- **`android/app/src/test/kotlin/com/rankmaster2/phone/net/Rm2Fixtures.kt`** (line ~238): the ping
  fixture has `"rename": false`. Flip to `true` so the Android fixture matches the server. (Not a
  server test, but it is a fixture of the contract and will otherwise mislead the phone tests.)

**New server tests** — a new `tests/RankMaster2.Server.Tests/RenameTests.cs`, in the
`Rm2ServerCollection`, driving the real HTTP surface against a `LibraryFolder`:

1. `Rename_renumbers_every_file_and_rewrites_the_database` — open `SixStills`, vote a few, rename,
   assert `200`; the snapshot's `pair` ids match `^\d{6}\.[a-z]+$`; on disk the top-level files are
   `000001.ext …` and `rankmaster_db.json`'s keys equal its `filename`s equal those names; a
   `rankmaster_backup_*` folder exists containing the original files and JSON.
2. `Rename_advances_pairSeq_and_leaves_a_rename_lastAction` — `pairSeq` +1, new non-null
   `pairToken`, `lastAction.type == "rename"`, `lastAction.clientRequestId` echoed,
   `undoAvailable == false`, `cues` empty, `sessionVotes` unchanged.
3. `Ratings_survive_the_rename` — capture μ/σ/matches/impressions per file before; after rename the
   same values are present, carried onto the new numeric ids (compare via `GET /media/{id}/meta` on
   the new ids, or read the JSON).
4. `The_old_ids_are_gone_after_a_rename` — a `GET /media/{oldId}/still` answers `404`
   (`unknown_media_id`); a stale pre-rename `pairToken` on `POST /session/vote` answers
   `409 stale_pair_token` with the post-rename snapshot embedded.
5. `Rename_takes_no_pairToken_and_ignores_one` — a body carrying a (stale) `pairToken` still
   succeeds; the token is neither required nor validated.
6. `Rename_needs_no_session_open_is_a_404` — `POST /session/rename` with no session →
   `404 no_session`.
7. `Rename_route_is_documented_and_reachable` — belt-and-braces that `POST /session/rename` is not a
   404 route (complements the `OpenApiContractTests` route check).

**State-machine audit** — extend `tests/RankMaster2.Audit.StateMachine/` (this is what the
`rm2-audit-state` agent owns; the plan writes the tests, the audit agent will later re-derive them):

8. `RenameHoldsTheLock` — start a rename against a folder large enough to be slow (or use the
   `JamSave` lever to stall it deterministically); a concurrent `POST /session/vote` gets
   `503 session_busy`. If a deterministic stall is hard, assert the weaker invariant that two renames
   cannot interleave (the second waits, never corrupts).
9. `RenameFailureRestoresAndChangesNothing` — `AuditFolder.SixStills().JamSave()`, then rename:
   `RenameByConservativeScore` moves the files, the second `Save` throws, `RestoreLibrary` puts
   originals and JSON back. Assert `500 rename_failed`, `details.restored == true`,
   `details.backup` names an existing backup folder; the top-level files are the **original** names
   again; `error.session` is the **unchanged** snapshot with the **unchanged** `pairSeq` and a still
   non-null original `pairToken`; a `GET /session` still ranks the original ids. This reuses the
   exact `JamSave` mechanism `RollbackAsymmetryTests` already relies on.
10. `RenameClearsUndo` — discard one side (so `undoAvailable` is true), then rename; afterwards
    `undoAvailable == false` and `POST /session/undo` answers `409 nothing_to_undo`.

**Compatibility audit** — add to `tests/RankMaster2.Audit.Compatibility/RoundTripTests.cs` (owned
by `rm2-audit-compat`):

11. `A_renamed_library_still_loads_in_the_desktop_app` — open, vote, `POST /session/rename`, close;
    then `new JsonCatalog().Scan(folder)` (what the desktop app calls) returns the same count with
    keys `000001.ext …`, every rating/match/impression/lastPlayed intact; `Db.RequireV1Schema`
    passes; and, since RM1 identity is the filename, `Rm1`-style read confirms keys equal
    `filename`s. This is the compatibility contract for the renamed file: the desktop app and RM1
    must open what the server's rename wrote, ratings intact.

---

## 6. The `rm2ctl` change — the acceptance gate for the whole server

`src/rm2ctl/Cycle.cs` is the server's acceptance gate: its cycle drives the whole contract and exits
non-zero on any disagreement. Two changes.

- **`UnhappyPathsAsync`, the "unknown route, and the rename that does not exist" step (~line 714).**
  Its loop currently asserts `/rename`, `/session/rename` and `/library/rename-by-rank` all 404. The
  rule reversed for exactly one of them. Rename the step to "unknown route, and the rename routes
  that do not exist", and **drop `/session/rename` from the loop** — keep `/rename` and
  `/library/rename-by-rank` (and `/debug/rename` if desired), which must still 404. The comment must
  now read that `POST /session/rename` is the *one* rename route and no other exists.

- **A new happy-path step, driven last** (after `SaveAsync`, before `CloseAsync`, so it does not
  disturb the earlier vote/skip/discard/special/undo checks — rename renumbers everything):
  `RenameAsync`. On the scratch library it:
  1. reads the pre-rename ids and each file's rating via `GET /media/{id}/meta`;
  2. `POST /session/rename` with a `clientRequestId`, using an extended client timeout;
  3. checks `200`, a `SessionSnapshot`, `pairSeq == before + 1`, a new `pairToken != before`,
     `lastAction.type == "rename"` and its `clientRequestId` echoed, `undoAvailable == false`;
  4. checks the new `pair` ids match `^\d{6}\.` and the old ids now `GET` `404`;
  5. checks a `rankmaster_backup_*` folder appeared in the scratch dir;
  6. checks the on-disk JSON still loads (reuse the cycle's existing JSON read) with ratings intact.

  Add a matching `Rm2Api.RenameAsync(clientRequestId)` in `src/rm2ctl/Rm2Api.cs` and, if the cycle's
  snapshot wire type (`src/rm2ctl/Snapshot.cs`) switches on `lastAction.type`, teach it the `rename`
  value. `ScratchLibrary.Create()` already makes six PNGs, which is enough to renumber.

The acceptance gate for the server is unchanged in spirit — `rm2ctl cycle` green against a real
folder with the desktop app never launched — and now includes the rename that *does* exist.

---

## 7. Phases

**Phase 1 — contract, alone, reviewed before any code.** All of § 3 (SERVER_SPEC.md + SERVER_PLAN.md)
and § 4 (openapi.yaml). Nothing else. Reviewed against `SPEC.md` § Rename by rank and against
`LibraryActions.RenameByRank` — the spec must describe what the code does, because the code is not
changing. Gate: `dotnet test` still builds; `OpenApiContractTests.The_document_parses` and
`ErrorCodeTableTests` are updated in lockstep (§ 5) so the suite stays green on the contract triangle
even before the endpoint exists. (Route/feature tests for the new endpoint go red here — that is
expected until Phase 2.)

**Phase 2 — the endpoint, in `Sessions/`.** Add `RenameByRankAsync(JsonElement? body, BodyError?
bodyError, CancellationToken)` to `SessionRegistry`, modelled on `MoveAsync`: read the optional
`clientRequestId`; no `pairToken` read; `WithLockAsync`; if no session → `no_session`; then
`try { open.Actions.RenameByRank(); open.Actions.ClearLastMove(); open.PairSeq++;
open.LastSavedAt = now; open.LastAction = rename record; return Ok(Materialise(open)); }`. The catch
distinguishes the two failure rows of § 2.3 by inspecting the exception message
`RenameByRank` throws — it says "Folder restored from: <backup>" on a restored failure and "Rename
failed and restore did not finish. Backup is at: <backup>" on the double fault; parse the backup path
out, and on the double fault also `_open = null; open.Lock.Dispose();` before returning
`rename_failed` with `restored:false` and no `error.session`. **If parsing a message is judged too
brittle**, the alternative is a one-line change to `LibraryActions.RenameByRank` to throw a typed
`RenameFailedException(bool Restored, string Backup)` instead of `IOException` — but that touches
`RankMaster2.Actions`, so it requires a `SPEC.md`/scope note first and is the fallback, not the
default. Map `RenameByRank`'s `InvalidOperationException("Nothing to rename.")` (empty folder) to
`rename_failed` `restored:true` (nothing was touched). Register the route in `SessionEndpoints.cs`
as `group.MapPost("/rename", …)`, reading the body with `required: false` like `undo`. Flip
`PingFeatures(Rename: false …)` to `true` in `src/RankMaster2.Server/Security/SecurityEndpoints.cs`,
and add `rename_failed` to `ErrorCodes`. Gate: the § 5 route/feature/error tests go green.

**Phase 3 — tests and `rm2ctl`.** The new `RenameTests.cs` (§ 5 items 1–7) and the `rm2ctl` changes
(§ 6). Gate: the whole `RankMaster2.Server.slnf` suite green on Linux, and `rm2ctl cycle` green end
to end including the rename step.

**Phase 4 — audits.** The state-machine (§ 5 items 8–10) and compatibility (§ 5 item 11) tests. Gate:
both audit suites green on Linux.

---

## 8. Acceptance gate

Rename-by-rank on the server is done when, on this Linux box, with the desktop app never launched:

1. `dotnet build RankMaster2.Server.slnf` succeeds and the entire suite passes — the pre-existing
   count plus the new `RenameTests`, the three audit additions, and the flipped/adjusted ping,
   transport, error-count and OpenApi tests. No test that pins a still-standing rule was deleted; the
   only deletions are the reversed-rule assertions named in § 5.
2. `rm2ctl cycle` against a scratch folder is green and its run includes the new rename step: rename
   returns `200`, renumbers every file to `000001.ext …`, advances `pairSeq`, leaves
   `lastAction.type == "rename"`, produces a backup folder, dead-ends every old media id, and the
   JSON it wrote still loads.
3. The compatibility audit proves a **renamed** library loads through `JsonCatalog.Scan` — the
   desktop app opening the folder after the phone renamed it — with every rating intact and keys
   equal to filenames equal to `000001.ext …`. This is the compatibility contract for rename.
4. The state-machine audit proves a jammed-save rename restores the library and changes nothing:
   `rename_failed` with `restored: true`, the original filenames back on disk, and a session whose
   `pairSeq` and `pairToken` never moved.
5. `SERVER_SPEC.md`, `SERVER_PLAN.md` and `openapi.yaml` agree with the server: the endpoint, the
   error code, the `ActionType` value, and `features.rename == true` appear in all of them, and the
   contract-triangle tests (`ErrorCodeTableTests`, `OpenApiContractTests`) enforce it.

---

## 9. What is verifiable here, and what is not

**Fully verifiable on this build machine.** Rename by rank is pure filesystem plus JSON — no WPF, no
native media, no video decode. `RenameByRank` and its `FileOps` primitives already run in
`RankMaster2.Catalog.Tests` on Linux (`RenameByConservativeScore_OrdersByMuMinusThreeSigma`,
`RemapAfterRename_KeepsRatings`). The server, its 500-plus tests, `rm2ctl` and both audit suites all
build and run on Linux today (`SERVER_PLAN.md` § 8). Every claim in § 8 is checkable here with
`dotnet test` and `rm2ctl cycle`.

**Not verifiable here, and not this part's to verify.** Whether the phone or the PC client shows a
rename button and puts the destructive confirmation in front of it — that is `PC_CLIENT_PLAN.md`
§ 6.6 and `pc/plans/E-ranking-surface.md` § E6, and it depends on the owner's client decision, not on
the server. The Windows-only edge of § 2.5 (a media `GET` holding a brief read handle over a
`File.Move` mid-rename, absorbed by `MoveWithRetry`) cannot be exercised on Linux, where an open
handle does not block a move; it is recorded, not tested here. And the § 2.2 ceiling — a library so
large the rename outlasts a client's patience — is a property of real hardware and library size, not
something a unit test asserts; it is the known gap added to § 16.

---

## 10. For the coordinator — decisions this plan made that touch a shared document

- **Reverses `SERVER_SPEC.md` § 1.1 and `SERVER_PLAN.md` § 4/§ 7**, and flips `openapi.yaml`
  `PingFeatures.rename` and `features.rename` in `/ping`. This is a deliberate scope reversal on the
  owner's 2026-09-16 decision, recorded as one in `SERVER_PLAN.md` § 7.
- **Same `sessionId` across a rename**, not a new one (§ 2.1). If a future reader prefers "a rename
  mints a new session," that is a bigger contract change (new secret, `pairSeq` reset, and edits to
  § 8.3, § 9.1 and § 12.5's cache-scope promise) and must be made spec-first; this plan chose the
  in-place transition because it is the same session and needs no new invariant.
- **`rename_failed` distinguishes `restored` from the double fault by parsing `RenameByRank`'s
  exception message** (§ 7, Phase 2). The clean alternative is a typed exception from
  `RankMaster2.Actions`, which is a one-line change to a shared library and therefore needs a
  `SPEC.md`/scope note first. If the coordinator would rather take that change now, this plan's
  Phase 2 simplifies and the fragility disappears.
- **The phone offering rename** is left to the client parts, with a recommendation only: offer it,
  behind rename's one allowed confirmation, gated on `features.rename` from `/ping`.
