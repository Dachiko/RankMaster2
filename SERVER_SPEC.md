# Rank Master 2 — server API contract

This file is the source of truth for the **headless server**. `SPEC.md` is the source of truth for
**ranking behaviour**; where the two overlap, `SPEC.md` wins and this file only says how that
behaviour is exposed over HTTP. `SERVER_PLAN.md` is the source of truth for **scope**. If code and
this document disagree, the document wins until we change it on purpose.

Written for four implementers who will not talk to each other. Everything here is normative.
**MUST / MUST NOT / SHOULD / MAY** carry their RFC 2119 meaning. Where a rule exists only because
`RankingSession` behaves a certain way, the reason is stated — do not "fix" it here, fix it in
`RankMaster2.Ranking` first and then update this file.

---

## 1. Scope

The server wraps the existing `net8.0` libraries. It owns HTTP, auth, locking, media bytes and
pair tokens. It owns **no** ranking logic.

| Concern | Owner | Do not reimplement |
|---|---|---|
| TrueSkill update, match quality | `RankMaster2.Ranking` | `SPEC.md` § Ranking |
| Pair selection, recent/reserved sets | `PairSelector` | `SPEC.md` § `SelectNextPair` |
| `impressions` / `matches` / `lastPlayed` rules | `RecordUpdates` | `SPEC.md` § Persistence |
| Save-on-every-choice, atomic write, merge | `JsonCatalog` | `SPEC.md` § Persistence |
| Discard / special 1 / undo-last-move | `LibraryActions` | `SPEC.md` § File actions |
| Everything below | this file | — |

### 1.1 Deliberately absent

- **Rename by rank is a journalled, long-running operation at `/session/rename` (§ 10.16)** —
  `POST` to start, `GET` to observe, `POST /session/rename/cancel` to stop — and nowhere else. It
  copies no files. A request to any *other* path containing `rename` MUST fall through to `404
  not_found`.
- **Transcoding, poster frames, duration and codec probing** (`SERVER_PLAN.md` § 3.4, § 7). The
  server never decodes a video frame. `GET /media/{id}/thumb` and `GET /media/{id}/still` MUST fail
  with `wrong_media_kind` for a video id.
- **Multi-client session sharing.** One session exists server-wide (`SERVER_PLAN.md` § 7).
- **Tie votes, a persisted match log, a leaderboard, recursive scan** — non-goals in `SPEC.md` and
  non-goals here.
- **An undo stack.** Undo is one level and covers the last action of any kind (§ 10.10). Depth is a
  product decision, not a limitation: each level costs a copy of the library's records.

---

## 2. Transport

| Item | Value |
|---|---|
| Base path | `/api/v1` — every path in this document is relative to it |
| Scheme | `https` only, self-signed certificate pinned by fingerprint (`SERVER_PLAN.md` § 3.5) |
| Bind address | explicit LAN address from config; never `0.0.0.0` |
| JSON request bodies | `Content-Type: application/json`; anything else → `415 unsupported_content_type` |
| JSON response bodies | `application/json; charset=utf-8` |
| Max JSON request body | 65536 bytes → `413 payload_too_large` |
| Unknown JSON fields | ignored; the server MUST NOT reject a request for them |
| Missing/`null` optional fields | treated as absent |
| Timestamps in JSON bodies | RFC 3339 UTC with `Z`, e.g. `2026-09-12T18:04:11.412Z` |
| Timestamps mirrored from `rankmaster_db.json` (`lastPlayed`, `lastUpdated`) | unix **milliseconds**, integer, exactly as stored — the file format does not change |
| Path separators in JSON | the platform's own, unescaped except as JSON requires |

Every response MUST carry:

```
X-Request-Id: <opaque, unique per request>
X-Content-Type-Options: nosniff
```

Every JSON response MUST carry `Cache-Control: no-store`. Media responses are the only cacheable
responses (§ 12).

`HEAD` MUST be supported on all four `/media` endpoints and MUST return the identical status line
and headers as `GET` with no body. `OPTIONS` is not required. CORS is not configured; the client is
native (`SERVER_PLAN.md` § 7).

---

### 2.4 The server data directory

Everything the server owns lives here, and **nothing the server owns is ever written into the user's
media folder** — the one exception is `rankmaster_db.json` itself, which is the user's data, and the
lock file beside it.

**One spelling, everywhere: `RankMaster2/Server`** — capital `R`, capital `M`, capital `S`. Every
program that resolves this directory MUST produce exactly this path; a lowercase `server` or
`rankmaster2` is a bug, and on Linux it is a bug that silently splits the server's own state across two
directories.

| Platform | Path |
|---|---|
| Windows | `%LOCALAPPDATA%\RankMaster2\Server` |
| Other | `$XDG_DATA_HOME/RankMaster2/Server`, else `~/.local/share/RankMaster2/Server` |

It holds the TLS certificate, the device token store, the pairing sentinel and offer, and the
rendered-still cache, which is at **`<data dir>/cache`** — under the data directory, never inside the
user's media folder. Created on first run with owner-only permissions, because the token store and the
certificate's private key live in it.

**Overridable by configuration**, in this order: `RankMaster2:DataDirectory` in `appsettings.json` beside
the server executable, then the `RM2_DATA_DIR` environment variable, then the platform default above.
Every program that needs to find the server's data directory — the tray, the PC client's enrolment —
MUST resolve it in that same order, or it will look in the wrong place on the very machine the server is
running on.

**Configuration keys.** `appsettings.json`, under `RankMaster2`:

| Key | Default | Meaning |
|---|---|---|
| `DataDirectory` | the platform path above | Where everything in this section lives. |
| `SaveDelaySeconds` | `2` | How long a choice may sit in memory before the database is written (§ 13.1). **`0` restores the old behaviour exactly**: every choice is saved before its response. |
| `MaxUnsavedChoices` | `5` | How many choices may be unsaved before a write is forced, whichever limit is reached first (§ 13.1). |

`SaveDelaySeconds` and `MaxUnsavedChoices` are **not** on `/ping` and are **not** features: a client
neither negotiates them nor needs to know them. They bound what a crash can cost, nothing else.

---

## 3. Authentication

| Endpoint | Auth |
|---|---|
| `POST /pair` | none (a valid one-time pairing code is the credential) |
| `GET /ping` | optional — see below |
| everything else | `Authorization: Bearer <device token>` |

- Tokens MUST be accepted **only** in the `Authorization` header. A token in a query string MUST be
  ignored, and the request MUST be treated as unauthenticated (`SERVER_PLAN.md` § 3.5).
- A missing or malformed `Authorization` header → `401 unauthenticated`.
- A well-formed but unknown/expired token → `401 invalid_token`.
- A token whose device was revoked → `401 token_revoked`.
- `GET /ping` without a token MUST succeed and return the **public** subset (§ 11.3). With a valid
  token it returns the full body. With an *invalid* token it MUST return `401`, not the public
  subset — a client with a bad token must learn that it is bad.
- The bearer token is the only barrier between the LAN and the whole filesystem
  (`SERVER_PLAN.md` § 3.6). Constant-time comparison is REQUIRED for token and pairing-code checks.

---

## 4. The error envelope

**Every** non-2xx response with a body uses exactly this shape. There is no second error shape
anywhere in the API, including for auth failures, routing 404s and unhandled exceptions.

```json
{
  "error": {
    "code": "stale_pair_token",
    "message": "The supplied pairToken is not the current pair.",
    "requestId": "01J8Z5K0QF3V8A0M6R9Q2B7T4C",
    "details": { "suppliedToken": "M0xQ…", "currentToken": "7tRb…" },
    "session": { "…": "a full SessionSnapshot, or absent" }
  }
}
```

| Field | Type | Required | Meaning |
|---|---|---|---|
| `error.code` | string | yes | Stable machine-readable code from § 5. Clients MUST branch on this and never on `message`. |
| `error.message` | string | yes | Human English. Unstable. Never parsed. |
| `error.requestId` | string | yes | Equal to the `X-Request-Id` header. |
| `error.details` | object\|null | no | Code-specific. Each code's `details` shape is fixed in § 5; a code with no row has no `details`. **Nullable, and MAY be omitted when null.** |
| `error.session` | SessionSnapshot\|null | no | Present **iff** a session is open at the moment the error is produced **and the caller is authenticated**. MUST be present for every 409 on a `/session/*` endpoint. MUST be omitted (or `null`) on `401`, `403` and `503` — see below. **Nullable, and MAY be omitted when null.** |

**Absent and `null` mean the same thing.** `details` and `session` are the only optional members, both are
nullable, and a writer MAY either emit `"details": null` or leave the key out — the two are the same
statement, and different parts of the server legitimately do it differently (a shorter wire versus an
explicit shape). **Clients MUST treat an absent key and a `null` value identically** and MUST NOT infer
anything from which form arrived. In particular, "a snapshot came with this refusal" is **not** a signal:
the server attaches one to essentially every refusal raised while a session is open, so its presence says
nothing about what went wrong. Branch on `error.code`.

The three required members — `code`, `message`, `requestId` — are never null and never absent. That is
what "there is no second error shape" means: one envelope, three mandatory fields, two optional ones whose
absence is their null.

Rules:

- `error.message` MUST NOT contain a stack trace, an exception type name, or a filesystem path
  outside the folder the caller already named.
- An unhandled exception MUST be converted to `500 internal_error` with a generic message. The
  detail goes to the server log keyed by `requestId`.
- The envelope is never used for 2xx. A successful action returns a `SessionSnapshot`, a media body,
  or `204 No Content`.
- **`error.session` MUST be omitted on `401` and `403`.** The snapshot carries the open folder's
  absolute path and its contents. Attaching it to an authentication failure would hand exactly what
  the token exists to protect to a caller who has just failed to present one. The "iff a session is
  open" rule is conditioned on the caller being authenticated, and this outranks it.
- **`error.session` MUST be omitted on `503 session_busy`.** Materialising a snapshot requires the
  session lock, and not holding it *is* the failure being reported.
- **`/media/*` errors always carry `error.session: null`** (or omit it), whether or not a session is
  open. The media endpoints are deliberately lock-free — that is what lets a picture load while a vote is
  being saved — and a snapshot can only be materialised under the session lock (§ 7.3). Taking the lock to
  decorate a `404 media_file_missing` would trade the whole point of the lock-free media path for a field
  no client reads. This is a stated exception to the "iff a session is open" rule above, not a defect.

---

## 5. Error codes

Stable. New codes MAY be added; existing codes MUST NOT change meaning or status.

### 5.1 Authentication and pairing

| Code | Status | When | `details` |
|---|---|---|---|
| `unauthenticated` | 401 | no `Authorization` header, or not `Bearer` | — |
| `invalid_token` | 401 | unknown or expired bearer token | — |
| `token_revoked` | 401 | the device was revoked | — |
| `invalid_pairing_code` | 401 | `POST /pair` with a wrong, used or expired code | `{ "attemptsRemaining": int }` |
| `pairing_not_open` | 403 | `POST /pair` while no pairing window is open | — |
| `too_many_requests` | 429 | pairing rate limit tripped | `{ "retryAfterSeconds": int }` |

### 5.2 Request shape

| Code | Status | When | `details` |
|---|---|---|---|
| `invalid_request` | 400 | body is not valid JSON, or a field has the wrong JSON type | `{ "field": string }` |
| `missing_field` | 400 | a required field is absent or `null` | `{ "field": string }` |
| `invalid_side` | 400 | `side`/`winner` is not `left` or `right` | `{ "field": string, "value": string }` |
| `invalid_media_id` | 400 | the `{id}` segment is not a legal media id (§ 11) | `{ "reason": string }` |
| `unsupported_width` | 400 | `w` is not in the allowed set | `{ "requested": int, "allowed": [int] }` |
| `unsupported_format` | 400 | `format` is neither `jpeg` nor `webp` | `{ "allowed": ["jpeg","webp"] }` |
| `invalid_path` | 400 | `path`/`folder` is empty, relative, or contains a NUL | `{ "field": string }` |
| `unsupported_content_type` | 415 | request `Content-Type` is not `application/json` | — |
| `payload_too_large` | 413 | JSON body over 65536 bytes | `{ "maxBytes": 65536 }` |
| `not_found` | 404 | no such route | — |

### 5.3 Session and folder

| Code | Status | When | `details` |
|---|---|---|---|
| `no_session` | 404 | any `/session*` or `/media/*` call while no session is open | — |
| `session_already_open` | 409 | `POST /session` naming a **different** folder than the open one | `{ "openFolder": string }` |
| `folder_not_found` | 404 | the folder does not exist | `{ "folder": string }` |
| `folder_not_a_directory` | 400 | the path exists but is a file | `{ "folder": string }` |
| `folder_access_denied` | 403 | the OS refused to enumerate it | `{ "folder": string }` |
| `folder_not_rankable` | 409 | `RankingSession.Start()` returned `false` — fewer than 2 eligible files (`SPEC.md` § Folder) | `{ "stills": int, "videos": int, "rankable": int }` |
| `library_json_unreadable` | 409 | `rankmaster_db.json` exists and does not parse. The file MUST NOT be overwritten (`SPEC.md` § Persistence) | `{ "file": string }` |
| `folder_locked` | 423 | `<folder>/.rankmaster.lock` is held elsewhere | `{ "holder": null }` — see § 5.3.1 |
| `session_busy` | 503 | the session semaphore was not acquired within 5 s | `{ "retryAfterSeconds": int }` |
| `server_shutting_down` | 503 | shutdown in progress | `{ "retryAfterSeconds": int }` |

#### 5.3.1 Why `holder` is always null

The lock is opened `FileShare.None`, which is the point: it is what stops a second writer. That same
exclusivity means **no other process can open the file to read who holds it**, so the holder record
the lock file contains is unreadable by definition. Confirmed against all three share modes.

`details.holder` is therefore `null`, always. It is kept in the shape so a future design can fill it
— which would mean either giving up exclusivity, or moving the holder record to a second, readable
file beside the lock. Do not implement a best-effort read here; it cannot succeed and would only
produce a misleading error path.

### 5.4 Pair and actions

| Code | Status | When | `details` |
|---|---|---|---|
| `stale_pair_token` | 409 | the supplied `pairToken` is not the current one (§ 8) | `{ "suppliedToken": string, "currentToken": string\|null }` |
| `no_current_pair` | 409 | the session is open but `state` is `exhausted` — there is no pair to act on | — |
| `nothing_to_undo` | 409 | `LibraryActions.UndoLastMove()` returned `false` | — |
| `undo_folder_changed` | 409 | the recorded move belongs to a different folder than the open session | `{ "moveFolder": string }` |
| `move_failed` | 500 | the file move for discard/special/undo threw | `{ "id": string, "stage": "move"\|"undo-move" }` |
| `save_failed` | 500 | `JsonCatalog.Save` threw | `{ "recordsChanged": bool, "fileMoved": bool }` |

### 5.5 Media

| Code | Status | When | `details` |
|---|---|---|---|
| `unknown_media_id` | 404 | the id is not a record in the open session | `{ "id": string }` |
| `media_file_missing` | 404 | the id is a record but the file is gone from disk | `{ "id": string }` |
| `media_outside_session` | 403 | the decoded id escapes the session folder (separator, `..`, rooted path) | `{ "id": string }` |
| `media_extension_not_allowed` | 403 | the extension is in neither list in `SPEC.md` § Media policy | `{ "id": string, "extension": string }` |
| `wrong_media_kind` | 409 | `/still` or `/thumb` on a video, or `/video` on a still | `{ "id": string, "kind": "still"\|"video", "endpoint": string }` |
| `media_decode_failed` | 422 | the file exists but could not be turned into an image **now** — see `reason` | `{ "id": string, "reason": "unreadable" \| "no_room" }` |
| `range_not_satisfiable` | 416 | the `Range` header cannot be satisfied | `{ "sizeBytes": int }` |

### 5.6 Server

| Code | Status | When |
|---|---|---|
| `internal_error` | 500 | anything unhandled |

### 5.7 Rename (§ 10.16)

| Code | Status | When | `details` |
|---|---|---|---|
| `rename_in_progress` | 409 | a mutating `/session*` call, or a second `POST /session/rename`, while a rename runs | `{ "operationId": string }` |
| `no_rename_operation` | 404 | `GET`/cancel of `/session/rename` with a session open but no rename recorded | — |
| `rename_failed` | 500 | the rename (or recovery on open) could not reunite the ratings | `{ "reunited": bool, "journal": string\|null }` |

`reunited: true` means the ratings are safe (folder possibly half-renamed); `reunited: false` is the
rare double fault — the reunite `Save` itself failed (e.g. disk full), the journal is left at
`journal` and the next open will retry recovery. `POST /session` runs recovery first and, if it
cannot reunite, returns `rename_failed { reunited:false, journal }` and does **not** open the session
(so nothing scans-and-drops before the ratings are safe).

---

## 6. Status codes

| Status | Used for |
|---|---|
| 200 | successful `GET`; `POST /session` resuming the session already open; every successful action |
| 201 | `POST /session` that opened a **new** session; `POST /pair` |
| 202 | `POST /session/rename` (§ 10.16) — accepted, **not** a completion; durability is asserted only when the operation reaches `succeeded` (§ 13.1) |
| 204 | `DELETE /session`, `DELETE /pair/{deviceId}` |
| 206 | `GET /media/{id}/video` with a satisfiable `Range` |
| 304 | media `GET` whose `If-None-Match` matches the current ETag |
| 400 | malformed request — § 5.2 |
| 401 | no credential, bad credential, revoked credential — § 5.1 |
| 403 | the credential is fine but the operation is refused: media outside the session folder, a disallowed extension, an OS-denied folder, pairing not open |
| 404 | no such route; no session open; unknown media id; missing file; missing folder; no rename operation recorded (`no_rename_operation`, § 10.16) |
| 409 | the request is well-formed and authorised but conflicts with the current state: stale token, no current pair, nothing to undo, folder not rankable, unreadable library JSON, a session already open on another folder, wrong media kind, a rename already running (`rename_in_progress`, § 10.16) |
| 413 | body too large |
| 415 | wrong request `Content-Type` |
| 416 | unsatisfiable `Range` |
| 422 | the file is present but cannot be decoded as an image |
| 423 | the folder's lock file is held by another process |
| 429 | pairing rate limit |
| 500 | `save_failed`, `move_failed`, `internal_error`, `rename_failed` (§ 10.16) |
| 503 | `session_busy` (with `Retry-After`), `server_shutting_down` (with `Retry-After`) |

`503` and `429` MUST carry a `Retry-After` header in delta-seconds.

**There is no `405`.** A request to a path this API knows, with a method it does not support for that
path, is answered **`404 not_found`**, in the standard envelope, and **no `Allow` header is promised** —
the server does not advertise which methods a path would accept. This is the contract, not an accident of
the router. A `405` row would mean a new status code in three programs for a case no client of this API
reaches: the clients are written against this document, they do not discover routes, and a wrong method is
therefore a bug in a client build, not a runtime condition to negotiate. "This path with this method is
not part of the API" is exactly what `404 not_found` says.

**403 vs 404 for media.** A 404 says "not part of this session"; a 403 says "you tried to leave the
session folder". Both are safe to return in full detail: the caller already holds a token that can
browse the whole filesystem (`SERVER_PLAN.md` § 3.6), so there is nothing to conceal.

---

## 7. The session state machine

Exactly one session exists server-wide (`SERVER_PLAN.md` § 7). It is a `RankingSession` plus the
folder lock, a semaphore, a per-session secret, `pairSeq` and `lastAction`.

### 7.1 States

| State | Meaning | Observable as |
|---|---|---|
| `closed` | no session object exists | every `/session*` and `/media/*` call → `404 no_session` |
| `ranking` | session open, `RankingSession.Current` is non-null | `state: "ranking"`, `pairToken` non-null |
| `exhausted` | session open, `Current` is null | `state: "exhausted"`, `pairToken` null |

`exhausted` is reachable only after `ranking`: a folder that cannot produce a first pair never opens
at all (`Start()` returns `false` → `409 folder_not_rankable`, § 10.1). It is reached when discards
leave fewer than two eligible records, and it is escapable only by `POST /session/undo` restoring a
file, or by closing and reopening the session.

### 7.2 Transitions

| From | Call | To | `pairSeq` | Notes |
|---|---|---|---|---|
| closed | `POST /session` ok | ranking | set to 0 | `Start()` returned true |
| closed | `POST /session` unrankable | closed | — | lock MUST be released before responding |
| ranking | `POST /session` same folder | ranking | unchanged | pure read; `Start()` MUST NOT be called again |
| ranking\|exhausted | `POST /session` other folder | unchanged | unchanged | `409 session_already_open` |
| ranking\|exhausted | `GET /session`, `GET /session/pair` | unchanged | unchanged | pure reads |
| ranking\|exhausted | `POST /session/save` | unchanged | unchanged | writes JSON, never touches the pair |
| ranking | `POST /session/vote` ok | ranking or exhausted | +1 | save, then advance |
| ranking | `POST /session/skip` ok | ranking or exhausted | +1 | save, then advance |
| ranking | `POST /session/discard`, `/special` ok | ranking or exhausted | +1 | move file, `Drop`, save |
| ranking\|exhausted | `POST /session/undo` ok | ranking | +1 | move file back, `Restore`, save |
| ranking\|exhausted | `DELETE /session` | closed | — | releases the lock; writes nothing |
| ranking\|exhausted | `DELETE /session` **during a rename** | unchanged | unchanged | `409 rename_in_progress` — refused; cancel the rename first (§ 10.4, § 10.16) |
| exhausted | vote / skip / discard / special | exhausted | unchanged | `409 no_current_pair` |
| ranking\|exhausted | `POST /session/rename` started | unchanged | unchanged | `202`; every other mutating call answers `409 rename_in_progress` until it settles (§ 10.16) |
| ranking\|exhausted | rename succeeded | ranking or exhausted | +1 | every id new; `lastAction.type == "rename"` |
| ranking\|exhausted | rename cancelled or failed | ranking or exhausted, **resynced from disk** | **+1** | same `sessionId`; records re-read, `Current` re-picked, `sessionVotes` and `cues` **kept**, undo cleared, `lastAction` null; ratings intact, filenames possibly mixed (§ 10.16). A cancel in `preparing` changes nothing at all |

### 7.3 Serialisation

Every mutating call and every call that reads a snapshot MUST hold the session semaphore. The
snapshot returned to the client MUST be materialised **inside** the lock, so two concurrent requests
can never observe a half-applied action. If the semaphore is not acquired within **5 seconds** →
`503 session_busy`, `Retry-After: 1`.

### 7.4 What the state machine cannot do

These are properties of `RankingSession`, stated so no client is promised otherwise.

1. **The pair does not necessarily change after a vote.** `Advance()` sets `Current = null` before
   `Pick()` precisely so that a 2–3 file library can re-pair (`SPEC.md` § Prefetch). With exactly two
   eligible files the next pair is the same two ids, possibly swapped, forever. `pairToken` changes
   anyway — this is why the token MUST include `pairSeq` and MUST NOT be a hash of the ids alone.
2. **Warm pairs may be one vote stale.** `FillWarm()` picks against the ratings at the time of
   filling, and `Advance()` dequeues a warm pair before re-picking. A warm pair may therefore not be
   the pair `PairSelector` would choose today. This is intended (`SPEC.md` § Prefetch).
3. **Warm pairs are not promises.** A discard drops every warm pair containing the discarded id;
   `Restore` (undo) re-picks `Current` from scratch. A warm pair may never become current.
4. **Undo always replaces the current pair.** `RankingSession.Restore` sets `Current = null` and
   picks again, so the pair on the client's screen is thrown away even though it was valid. This is
   required behaviour (`RankingSessionTests.Restore_WhileAPairIsOnScreen_DoesNotKeepOldPairReserved`)
   and the contract MUST NOT pretend undo is pair-preserving.
5. **A vote is rolled back in full if the save throws.** `ApplyVote` snapshots records, `Current`,
   `SessionVotes`, warm queue, recent set and cues; on any exception it restores all of them. The
   server therefore MUST NOT advance `pairSeq` on that path, and the client's token stays valid.
6. **`Drop` and `Restore` do not save and cannot roll back.** They mutate in-memory state
   immediately. The server calls them through `LibraryActions`, which moves the file *first*.
7. **`Drop` clears the pair when fewer than two records remain in total**, not fewer than two
   *eligible* records. In a mixed folder (stills rank, videos do not — `SPEC.md` § Media policy) the
   session can be `exhausted` while `records.Count` is large. Both routes to `exhausted` look
   identical to the client.
8. **The session never rescans.** Files added to the folder after `POST /session` are invisible
   until the session is closed and reopened. Files removed externally stay in `Records` and produce
   `404 media_file_missing` (§ 11.3).
9. **A discard does not mark the survivor as recently shown and does not count an impression for
   either item.** The survivor MAY appear again in the very next pair. This is asserted by
   `RankingSessionTests.Drop_RemovesFileAndDoesNotCountTheOtherAsSeen`.
10. **`impressions` increments only on vote and skip** (`SPEC.md` § Ranking). Reading a pair,
    fetching a still, prefetching a warm pair, and closing the session MUST NOT increment it. The
    pair open when `DELETE /session` arrives is **unseen**.
11. **A rename does not change `sessionId`.** On success it advances the session one generation —
    every numeric id new, `pairSeq` +1, `cues` cleared, `undoAvailable` false. On cancel or failure
    the session is resynced from disk with ratings intact; filenames may be mixed. See § 10.16.

---

## 8. `pairToken`

### 8.1 What it is

An opaque, server-generated, per-pair string. It is an **optimistic-concurrency tag and a
duplicate-action guard**, not a security credential — authorisation is the bearer token alone.

Clients MUST treat it as an opaque string: never parse it, never derive one, never reuse one across
`sessionId` values, never compare two tokens for anything but equality.

### 8.2 How it is generated

Each session holds a `sessionSecret` of 32 cryptographically random bytes, created when the session
opens and never leaving the process.

```
mac      = HMAC-SHA256(sessionSecret,
                       sessionId || 0x1F || decimal(pairSeq) || 0x1F || left.Filename || 0x1F || right.Filename)
pairToken = base64url(mac) truncated to the first 22 characters      // 132 bits, no padding
```

- All string inputs are UTF-8, unnormalised, exactly as `MediaId.Filename` holds them.
- `pairSeq` is a 64-bit unsigned integer, decimal, no leading zeros, starting at **0** when the
  session opens.
- When `state` is `exhausted`, `pairToken` is `null`. There is nothing to sign.

Deriving the token rather than drawing a random one is REQUIRED: it makes the token a pure function
of session state, so a rolled-back vote (§ 7.4.5) automatically yields the token the client already
holds, with no separate undo bookkeeping.

### 8.3 When `pairSeq` changes

`pairSeq` MUST be incremented by exactly 1, and never by anything else, under this rule:

> Increment if and only if the server **entered** one of `VoteLeft`, `VoteRight`, `Skip`, `Drop` or
> `Restore`, **and**, for the three that save internally (`VoteLeft`, `VoteRight`, `Skip`), that
> call returned without throwing.

The asymmetry is deliberate and mirrors the library:

| Path | Outcome | `pairSeq` | Client's token |
|---|---|---|---|
| vote/skip succeeds | pair advanced; records saved within the bound of § 13.1 | +1 | stale |
| vote/skip cannot be saved (the write failed and is latched, § 13.1) | nothing applied — full in-memory rollback (§ 7.4.5), `500 save_failed` | unchanged | **still valid — retry with the same token** |
| discard/special: move throws | nothing changed | unchanged | still valid |
| discard/special: move ok, `Drop` ok, `Save` throws | file already moved, record already gone | **+1** | stale |
| undo of a vote/skip: succeeds | the snapshot is restored and the pair it consumed is current again | **+1** | stale |
| undo of a vote/skip: `Save` throws | full in-memory rollback — the action stays applied | unchanged | still valid |
| undo of a move: move-back throws | nothing changed | unchanged | still valid |
| undo of a move: move-back ok, `Restore` ok, `Save` throws | file already back, record already restored | **+1** | stale |
| `POST /session/save`, any `GET` | — | unchanged | still valid |
| rename succeeds | database rewritten with new keys, session replaced | **+1** | stale — every id is new |
| rename cancelled or failed after the journal was written | session resynced from disk (§ 10.16) | **+1** | stale — the records were re-read and the pair re-picked, and saying the generation changed is the honest answer |
| rename cancelled in `preparing` (nothing moved) | session untouched | unchanged | still valid |

`pairSeq` never decreases and never resets while a session is open. It resets to 0 only when a new
session opens — and `sessionId` changes at the same moment, so old tokens from a previous session can
never collide with new ones. A rename does neither: it does not reset `pairSeq` and does not change
`sessionId`, even on success (§ 10.16).

### 8.4 Checking a token

Every mutating pair action (`vote`, `skip`, `discard`, `special`) MUST validate in this exact order,
inside the session lock, before touching anything:

1. No session → `404 no_session`.
2. Body invalid → `400` per § 5.2.
3. `state == "exhausted"` → `409 no_current_pair`.
4. `pairToken != current` → `409 stale_pair_token`.
5. Apply.

`POST /session/undo` takes **no** `pairToken` — see § 10.10.

### 8.5 Exactly what happens on a stale token

A stale token produces **no state change of any kind**: no save, no move, no impression, no advance,
no `pairSeq` bump, no lock file touched. It is a pure read.

The response is `409` with `error.code = "stale_pair_token"` and:

- `error.details.suppliedToken` — echoed verbatim.
- `error.details.currentToken` — the live token, or `null` when exhausted.
- `error.session` — **the complete `SessionSnapshot` (§ 9)**, identical byte-for-byte to what
  `GET /session` would return at that instant.

Because the snapshot includes `pair`, `pairToken`, `warmPairs`, `counts`, `cues`, `sessionVotes`,
`undoAvailable` and `lastAction`, the client is fully resynchronised by this single response. A
client MUST NOT poll `GET /session` after a 409.

**Deciding whether the lost request landed.** `lastAction` (§ 9.4) records the most recent successful
mutation, including the `pairToken` it consumed and the `clientRequestId` the caller sent. On a 409:

| Comparison | Conclusion | Client action |
|---|---|---|
| `lastAction.clientRequestId` equals the id the client just sent | that exact request landed once | continue from the snapshot; MUST NOT retry |
| `lastAction.pairToken` equals the token the client just sent, but the `clientRequestId` differs | the token was consumed by a **different** earlier request of the client's own | inspect `lastAction.type`; continue from the snapshot; MUST NOT retry |
| neither matches | the request never landed, and the pair has since moved on for another reason | continue from the snapshot; MUST NOT replay the old token |
| `lastAction` is `null` | the session was opened, or the server restarted, since the action | outcome unknown — see § 13.4 |

### 8.6 Lifetime

A token is valid from the moment `pairSeq` reaches its value until the next increment. There is no
expiry clock, no grace window, and no history: the server validates against the current token only.
The server MUST NOT accept a token from an earlier `pairSeq` under any circumstance, including an
obvious retry.

---

## 9. The session snapshot

**Every** 2xx JSON response from `POST /session`, `GET /session`, `GET /session/pair`,
`POST /session/save`, `POST /session/vote`, `/skip`, `/discard`, `/special`, `/undo` is the same
object, with the same fields, in the same shape. There is no "small" variant and no partial update.
`error.session` in a 409 is the same object. Four implementations, one shape.

### 9.1 `SessionSnapshot`

| Field | Type | Null? | Meaning |
|---|---|---|---|
| `sessionId` | string | no | Opaque, 22 chars, base64url. New on every `POST /session` that opens a session, and therefore new after a server restart. A change means every cached token, link and `lastAction` from before is void. |
| `state` | enum | no | `ranking` \| `exhausted` (§ 7.1). |
| `folder` | string | no | The absolute, fully-resolved folder path, as `Path.GetFullPath` produced it. |
| `folderName` | string | no | `RankingSession.FolderName` — the last path segment with trailing separators trimmed. May be empty for a drive root. |
| `policy` | enum | no | `still` \| `video`. `MediaExtensions.RankPolicy` over every record. A mixed folder is `still` (`SPEC.md` § Media policy). |
| `openedAt` | string | no | RFC 3339 UTC, when this session opened. |
| `prefetchPairs` | integer | no | The session's configured warm depth. Default **2** (`SPEC.md` § Pipeline). `warmPairs` never exceeds it. |
| `sessionVotes` | integer | no | `RankingSession.SessionVotes`. Votes only; skip, discard, special and undo do not count. Reset only by a new session. |
| `counts` | Counts | no | § 9.2. |
| `progress` | number | no | `LibraryProgress.Of(Rankable)`, 0…1, rounded to 6 decimals. Overlay maths only; not a picker input (`SPEC.md` § Ranking). |
| `progressPercent` | integer | no | `round(progress * 100)`, 0…100 — the number the desktop overlay shows. |
| `cues` | array of enum | no | Up to 10 of `confirmation` \| `upset`, **oldest first**, exactly `RankingSession.RecentCues`. Session-only; never written to JSON (`SPEC.md` § Match strip). Empty until the first vote. Skip, discard, special and undo append nothing. |
| `pair` | Pair | **yes** | The current pair, or `null` when `state` is `exhausted`. § 9.3. |
| `pairToken` | string | **yes** | § 8. `null` iff `pair` is `null`. |
| `pairSeq` | integer | no | Monotonic, starts at 0 (§ 8.3). Exposed for logging and for ordering two snapshots; it is **not** a substitute for `pairToken` and MUST NOT be sent in an action body. |
| `warmPairs` | array of Pair | no | 0…`prefetchPairs` entries, in the order they will be dequeued. **Prefetch hints only** — see § 9.5. |
| `undoAvailable` | boolean | no | There is an action to cancel (§ 10.10): the session has applied a `vote`, `skip`, `discard` or `special` that has not already been cancelled, and for a move, its folder matches the open session. A `POST /session/undo` while this is `false` returns `409 nothing_to_undo`. |
| `lastAction` | LastAction | **yes** | § 9.4. `null` immediately after a session opens. In-memory only. |
| `lastSavedAt` | string | **yes** | RFC 3339 UTC of the last successful `JsonCatalog.Save` by this session, or `null` if this session has not saved yet. |

### 9.2 `Counts`

| Field | Type | Meaning |
|---|---|---|
| `total` | integer | `RankingSession.Records.Count` — every media file the scan found, both kinds. |
| `rankable` | integer | `RankingSession.Rankable.Count` — records matching `policy`. |
| `unranked` | integer | `RankingSession.UnrankedCount` — rankable records with `matches == 0`. The overlay's "unranked" number. |
| `stills` | integer | records with `kind == "still"`. |
| `videos` | integer | records with `kind == "video"`. |

`stills + videos == total`. `rankable == stills` when `policy` is `still`, else `videos`.

### 9.3 `Pair` and `MediaRef`

```json
{
  "left":  { "id": "DSC_0123.jpg", "kind": "still", "sizeBytes": 4210332, "mediaVersion": "9f2a1c77b0e4d310",
             "links": { "meta": "/api/v1/media/DSC_0123.jpg/meta",
                        "still": "/api/v1/media/DSC_0123.jpg/still?v=9f2a1c77b0e4d310",
                        "thumb": "/api/v1/media/DSC_0123.jpg/thumb?v=9f2a1c77b0e4d310",
                        "video": null } },
  "right": { "…": "…" }
}
```

| Field | Type | Null? | Meaning |
|---|---|---|---|
| `id` | string | no | The **decoded** filename, exactly as on disk (`MediaId.Filename`). Not URL-encoded. § 11. |
| `kind` | enum | no | `still` \| `video`, from the extension (`MediaExtensions.KindOf`). |
| `sizeBytes` | integer | **yes** | File length at snapshot time; `null` if the file could not be stat'd (it has been moved or deleted under the session — § 7.4.8). |
| `mediaVersion` | string | **yes** | 16 lowercase hex chars, § 12.2. `null` when `sizeBytes` is `null`. |
| `links` | object | no | Server-built, already percent-encoded, already carrying `v=`. Clients SHOULD use these verbatim rather than building URLs, which removes every encoding bug from four client implementations at once. `links.video` is `null` for a still; `links.still` and `links.thumb` are `null` for a video (no poster frames — `SERVER_PLAN.md` § 7). |

`left` and `right` are never equal; `PairSelector` never returns an id twice.

**Ratings are deliberately not exposed in `Pair`.** The desktop app shows no μ, σ or score on the
compare screen, and a client that displayed them would bias the vote it is collecting. `μ`/`σ` are
available per item from `GET /media/{id}/meta` for tooling; the match strip (`cues`) is the only
rating signal on the ranking surface, exactly as in `SPEC.md`.

### 9.4 `LastAction`

The most recent **successful** mutation of this session. Used for retry disambiguation (§ 8.5).

| Field | Type | Null? | Meaning |
|---|---|---|---|
| `seq` | integer | no | The `pairSeq` value this action produced. |
| `type` | enum | no | `vote` \| `skip` \| `discard` \| `special` \| `undo` \| `drop_missing` \| `rename` (§ 10.16 — every other field null). |
| `pairToken` | string | **yes** | The token this action consumed. `null` for `undo` and `drop_missing`, which do not take one. |
| `clientRequestId` | string | **yes** | Echoed verbatim from the request body, or `null` if the client sent none. |
| `winner` | enum | **yes** | `left` \| `right` — `vote` only. |
| `side` | enum | **yes** | `left` \| `right` — `discard` and `special` only. |
| `id` | string | **yes** | The media id the action moved or dropped — `discard`, `special`, `drop_missing`. |
| `restoredId` | string | **yes** | `undo` of a move only. The id the file came back as, which **differs** from `id` when the original name was taken and `FileOps.UniqueFileName` produced `name (2).ext`. `null` when the cancelled action moved no file. |
| `undoneType` | enum | **yes** | `undo` only: which action was cancelled — `vote` \| `skip` \| `discard` \| `special`. `null` for every other type. It is what lets a client say "vote taken back" rather than a bare "undone". |
| `at` | string | no | RFC 3339 UTC. |

`lastAction` is in-memory and is lost on restart. § 13.4 says what that costs.

### 9.5 Warm pairs

`warmPairs` mirrors `RankingSession.WarmPairs`, oldest first — the order `Advance()` will dequeue.
Normative client rules:

- A warm pair has **no token** and MUST NOT be acted on.
- A warm pair MAY be one vote stale (§ 7.4.2) and MAY never become current (§ 7.4.3).
- It exists so the client can prefetch bytes exactly as `MediaPipeline` does (`SPEC.md` § Pipeline).
- Prefetching a warm pair MUST NOT be treated as an impression, by client or server.
- `warmPairs` is frequently **empty** and that is not an error. With exactly two eligible files it is
  always empty: every id is reserved by `Current`, so `Pick()` has nothing left to return.

---

## 10. Endpoints

### 10.1 `POST /session`

Open or resume the one session.

```json
{ "folder": "D:\\photos\\trip" }
```

| Field | Type | Required |
|---|---|---|
| `folder` | string, absolute path | yes |

Order of operations:

1. Validate `folder`: non-empty, rooted, no NUL → else `400 invalid_path`. Resolve with
   `Path.GetFullPath` and trim trailing separators.
2. If a session is open, compare the resolved path with `session.folder` — `OrdinalIgnoreCase` on
   Windows, `Ordinal` elsewhere.
   - Equal → **`200`** with the live snapshot. `Start()` MUST NOT be called again: it clears cues,
     `SessionVotes` and the recent-shown set, which would silently discard a resuming phone's
     session state. This call is a pure read and MUST NOT change `pairSeq`.
   - Different → `409 session_already_open`, `details.openFolder`. The client MUST
     `DELETE /session` first. The server MUST NOT close the open session implicitly.
3. Folder missing → `404 folder_not_found`; a file → `400 folder_not_a_directory`; unreadable →
   `403 folder_access_denied`.
4. Acquire `<folder>/.rankmaster.lock` (§ 10.4). Failure → `423 folder_locked`.
5. `RankingSession.Start()`.
   - Throws `InvalidDataException` from `JsonCatalog.LoadRequired` → release the lock →
     `409 library_json_unreadable`. The server MUST NOT write the file on this path, at any later
     point in the request, or during cleanup (`SPEC.md` § Persistence).
   - Returns `false` → release the lock, discard the session → `409 folder_not_rankable` with
     `details` counts.
   - Returns `true` → `sessionId` and `sessionSecret` generated, `pairSeq = 0`, `lastAction = null`
     → **`201`** with `Location: /api/v1/session`.

`Start()` does **not** save. A freshly opened session has `lastSavedAt: null` even though
`rankmaster_db.json` may be older — that field describes this session's writes, not the file's age.

### 10.1.1 Opening a pairing window

**Opening a window is deliberately not an HTTP operation.** If it were, anyone who can reach the port
could open one and then spend the rest of the day guessing at it. Opening a window MUST require
control of the owner's OS account, and there are two ways in:

1. **Automatic at startup** when no device is yet enrolled, so a fresh install is reachable.
2. **A sentinel file** in the server's data directory, polled by the server. Creating it requires
   write access to that directory, which is the OS-account check.

Both files are **named normatively**, because three separate clients guessed differently at them:

| Path | Role |
|---|---|
| `<data>/pair.request` | Sentinel. Create it to ask for a window; the server deletes it on pickup, and **ignores one older than 30 seconds** — a request left behind by a crash must not open a window later with nobody watching |
| `<data>/pairing.json` | The published offer: `code`, `expiresAt`, `host`, `port`, `fingerprint`. Owner-only (`0600`) |
| `<data>/revoke.request` | The same channel used to revoke a device the caller is not (§ 10.12) |

`<data>` is the server data directory (§ 2.4). The offer is what `rm2ctl` renders as text and as a
QR code.

The client MUST pin the fingerprint **before** sending the code. The code is a bearer secret: handing
it to an unverified TLS peer hands it to whoever answered.

### 10.2 `GET /session`

The snapshot. Pure read. `404 no_session` when closed — a client that wants to know whether a
session exists without a 404 SHOULD read `session` from `GET /ping` instead.

### 10.3 `GET /session/pair`

Returns the identical `SessionSnapshot`. It exists because `SERVER_PLAN.md` § 4 lists it and because
"give me the pair and the warm pairs" is the client's hot path; it is a convenience alias, not a
narrower resource. Pure read; never changes `pairSeq`; never counts an impression.

### 10.4 `DELETE /session`

Closes the session, releases the semaphore, closes and deletes `<folder>/.rankmaster.lock`, and
disposes cached decoders. `204` on success, `404 no_session` if none is open — a repeated DELETE is
therefore a 404 and MUST NOT be treated by the client as a failure.

**Refused while a rename is running:** `409 rename_in_progress` with `details.operationId`, like every
other mutating call (§ 10.16). Closing the session would release the folder lock while the rename task is
still moving files, which lets a second `POST /session` run journal recovery on a folder that is being
renamed underneath it. A client that wants to close during a rename cancels it first
(`POST /session/rename/cancel`), waits for the operation to reach `cancelled`, and then deletes the
session.

**It flushes unsaved choices, and writes nothing otherwise.** Before releasing the lock it forces the
write-behind of § 13.1 to disk, so a close is a durability point: no choice the owner made in this session
is lost by closing. Beyond that flush there is nothing to write — every move already saved before its
response (§ 13.2) — and the close itself adds nothing. This mirrors `Esc` in `SPEC.md`: the pair on screen
is unseen, and no *extra* catalog write happens. If the flush fails, the session still closes and the lock
is still released (a close that could be refused by a broken disk would be a wedge); the failure is logged
and the response is `204`, because there is nothing the client could usefully do about it at that point
and the choices were already reported as applied.

The lock file: `<folder>/.rankmaster.lock`, opened `FileMode.OpenOrCreate, FileAccess.ReadWrite,
FileShare.None`, held open for the life of the session, containing
`{"pid":int,"host":string,"process":string,"startedAt":"RFC3339","serverVersion":string}` written at
open. It is invisible to the library: `JsonCatalog` only admits files whose extension is in the
still or video list, so `.lock` is never scanned, never ranked and never appears as a media id.
A stale lock file left by a killed process is harmless — the OS releases the handle, and the next
`POST /session` re-opens it.

### 10.5 `POST /session/save`

Explicit `RankingSession.Save()` — the `Ctrl+S` equivalent. Always allowed while a session is open,
including in `exhausted`. Idempotent and always safe to retry. Never changes the pair, the token or
`pairSeq`. Updates `lastSavedAt`. `500 save_failed` on failure with `details.recordsChanged: false`.

**This is the way a client makes a point durable.** Since § 13.1 relaxed the per-choice promise, the
`200` from this endpoint is the strongest statement the server makes: every choice applied up to this
moment is fsynced and atomically in place on disk. A client that needs a durability point — before it
tells the owner it is safe to unplug the drive, before a test reads `rankmaster_db.json`, before anything
that depends on the file rather than on the API — calls this and waits for the `200`. A successful save
also **clears the latched save failure** of § 13.1, which makes this the recovery path after the disk
came back.

### 10.6 `POST /session/vote`

```json
{ "pairToken": "7tRbQ0…", "winner": "left", "clientRequestId": "1f0c…" }
```

| Field | Type | Required |
|---|---|---|
| `pairToken` | string | yes |
| `winner` | `left` \| `right` | yes |
| `clientRequestId` | string, ≤64 chars | no, but STRONGLY RECOMMENDED (§ 8.5) |

Validate per § 8.4, then call `VoteLeft()` / `VoteRight()`. Inside that one call the library, in
order: appends the cue (`Confirmation` if the winner's μ ≥ the loser's μ *before* the update, else
`Upset`), applies TrueSkill, sets `matches + 1`, `impressions + 1` and `lastPlayed = now` on **both**
records, increments `SessionVotes`, and advances the pair.

- Success → `200` with the new snapshot. `pairSeq` +1.
- **A vote is a *choice*, so its write is governed by § 13.1, not by this section.** Whether the JSON
  on disk already contains it depends on the bound configured there: with `SaveDelaySeconds = 0` it
  does, exactly as this section used to promise unconditionally; otherwise it is on disk no later
  than the bound, and `POST /session/save` (§ 10.5) is how a client makes the point durable on
  demand. This is the owner's decision of 2026-09-17 and the one place in the contract where a 2xx
  stopped meaning "already written".
- A write that throws → `500 save_failed`, `details.recordsChanged: false`. **`pairSeq` is unchanged
  and the client's `pairToken` is still current** — the client MAY retry the identical request. When
  the write was deferred, the failure latches (§ 13.1) and it is the *next* mutating call that is
  refused with nothing applied, because this one has already been answered.

The server MUST NOT reorder these steps and MUST NOT respond before `VoteLeft`/`VoteRight` returns.
It MAY batch the writes, within the bound § 13.1 fixes; `SPEC.md` § Persistence describes the
desktop app, which saves on every choice and is unaffected.

### 10.7 `POST /session/skip`

```json
{ "pairToken": "7tRbQ0…", "clientRequestId": "1f0c…" }
```

`RankingSession.Skip()`: `impressions + 1` on both, **no** rating change, **no** `matches` change,
**no** `lastPlayed` change, **no** cue, no `SessionVotes` change, advance. A skip is a choice, so its
write is governed by § 13.1 like a vote's; failure semantics are identical to vote (§ 10.6).

The Windows client no longer offers skip (the owner does not use it, 2026-09-17) and the phone never
did. The endpoint stays in the contract and keeps working: `rm2ctl` uses it, and removing a route
that costs nothing to keep would break a client for no gain.

### 10.8 `POST /session/discard`

```json
{ "pairToken": "7tRbQ0…", "side": "left", "clientRequestId": "1f0c…" }
```

Moves the named side of the **current pair** to `<folder>/discarded/`. The id is taken from the pair
the token names — the client never sends an id, so it cannot act on an item that is not on screen.

Order (`LibraryActions.Move`): release any server-side decode of the id → `FileOps.MoveToSubfolder`
(creates the folder on demand, uniquifies on collision as `name (2).ext`) → `RankingSession.Drop` →
`RankingSession.Save` → **carry the rating into the subfolder**.

**The rating travels with the photograph** (owner's decision, 2026-09-17). The moved file's row is
written into `<folder>/discarded/rankmaster_db.json` (and likewise `special 1/`), an ordinary v1
database keyed by filename — so the subfolder can be opened and ranked like any other folder, and
Rank Master 2 reads it with no schema change. Before this, the row was simply deleted on the next
save: a discard folder could not be audited, and `special 1/`, which holds the owner's *best*
pictures, destroyed exactly the judgements that had cost the most comparisons.

It is the **last** step on purpose. The library's own database is the one that must be right, so the
second write happens after it and can never cost it. The write is not swallowed: a carry that
silently did not happen is the loss this exists to prevent, and § 8.3's stage rule already reads a
throw at this point correctly — the record is gone from the session, so the answer is
`500 save_failed { recordsChanged: true, fileMoved: true }` with undo armed, which is exactly true.

`POST /session/undo` of a move rebuilds the subfolder's database from what is actually present,
dropping the row the returned file left behind. That half is best-effort: the rating is already back
in the library, so a stale row is untidiness, not loss.

| Outcome | Status | State |
|---|---|---|
| all steps ok | `200` | `pairSeq` +1; record gone; new pair or `exhausted` |
| the file is missing on disk | `200` | **special case below** |
| move throws | `500 move_failed`, `details.stage:"move"` | nothing changed, token still valid |
| move ok, `Save` throws | `500 save_failed`, `details.fileMoved:true, recordsChanged:true` | `pairSeq` **+1**; the file is in `discarded/` and the record is gone from memory; the JSON still lists it, and the next successful save will drop it because `Save` merges against what is on disk |

**Missing-file special case.** `SPEC.md` requires that an unreadable or vanished file does not crash
the session, and the desktop app answers a failed decode by calling `Drop`. The API has no separate
"drop" endpoint (`SERVER_PLAN.md` § 4 does not list one), so discard carries the behaviour: if the
source file does not exist when the move is attempted, the server MUST NOT call
`FileOps.MoveToSubfolder` (which would throw `FileNotFoundException`) and MUST instead call
`RankingSession.Drop(id)` and `Save()` directly, returning `200` with
`lastAction.type = "drop_missing"`. **No undo entry is recorded** — there is no file to put back —
so `undoAvailable` is left as it was. Without this, a file deleted out from under a session wedges
that pair permanently, because the pair cannot be voted (the media will not load) and cannot be
discarded (the move throws).

A file that is present but *corrupt* is not this case: the move succeeds and the discard is ordinary.

### 10.9 `POST /session/special`

Identical to § 10.8 in every respect except the destination, `<folder>/special 1/`, and
`lastAction.type = "special"`. `special 2` MUST NOT be created (`SPEC.md` § File actions).

### 10.10 `POST /session/undo`

```json
{ "clientRequestId": "1f0c…" }
```

Cancels the **last successful action of this session** — one level, no stack. An action is a `vote`,
`skip`, `discard` or `special`.

This is the endpoint a phone's cancel button calls. It covers votes because a mis-tap on a touch
screen is a real vote: without it the only way back from a wrong tap is to keep voting and hope the
ratings recover, which they do not.

**It takes no `pairToken`, deliberately.** `SERVER_PLAN.md` § 4 heads the action block with "every
action takes `pairToken`" but lists `undo` with no body; the two cannot both be honoured. Undo is not
pair-scoped — the action it reverses happened in a previous pair generation, so by construction the
client's token for that generation is already stale, and requiring it would make undo permanently
unusable. Sending `pairToken` in the body is allowed and MUST be ignored.

#### What is restored

| Cancelled | Ratings and counters | Files | The pair afterwards |
|---|---|---|---|
| `vote` | both records' `mu`, `sigma`, `matches`, `impressions` and `lastPlayed` exactly as they were; `sessionVotes` − 1; the match cue removed | none move | **the pair that vote consumed is current again** |
| `skip` | both records' `impressions` and `lastPlayed` as they were | none move | the pair that skip consumed is current again |
| `discard`, `special` | the record is restored | the file is moved back out of `discarded/` or `special 1/` | a freshly picked pair (§ 7.4.4) |

Ratings are restored **by snapshot, never by inverse arithmetic**. The server keeps the state as it
was immediately before the action and puts that back. A TrueSkill update is not invertible to the
last bit, and a rating that drifts a little every time someone cancels is worse than having no cancel
at all. This is the same snapshot `RankingSession` already takes to roll back a failed save
(§ 7.4.5), so cancelling exercises a path the engine has always had.

#### `undoAvailable` has exactly one source

There is **one undo point per session**, and `undoAvailable` is the answer to one question about it:

> `undoAvailable` is `true` **iff** the last thing that happened in this session was a `vote`, `skip`,
> `discard` or `special`, **and** nothing structural has happened since.

The structural events, each of which **clears the undo point**, are: `drop_missing` (§ 10.8), a
successful rename (§ 10.16), a **resync** after a cancelled or failed rename (§ 10.16), an `undo` itself,
and opening the session. There is no second source. In particular, a recorded file move is **not**
independently undoable: if the undo point is cleared, the move is not offered, however recently it
happened. `undoAvailable`, `lastAction` and what `POST /session/undo` actually does MUST always agree —
two sources that can disagree is how a record the owner discarded on purpose comes back.

`undoAvailable` is therefore `false`, and `POST /session/undo` answers `409 nothing_to_undo`, when:

- the session has applied nothing yet;
- the last action has already been cancelled — there is exactly one level;
- the last action was `drop_missing` (§ 10.8). The server dropped that record because its file had
  vanished from the folder; that is the server coping, not the user acting, and putting the record
  back only wedges the same pair again on the next pick. It clears the undo point rather than being
  skipped over: undo MUST NOT reach past it to an earlier action;
- a rename succeeded, or was cancelled or failed and the session was resynced. The snapshot the undo
  point held names files that may no longer be at those names.

A recorded move belonging to another folder answers `409 undo_folder_changed`, and the server clears
it so the stale offer disappears.

#### On success

`200` with the snapshot: `pairSeq` + 1, a **new** `pairToken`, `undoAvailable` now `false`,
`lastAction.type` = `undo`, and `lastAction.undoneType` naming what was cancelled.

**`pairSeq` still increases and the token still changes**, even when cancelling a vote puts the
identical pair back on screen. That is exactly what makes cancel safe against a retry: a vote that
was in flight when the cancel landed arrives holding the old token, is answered `409
stale_pair_token` with the current state, and is never applied a second time. Per § 13.3 the client
MUST NOT re-send it against a fresh token.

#### When the write fails

Cancelling a `vote` or `skip` rewrites `rankmaster_db.json` and moves no file. If that write throws,
the cancel is rolled back in full: the action stays applied, `pairSeq` does not move, the client's
token stays valid, and the reply is `500 save_failed` with `recordsChanged: false` and
`fileMoved: false`. **Cancelling a vote is all-or-nothing**, exactly like the vote it reverses.

Cancelling a move cannot be all-or-nothing, because the file is already back by the time the write is
attempted. Those two outcomes are unchanged and are the two `undo of a move` rows of § 8.3: if the
move back throws, nothing changed; if the move back succeeded and the write threw, the change is
committed and says so.

#### Two consequences the client MUST expect

1. **The current pair is replaced.** For a cancelled `vote` or `skip` it is replaced by the pair that
   action consumed — which is the whole point, since that is the pair you meant to vote differently.
   For a cancelled move it is a freshly picked pair.
2. **The restored id may differ from the discarded id.** If the original name has since been taken,
   `FileOps.UniqueFileName` gives the file back as `name (2).ext` and the record's id changes with
   it. `lastAction.id` holds the old id, `lastAction.restoredId` the new one. Any URL the client
   cached for the old id is dead.

`Restore` recovers the session from `exhausted` back to `ranking` when it brings the eligible count
back to two. So does cancelling the vote that exhausted it.

### 10.11 `POST /pair`

Unauthenticated. Exchanges a one-time pairing code (shown by `rm2ctl pair` as a QR code and a short
numeric code) for a long-lived device token.

```json
{ "code": "418 250", "deviceName": "Pixel 8" }
```

- The pairing window is opened out of band, is **single-use**, and expires after **5 minutes**.
- No window open → `403 pairing_not_open`. Wrong/used/expired code → `401 invalid_pairing_code`
  with `details.attemptsRemaining`.
- **The guess budget is 5 attempts per source address, per window.** Each address that guesses at a
  window has its own budget of five; `details.attemptsRemaining` is **that caller's** remaining count, not
  a global one. When an address exhausts its five, **that address alone is locked out of this window for
  the rest of its life**. The window, the code and `pairing.json` stay live for every other address — in
  particular for the owner's own devices. **No address, however many guesses it spends, can end the
  window for anyone else**; only a successful pairing, or the window's own five-minute expiry, closes it.
  One address cannot consume another's budget, so a stranger on the LAN cannot silently burn the owner's
  five tries out from under him while he is typing; his phone still has all five.
- This replaces an earlier per-window total of five counted across all addresses. That version made a
  six-digit code arithmetically safer and was the right call in isolation, but it handed any host on the
  LAN a one-packet denial of the owner's own pairing, repeatable forever. The per-address budget keeps the
  guessing cost where it belongs, because **the per-address rate limit still stands**: more than
  **5 attempts per minute per source address** → `429 too_many_requests` with `Retry-After`. An attacker
  with several addresses gets five guesses per address per minute against a code of a million values in a
  five-minute window, and cannot deny the owner his own pairing while doing it.
- An earlier build deleted `pairing.json` as soon as any one address spent its five. That kept the
  denial this section exists to prevent, and it made the tray report "too many wrong codes" on a
  **successful** pair, because the tray reads the offer file disappearing as the window having died.
  The offer file now disappears only on a successful pairing or at shutdown, so its absence means what
  the tray always claimed it meant.
- Success → `201` with `{ deviceId, deviceName, token, issuedAt, expiresAt }`. `expiresAt` is `null`
  for a non-expiring token. The token is returned **once** and is never readable again.
- Whitespace in `code` MUST be ignored when comparing. The comparison MUST be constant-time.

### 10.12 `DELETE /pair/{deviceId}`

Authenticated. Revokes a device token; the revoked device's next call gets `401 token_revoked`.
`204` on success. Revocation does **not** close an open session — `DELETE /session` is the only thing
that does.

**A device may revoke only itself.** `deviceId` MUST equal the authenticated caller's own device, and
any other value answers `404 not_found` — the same answer an id that does not exist gets, so the route
never confirms which devices are enrolled. This is the "Forget this PC" button, and nothing more.

Without that rule any enrolled device could unpair any other: a phone could unpair the PC client, and
a device paired once and forgotten could unpair everything. Whoever holds one token would decide who
else is allowed in, which is not a decision a token should carry.

**Revoking a different device is the owner's, and goes through his OS account**, the same proof § 10.1.1
already uses for opening a window:

| Path | Role |
|---|---|
| `<data>/revoke.request` | One line of UTF-8: the `deviceId` to revoke. The server polls at the same one-second cadence as `pair.request`, revokes the device it names, and deletes the file. A request naming an unknown or already-revoked device is consumed and ignored |

Writing that file needs write access to the server's data directory, which is the OS-account check.
A re-enrolling client that wants to retire its own previous identity does **not** need it: it still
holds the old token at that moment, so it authenticates as the device being retired and the ordinary
self-revoke applies.

### 10.13 `GET /ping`

Liveness, version, capabilities and the certificate fingerprint for pinning. Cheap; safe to poll.
See § 14 for the body.

### 10.14 `GET /libraries/roots`

Drive roots (Windows) or mount points (Linux). Each entry: `path`, `label`, `kind`
(`fixed`\|`removable`\|`network`\|`ram`\|`unknown`), `available`, `totalBytes`, `freeBytes`
(the byte counts may be `null` for a device that will not report them). A root that is listed but
not `available` (an empty optical drive, a disconnected share) MUST still appear, with `available:
false`, rather than being silently dropped.

### 10.15 `GET /libraries/browse?path=…&counts=true`

Direct child folders of `path`, never recursive.

| Query | Type | Default | Meaning |
|---|---|---|---|
| `path` | absolute path | required | the folder to list |
| `counts` | boolean | `true` | count media in each child |

Response: `{ path, parent, entries: [BrowseEntry] }`. `parent` is `null` at a root.

`BrowseEntry`: `name`, `path`, `stillCount`, `videoCount`, `rankable`, `hasDatabase`, `accessible`.

- Counts are **top-level files of that child only**, applying `SPEC.md` § Media policy (same
  extension lists, same skipping of hidden and system files and of `rankmaster_db.json`).
- `rankable` is `true` when that child would open: at least 2 eligible files under the mixed-folder
  rule. It is the same predicate `POST /session` applies, so a client can grey out folders that
  would return `409 folder_not_rankable`.
- `hasDatabase` is `true` when `rankmaster_db.json` exists there. It says nothing about whether the
  file parses.
- A child that cannot be enumerated MUST be returned with `accessible: false` and **`null`** counts —
  not zero. `null` means unknown; `0` means counted and empty. The whole listing MUST NOT fail
  because one child is unreadable.
- With `counts=false` the server does not count, and **the count keys are absent** from each
  `BrowseEntry`: `stillCount`, `videoCount` and `rankable` are not emitted at all, rather than emitted as
  `null`. Clients MUST treat an absent count exactly as they treat `null` — unknown — which is what they
  already do for an inaccessible child. The shorter wire is the only reason; nothing else changes.
  Clients browsing a large or networked tree SHOULD pass `counts=false`: counting is one directory
  enumeration per child, and a root with hundreds of children on a slow share is a slow request
  (`SERVER_PLAN.md` § 6). A cap on children was considered and rejected: a truncated root listing would
  be a listing that lies.
- Browsing is unrestricted by decision (`SERVER_PLAN.md` § 7). Byte-serving is not: nothing here
  returns file contents, and `/media/*` serves only from inside the open session folder (§ 11).
- Symlinked directories are listed. The server MUST NOT follow them for counting beyond the single
  level it already reads, so a symlink loop cannot hang a request.
- Files are never listed. Hidden and system directories are listed with their real names; filtering
  them is the client's choice.

### 10.16 Rename by rank — `POST /session/rename`, `GET /session/rename`, `POST /session/rename/cancel`

The server side of `SPEC.md` § Rename by rank, **with no file backup** — safety is a journal, not a
copy. Reversed from an earlier prohibition (2026-09-16, owner decision); § 1.1 now points here.

**Scope.** These three routes take no `pairToken`; one sent in the body MUST be ignored — a rename
is not pair-scoped. Any *other* path containing `rename` still falls through to `404 not_found`
(§ 1.1).

**The request body.** `POST /session/rename` takes an **optional** JSON body, `{ "clientRequestId"?:
string }`. No body, an empty body, or `{}` are all valid and mean the same thing. A body that is present
but is **not** well-formed JSON is rejected `400 invalid_request` (§ 5.2's code for exactly that: "body
is not valid JSON"), and one that exceeds the body limit is
rejected `413`, exactly as for every other POST in this contract (§ 15) — the body is read, not skipped.
`clientRequestId`, when given, follows § 15's rules and is echoed in `lastAction` on success.
`POST /session/rename/cancel` takes no body.

**The three routes and `RenameOperation`:**

```
POST   /session/rename          start    -> 202 Accepted + RenameOperation (running)
GET    /session/rename          observe  -> 200 RenameOperation   (poll for the bar; no lock taken)
POST   /session/rename/cancel   cancel   -> 200 RenameOperation
```

```json
{
  "operationId": "T8l63K3ATihvFD9hSwOCtr",
  "state": "running",
  "phase": "renaming",
  "done": 4,
  "total": 12,
  "startedAt": "2026-09-16T18:04:11.400Z",
  "updatedAt": "2026-09-16T18:04:11.412Z",
  "error": null
}
```

| Field | Type | Meaning |
|---|---|---|
| `operationId` | string | Opaque, stable for the life of this run. |
| `state` | enum | `running` \| `cancelling` \| `succeeded` \| `cancelled` \| `failed`. |
| `phase` | enum | `preparing` \| `renaming` \| `saving` \| `reuniting` \| `done`. |
| `done`, `total` | integer | Moves completed / **`N`, the number of files in the plan**. There is one move per file (§ below), so `done` reaches `total` exactly once. |
| `startedAt`, `updatedAt` | string | RFC 3339 UTC. |
| `error` | object\|null | `null` unless `state` is `failed`: `{ "code": "rename_failed", "reunited": bool, "journal": string\|null }` (§ 5.7). |

**`202` is an acknowledgement, not a completion** — the first, named exception to "a 2xx means the
change is durable" (§ 13.1). By the time it is returned the journal is already fsynced, but the
moves and the database commit may not have run yet. Durability is asserted only when the operation
observed through `GET /session/rename` reaches `succeeded`.

**The names.** A run renames every file to `NNNNNN-ssss.ext` — six decimal digits of rank, a hyphen, the
run's four-hex-character suffix, the file's own extension — specified in full in `SPEC.md` § Rename by
rank. The suffix is drawn once per run and verified against the plan's input names **and** the folder's
top-level media listing, so **the old names and the new names of one run cannot overlap**. Sixteen
consecutive rejected draws fail the run with `500 rename_failed` (unreachable outside a test that forces
it). That disjointness is what makes everything below simple, and it is checked before the plan is used:
a plan whose `old` and `new` sets intersect is a bug, not a runtime condition.

**One move per file.** `old → new`, directly. There is **no temporary name and no second phase.** The move
never overwrites: a destination that already exists is an I/O error that fails the run safely, with the
journal still on disk, so the ratings are reunited from it.

**The journal.** Before the first file moves, the server writes `<folder>/.rankmaster-rename.json`
and fsyncs it (the same write-tmp / `Flush(true)` / atomic-replace discipline `JsonCatalog.Save`
uses). It carries the full old→new plan **and every rating row** (`mu, sigma, matches, impressions,
lastPlayed`), so recovery can rebuild a correct database from the journal alone, independent of
whatever `rankmaster_db.json` currently says. Its extension is not a media extension, so
`JsonCatalog.Scan` never lists it, exactly like `.rankmaster.lock`.

The journal is **format 2**:

```json
{
  "format": 2,
  "state": "renaming",
  "createdAt": "2026-09-17T18:04:11.400Z",
  "suffix": "7f3a",
  "plan": [
    { "old": "DSC_0123.jpg", "new": "000001-7f3a.jpg",
      "mu": 31.4, "sigma": 4.02, "matches": 9, "impressions": 11, "lastPlayed": 1757000000 }
  ]
}
```

The reader accepts **format 1** (the same shape without `suffix`; such journals exist only in `rm2ctl`
scratch folders, never on the owner's disk) and **format 2**. Anything else — another `format` value, a
null or absent `plan`, malformed JSON, a truncated file, or a plan whose `old` and `new` sets intersect —
is **unreadable**. An unreadable journal is never guessed at: see "Recovery" below.

**Ordering.** Under the session lock: compute the plan (`μ − 3σ` descending, then filename for ties, to
`NNNNNN-ssss.ext` — `SPEC.md` § Rename by rank), draw and verify the run suffix, then **write and fsync
the journal, and only if that write succeeded** mark the session rename-in-progress and publish the
operation. The lock is then released and the rest runs on a server task, observable and cancellable:
**one move phase** (`old → new` for each of the N entries, `done` counting up to `total` = N), then the
database is rewritten with the new keys (atomic), then the journal is deleted — **the commit point** —
then, under the lock again, the session is updated: `ReplaceAll` with the remapped records, undo cleared,
`pairSeq` +1, `lastAction.type = "rename"`.

**The flag is set after the journal write, never before.** If writing the journal throws — a read-only
folder, a full disk — the server answers `500 rename_failed { "reunited": true, "journal": null }` and
**the session is left exactly as it was**: not marked rename-in-progress, no operation published, the pair
on screen still valid, the next call served normally. `reunited` is `true` because nothing was ever
disturbed and no rating is in doubt; `journal` is `null` because none was written. Setting the flag first
would leave a session that answers `409 rename_in_progress` to every action, with no run to finish it and
no cancel to clear it — a wedge only a server restart cures. It MUST NOT happen.

**Recovery is forward and total, and never needs the owner.** It runs on the next `POST /session`,
under the folder lock, **before** `RankingSession.Start()` scans anything: if the journal is present,
a rename was interrupted. Recovery finalizes the moves toward `new` where it can (best-effort — an
entry it cannot finish is not a failure: if `new` already exists it skips, else if `old` still exists it
moves `old → new`) and then **reunites every rating with its file at that file's current name**, writing a
fresh database from the journal's ratings; a file with no journal entry gets a default rating, as `Scan`
already gives one; a journal entry whose file is gone from disk entirely is dropped (its image no longer
exists, the only unavoidable loss). The journal is then deleted.

**Nothing has to be guessed, because the name says which run it belongs to.** For each top-level media
file found on disk, its name is looked up as a `new` name first and as an `old` name second. The two sets
are disjoint by construction (the run suffix was verified against the folder before the plan was used), so
the first hit is the only possible hit — there is no name that could be either, and therefore no file whose
state is ambiguous. Recovery re-checks that disjointness before trusting a journal; a hand-edited journal
that violates it is unreadable (below). **Every interruption point resolves to "every rating finds its
file":**

| Killed at | On disk when reopened | Recovery reunites because |
|---|---|---|
| after the journal fsync, before any move | every file still `old`, DB original, journal present | every name on disk is an `old` name and only an `old` name; reunite writes back the same DB; harmless |
| after k of N moves | k files `new`, N−k files `old`, DB still original (old keys) | each name is in exactly one of the two disjoint sets, so the journal maps it to its rating with no guess; recovery finishes the remaining moves and keys the DB to the resulting names |
| after all N moves, before the DB save | every file `new`, DB still old keys | **the dangerous case** — the ordinary `Scan`/`Save` merge would drop every rating; every name is a `new` name, so the journal reunites them and the ratings follow the files |
| after the DB save, before the journal delete | files `new`, DB new keys, journal present | every name is a `new` name and the DB already agrees; reunite is idempotent and just deletes the journal |
| after the journal delete | files `new`, DB new keys, no journal | nothing to recover; consistent |

**An unreadable journal is refused, not guessed at.** If the journal is present but cannot be read — a
format the reader does not accept, a null or missing `plan`, malformed or truncated JSON, or `old` and
`new` sets that intersect — recovery makes no move and no database write. The `POST /session` **does not
open the session**, answers `500 rename_failed { "reunited": false, "journal": "<path>" }` (§ 5.7), and
**releases the folder lock** before answering. `reunited: false` is the truth: the ratings are still on
disk in the old database and nothing was touched, but the server could not reunite them. The path is in
the answer so the owner can look at the file or move it aside. The lock MUST be released: a failed open
that keeps the lock leaves the folder answering `423 folder_locked` ("in use by another process") to every
later attempt, including the owner's, until the server is restarted.

The one thing that does **not** heal a mid-rename crash is the ordinary `Scan` merge — it keys by
filename and would assign the renamed files fresh ratings and drop the real ones (`JsonCatalog.Save`
merges against what is on disk; `SPEC.md` § Persistence). That is why recovery via the journal MUST
run before anything scans. A half-renamed folder is an **acceptable
resting state**: recovery reunites ratings even when it cannot finish every move, and the owner may
simply re-run rename.

**Cancel — stop and reunite in place, never a database-risking rollback.**

- In `preparing`, before the journal is written and before any move: abort cleanly, delete any
  half-written journal, session unchanged.
- In `renaming`: stop issuing moves, run **reunite in place** — write the database keyed by the
  files' current (mixed `old`/`new`) names from the journal's ratings, delete the journal, then
  **resync the session** ("Cancel or failure — the session effect", below). Terminus `cancelled`.
  No database risk: the reunite `Save` is atomic.
- In `saving` or later: too late; the operation completes and cancel just reports the current state.

Cancel is idempotent: a second press returns the operation, no second action.

**On success** the session effect is: same `sessionId`, `pairSeq` +1, new numeric pair ids, `cues`
cleared, `undoAvailable` false, `sessionVotes`/`counts`/`progress` unchanged in value,
`lastAction.type == "rename"`. An in-flight action that arrives after success gets `409
stale_pair_token` with the new snapshot and MUST NOT be replayed. Every cached media URL now 404s
(§ 11.3), because every filename changed.

**Cancel or failure — the session effect.** A rename that is cancelled in `renaming` and a rename that
fails after the journal was written do **the same thing** to the session, because the folder is in the same
condition either way: some files moved, some did not, the ratings have been reunited in place. The session
is **resynced**:

| | After a cancel in `renaming`, or a failure |
|---|---|
| `sessionId` | **unchanged** — it is the same session, not a new one |
| records | **re-read from disk**, so the names the server holds are the names on disk |
| `Current` | **re-picked** from the re-read records |
| `pairSeq` | **+1** — the generation genuinely changed, and saying so is honest (§ 8.3) |
| `sessionVotes` | **kept.** The owner's count of this sitting does not restart because a rename did not finish |
| `cues` | **kept** — the match strip is a record of what he did, and he still did it |
| `undoAvailable` | **false** — undo is cleared; the snapshot it held refers to files that may have moved |
| `lastAction` | **`null`** |
| old `pairToken`s | **stale.** An action sent with one gets `409 stale_pair_token` with the new snapshot |

A cancel in `preparing` — before the journal was written and before any file moved — **leaves the session
completely untouched**: same pair, same token, same everything. Nothing happened, so nothing changes.

If the resync itself cannot read the folder (it was unmounted, deleted, or made unreadable during the
run), the session is **closed** and the folder lock released. The operation still finishes and reports
`failed` with `reunited` stating the truth about the ratings; the client's next call gets
`404 no_session` and reopens. A session whose folder cannot be read is not a session.

**Concurrency.** The run holds the session semaphore only at the start (create + journal
+ flag) and at the final apply (`ReplaceAll`). While the moves run off the lock, the rename-in-progress
flag makes every mutating `/session*` call that takes the lock answer `409 rename_in_progress`
(`details.operationId`); a `GET /session` read still works and shows the pre-rename session; the
status poll (`GET /session/rename`) is lock-free. **`DELETE /session` is refused the same way** while a
rename is running — `409 rename_in_progress` (§ 10.4) — even though it is the one call that would
otherwise seem harmless. It is not: releasing the folder lock under a running rename lets a second
`POST /session` start journal recovery on a folder the first session is still moving files in, with two
programs finalizing the same plan at once. The refusal makes that race unbuildable. The client closes the
session by cancelling the rename first and then closing. Media `GET`s (lock-free) see files mid-move and
answer `404 media_file_missing`, and afterward `404 unknown_media_id` for the old ids (§ 11.3) —
never corruption, because a `GET` never mutates.

**Why there is a bar and a cancel.** Renaming within one directory is directory-entry moves, not byte
copies, and on a local disk it is near-instant even for thousands of files — a folder on an SSD is over
before a human can react, and a client SHOULD say so rather than imply a copy is happening. The bar and
the Cancel button are **not** there for large folders. They are there because the owner does not know how
a rename behaves on a **slow USB drive**, and he wants to be able to stop it the moment it looks wrong
(his decision, 2026-09-17). That sets a requirement on cancel, not on the bar:

- **Cancel is checked before every move**, so a run that has not reached a file will not touch it.
- **Cancel is also checked between the retries of a move that is waiting on a locked file.** A move whose
  destination or source is briefly held by another program is retried with a delay; a cancel that arrives
  during that wait is honoured within **one retry interval**, not after the whole retry budget. On a slow
  or stubborn drive this is the difference between a button that works and a button that appears frozen.
- Cancel **stops where it is.** It undoes nothing already done and rolls nothing back; it reunites the
  ratings with the files at their current names and resyncs the session (above). **No rating is lost by
  cancelling**, at any point. The folder is left with mixed names, which is untidy and not lossy, and
  re-running rename tidies it in one pass.
- Cancel is safe to press at any time, including in `preparing`, where nothing has happened yet.

A client MUST offer Cancel from the first instant of the operation and MUST show `done / total` rather
than a spinner, so that "nothing is happening" and "it is working slowly" look different.

**Who offers it.** `features.rename` is `true` (§ 14) whenever the server exposes this operation; the
endpoint itself is open to any paired token. The *recommendation* — PC client yes, phone no — is a
client concern (`PC_CLIENT_PARTS.md`), not an availability restriction.

---

## 11. Media identity

### 11.1 An id is a filename

`MediaId` is the filename and nothing else — no GUID, no hash, no database key (`SPEC.md` § Shared
types). The JSON object key, the `filename` field and the id are the same string. This is a
portability feature of the file format and a source of sharp edges over HTTP; the rules below exist
to blunt them.

The id appears as **one path segment**:

```
/api/v1/media/{id}/still
```

**Encoding, normatively:**

1. The client MUST percent-encode the UTF-8 bytes of the filename using RFC 3986 path-segment rules.
   Everything outside `A–Z a–z 0–9 - . _ ~` MAY be encoded; the following MUST be encoded:
   `/ \ ? # % & + ; = : @ [ ] " < > ^ { } |`, space, and every byte ≥ 0x80.
   A space MUST be `%20`. `+` MUST NOT be used for a space — this is a path segment, not a query.
2. The server MUST percent-decode exactly once, as UTF-8. A byte sequence that is not valid UTF-8 →
   `400 invalid_media_id`, `details.reason: "not-utf8"`.
3. The server MUST NOT apply Unicode normalisation, case folding, or any other transformation to the
   decoded string. `Ä` composed and `Ä` decomposed are different ids, because they are different
   filenames on Linux. Comparison against the session's records is ordinal — case-insensitive on
   Windows, case-sensitive elsewhere, matching `JsonCatalog`'s `OrdinalIgnoreCase` dictionary and the
   platform's filesystem.
4. The decoded id MUST satisfy all of: non-empty; ≤ 255 UTF-16 code units; contains no `/`, `\`,
   NUL, or any control character below 0x20; is not `.` or `..`; is not rooted
   (`Path.IsPathRooted` is false); and equals its own `Path.GetFileName`. Any violation →
   `400 invalid_media_id` for the syntactic cases, `403 media_outside_session` for anything that
   would escape the folder.
5. `%2F` and `%5C` decode to separators and MUST therefore be rejected. Servers hosted where the
   framework rejects encoded separators before routing MAY return the framework's `400`, provided it
   still carries the standard error envelope.
6. The canonical spelling of an id is the one the server put in a snapshot. Clients MUST echo that
   spelling and MUST NOT re-case it. The server MUST return the on-disk spelling in `pair`,
   `lastAction` and `meta`, not the spelling the client sent.

Because building these URLs correctly four times is four chances to get it wrong, every snapshot
ships ready-made `links` (§ 9.3) and clients SHOULD use them verbatim.

### 11.2 Where an id may point

`/media/*` is the one place the token does not buy access to the whole filesystem
(`SERVER_PLAN.md` § 3.6). Resolution MUST be, in order:

1. No session open → `404 no_session`. There is no way to fetch bytes without a session.
2. Id fails § 11.1 → `400 invalid_media_id` or `403 media_outside_session`.
3. Extension in neither list of `SPEC.md` § Media policy → `403 media_extension_not_allowed`.
   (Redundant with step 4 in practice; it MUST be checked anyway, so the rule holds even if the
   record set is ever populated from somewhere else.)
4. The id is not a record in the open session → `404 unknown_media_id`. Membership is against
   `RankingSession.Records`, which includes videos in a mixed folder even though they are not
   rankable. It excludes anything already discarded.
5. Combine as `Path.Combine(session.folder, id)`, resolve with `Path.GetFullPath`, and verify the
   result's directory is exactly the session folder. Any mismatch → `403 media_outside_session`.
   This check MUST be performed even though step 4 already passed; it is the backstop against a
   record id that somehow carries a separator.
5b. **Follow the link.** `Path.GetFullPath` is string canonicalisation: it collapses separators and
   `..` and does not resolve symlinks, so a link inside the session folder named `holiday.mp4`
   passes step 5 while pointing anywhere on the disk. The server MUST resolve the final link target
   and verify *that* path's directory is the session folder — comparing against the folder's own
   final target too, since the folder may itself legitimately be reached through a link. A link to a
   sibling inside the folder is allowed; it reaches nothing the caller could not ask for by name.
   Anything else → `403 media_outside_session`. A link that dangles or loops is not a containment
   failure: it falls through to step 6.
6. The file does not exist → `404 media_file_missing`.

Subfolders are never reachable: `discarded/`, `special 1/` and `rankmaster_backup_*` hold files that
are, by definition, no longer records.

### 11.3 When a file moves under a session

The session does not rescan (§ 7.4.8). Filenames are the identity, so anything that changes a
filename breaks the link between the session and the bytes.

| Event | Snapshot | `/media/{id}/*` | Recovery |
|---|---|---|---|
| File deleted externally | id still in `pair`/`records`; `sizeBytes` and `mediaVersion` `null` | `404 media_file_missing` | `POST /session/discard` on that side → `drop_missing` (§ 10.8) |
| File renamed externally | old id still in records; the new name is invisible | `404 media_file_missing` for the old id, `404 unknown_media_id` for the new | same; the new name appears only after close + reopen |
| File replaced with different bytes, same name | unchanged ids; `mediaVersion` changes at the next snapshot | new bytes, new ETag | none needed, but see § 12.5 |
| File discarded through the API | id removed from records | `404 unknown_media_id` | `POST /session/undo` |
| File restored by undo under a new name | the **new** id appears | the new id serves; the old one is `404 unknown_media_id` | use `lastAction.restoredId` |
| File added externally | invisible | `404 unknown_media_id` | close and reopen the session |

A client MUST therefore treat a `404` on a media URL as "this id is gone", not as a transport error,
and MUST refresh from the snapshot rather than retrying the URL. A `404 media_file_missing` on an id
that is still in the current pair is the client's cue to discard that side.

The server MUST NOT mutate session state from a `GET`. A failed media read never drops a record on
its own; only `POST /session/discard` does (§ 10.8). This differs from the desktop app, where a
failed decode calls `Drop` from the UI thread — on the server the client is the one that decides,
because a lost image request is far more likely to be a flaky Wi-Fi link than a bad file.

---

## 12. Media endpoints and caching

### 12.1 The four endpoints

| Endpoint | For | Body |
|---|---|---|
| `GET /media/{id}/meta` | both kinds | JSON `MediaMeta` |
| `GET /media/{id}/still?w=&format=&v=` | stills only | re-encoded JPEG or WebP |
| `GET /media/{id}/thumb?format=&v=` | stills only | 320 px JPEG or WebP |
| `GET /media/{id}/video?v=` | videos only | the original bytes, Range-capable |

Wrong kind → `409 wrong_media_kind`. There are no poster frames and no still for a video: the client
plays the stream directly (`SERVER_PLAN.md` § 3.4).

`MediaMeta`:

| Field | Type | Null? | Meaning |
|---|---|---|---|
| `id` | string | no | on-disk spelling |
| `kind` | enum | no | `still` \| `video` |
| `sizeBytes` | integer | no | file length |
| `modifiedAt` | string | no | RFC 3339 UTC, file mtime |
| `mediaVersion` | string | no | § 12.2 |
| `width`, `height` | integer | **yes** | stills only, **after** EXIF orientation is applied (`SPEC.md` § Media policy). `null` for a video, always. |
| `rating` | `{mu,sigma,conservative}` | no | the record's rating; `conservative` is `μ − 3σ`, computed, never stored (`SPEC.md` § Persistence) |
| `matches`, `impressions` | integer | no | as stored |
| `lastPlayed` | integer | no | unix ms, as stored |
| `rankable` | boolean | no | whether this record matches the session `policy` |

`MediaMeta` MUST NOT contain `durationMs`, `codec`, `frameRate`, `bitrate` or any other field that
would require decoding a video (`SERVER_PLAN.md` § 7). Reading `width`/`height` for a still is a
header read, not a full decode; if it fails → `422 media_decode_failed`.

**`details.reason` on `media_decode_failed`.** `unreadable` means the bytes on disk are not an image
this server can decode — the file really is the problem. `no_room` means the file is fine and the
server had no free decode memory for it at that moment (§ 12.3's budget); it will very likely succeed
on the next attempt. **Clients MUST branch on `reason`, not on `error.message`**, which § 4 fixes as
unstable and never parsed — and MUST NOT tell the owner a `no_room` file is damaged or that his own
device could not open it. A client that cannot distinguish the two will blame the wrong thing, which
is exactly what happened before this field existed.

### 12.2 `mediaVersion` and `ETag`

Both derive from one fingerprint. All inputs are of the file **as it is at request time**.

```
fingerprint  = SHA-256( utf8(folder) || 0x00 || utf8(id) || 0x00 || decimal(sizeBytes) || 0x00 || decimal(mtimeUtcTicks) )
mediaVersion = lowercase hex of fingerprint[0..8)          // 16 chars
ETag         = "\"" + variant + "-" + lowercase hex of fingerprint[0..16) + "\""
```

- `folder` is the **open session's own absolute, canonicalized folder** (§ 11.2 step 5), never a
  path the client supplied. Without it the fingerprint identifies a file by name alone, so two files
  in two folders agreeing on id, size and mtime are one object to the server — the same ETag and the
  same disk-cache entry. That is not theoretical: it was reproduced serving one photograph's pixels
  under another photograph's name, byte-identical, while `meta` still reported the second file's real
  size, and the client would then hold the wrong picture for a year under § 12.5.3's caching licence.
  Including the folder is what makes "the same ETag means the same bytes" true across folders.
- **Consequence, worth knowing:** moving or renaming a session folder on disk changes every ETag
  under it at once. Nothing is wrong — each client revalidates once and receives a fresh ETag for the
  same, correct bytes — but it is a real one-time cost rather than a silent one.
- `mtimeUtcTicks` is `File.GetLastWriteTimeUtc(path).Ticks` — 100 ns units since 0001-01-01, decimal,
  no separators.
- `variant` identifies the exact representation:

| Endpoint | `variant` |
|---|---|
| `still`, JPEG | `s{w}j`, e.g. `s1080j` |
| `still`, WebP | `s{w}w`, e.g. `s1080w` |
| `thumb`, JPEG | `t320j` |
| `thumb`, WebP | `t320w` |
| `video` | `orig` |
| `meta` | not applicable — `meta` is a JSON endpoint and is `no-store` |

- `{w}` in the variant is the **requested** width, not the delivered one (§ 12.3), so two clients
  asking for different widths of a small image get different ETags even when the bytes match.
- The ETag is **strong**. No `W/` prefix. Two responses with the same ETag MUST be byte-identical,
  forever, on every server instance.
- `If-None-Match` MUST be honoured on all three byte endpoints: an exact match → `304` with
  `ETag` and `Cache-Control` and no body. `*` matches any existing representation.

### 12.3 Widths and formats

Allowed `w`, and nothing else:

```
360, 540, 720, 1080, 1440, 2160
```

- `w` omitted → **1080**.
- Any other value, including a smaller one that "would be fine" → `400 unsupported_width` with
  `details.allowed`. Capping the set is what keeps the on-disk cache bounded
  (`SERVER_PLAN.md` § 3.3).
- `thumb` ignores `w` entirely; its width is fixed at **320**. A `w` on `thumb` MUST be ignored, not
  rejected.
- **Never upscale.** If the source's long edge after orientation is below the requested width, the
  server serves it at source size (`SPEC.md` § Pipeline). The response is still keyed by the
  requested `w`. `Content-Length` and the actual pixel dimensions therefore do not follow from `w`,
  and a client MUST NOT lay out from `w` alone — use `meta`, or the decoded image.
- Aspect ratio is preserved; the server never crops. EXIF orientation and the ICC profile are applied
  during decode, and the output is sRGB (`SPEC.md` § Pipeline, § Media policy).

Format selection, in strict precedence order:

1. `format=jpeg` or `format=webp` in the query wins. Any other value → `400 unsupported_format`.
2. Otherwise, if the request's `Accept` header includes `image/webp` (with a non-zero `q`), WebP.
3. Otherwise JPEG.

A response whose format was chosen by `Accept` MUST carry `Vary: Accept`. One chosen by `format=`
MUST NOT. Quality is a server setting; it MUST be identical across requests, because the ETag does
not encode it.

`v` is a cache-busting parameter. The server MUST accept any value, MUST ignore it when selecting
content, and MUST NOT fail on a mismatch with the current `mediaVersion`. It exists so the `links`
in a snapshot are stable, distinct URLs whenever the bytes change.

### 12.4 Range requests

`GET /media/{id}/video` only; `still` and `thumb` are generated and are served whole.

| Request | Response |
|---|---|
| no `Range` | `200`, full body, `Accept-Ranges: bytes`, `Content-Length` |
| one satisfiable `bytes=` range | `206`, `Content-Range: bytes a-b/size`, `Content-Length: b-a+1` |
| several ranges | the server MUST serve **only the first** as a single `206`. Multipart byte ranges MUST NOT be produced |
| unsatisfiable | `416 range_not_satisfiable` with `Content-Range: bytes */size` and the error envelope |
| syntactically invalid `Range` | ignored; serve `200` full, per RFC 9110 |
| `If-Range` matching the ETag | the range is served |
| `If-Range` not matching | `200` full body |
| `If-Range` as an HTTP-date, at or after the file's last-modified time | the range is served (RFC 9110 § 13.1.5) |
| `If-Range` as an HTTP-date older than the file's last-modified time | `200` full body — the file changed under the client, so its partial copy is stale |

Open-ended (`bytes=500-`) and suffix (`bytes=-500`) forms MUST both be supported. `Content-Type` is
by extension: `video/mp4`, `video/webm`, `video/x-matroska`, `video/x-msvideo`, `video/quicktime`.
The bytes are the original file, unmodified (`SERVER_PLAN.md` § 3.4).

### 12.5 What the client may cache

Byte responses carry:

```
ETag: "s1080j-9f2a1c77b0e4d3105ab8…"
Cache-Control: private, max-age=31536000, immutable
Accept-Ranges: bytes          (video only)
Vary: Accept                  (only when Accept chose the format)
```

Normative guarantees, and their exact limits:

1. **Bytes are immutable per ETag.** The same ETag always means the same bytes. A client MAY cache
   indefinitely, keyed by ETag.
2. **URLs are immutable only with `v`.** A URL without `v` can change content when the user replaces
   a file with the same name, which makes `immutable` a lie for that URL. The `links` the server
   hands out always include `v=<mediaVersion>`; a client that uses `links` verbatim has genuinely
   immutable URLs. A client that builds its own URLs without `v` MUST revalidate with
   `If-None-Match` whenever it reopens a session.
3. **Cache scope is the session, not the device.** `sessionId` changing, or a media `404`, voids the
   client's assumptions about which ids exist. The bytes already downloaded stay valid.
4. **`private` is mandatory.** These bytes are the user's photos; no shared cache may hold them.
5. `meta` and every other JSON endpoint are `no-store` and MUST NOT be cached.
6. The server's own disk cache for re-encoded stills lives under the server's data directory and
   **never** inside the user's media folder (`SERVER_PLAN.md` § 3.3). It is bounded by size with LRU
   eviction and is invisible to the API: eviction never changes a response body, only its latency.

**Known limit:** the fingerprint is `(folder, name, size, mtime)`. Two writes to the **same** file
that leave it with an identical name, byte length and modification time but different content still
collide — a same-folder restore, or a sync tool that preserves all three while changing the bytes —
and a client will serve stale bytes from its cache. This is the residual cost of identifying a file
by what `stat` says rather than by its content. It no longer extends **across** folders: that was a
real fault, and § 12.2 now hashes the folder. Hashing content would cost a full read of every file on every
request; the trade is deliberate. Clients that must be certain MAY bypass with a distinct `v`. A
rename (§ 10.16) reassigns every name to `NNNNNN-ssss.ext`, where `ssss` is that run's suffix. Two
consecutive runs therefore cannot produce the same name for anything, because the second run refuses a
suffix that is already on disk. A suffix can only recur across **non-adjacent** runs, and a name can only
recur if the same rank falls to the same file at the same extension in such a run — at which point the
ordinary `(name, size, mtime)` limit above applies, exactly as it does for any name. It is theoretical.

---

## 13. Ordering, durability and retries

### 13.1 The invariant

> When a 2xx response leaves the server, the change that request made is **applied**, and it is on disk
> **within a bounded delay** — at most `SaveDelaySeconds` (default **2**) or `MaxUnsavedChoices`
> (default **5**) further choices, whichever comes first.
>
> For everything that is not a plain choice, the old, stronger promise still holds exactly: **the 2xx of
> `POST /session/save`, and the 2xx of every endpoint that moves a file, mean the change is on disk
> now.**

This is a **bounded write-behind**, and it is a deliberate relaxation of what this section used to
promise (the owner, 2026-09-17: *"I'm not afraid of losing a couple of votes, it's non-consequential."*).
A vote, a skip, or the undo of a vote or skip is applied in memory, `pairSeq` advances, the snapshot is
materialised and the response leaves — and the server writes the database off the request path, no later
than the bound. The most that a crash, a kill or a power cut can cost is **five choices or two seconds of
voting**, and nothing else.

**It is not a cache.** Nothing is held in memory that is not also going to disk on a clock the server
owns; there is no eviction, no queue that can grow, and no request that can be told "later" indefinitely.

**What is never deferred.** A save is forced, synchronously, inside the request, **before** the response:

- before a file move — `POST /session/discard`, `POST /session/special`, and the undo of a move. § 13.2's
  "file moved, then JSON written" ordering is unchanged, so the self-healing argument there stands;
- before a rename starts (§ 10.16);
- on `POST /session/save` — this is now *the* way a client makes a point durable (§ 10.5);
- on `DELETE /session` — the close flushes what is unsaved (§ 10.4);
- on server shutdown.

**A write that is made is exactly as safe as it ever was.** `JsonCatalog.Save` is untouched: temp file,
`Flush(true)` (an fsync), `File.Replace`; it never creates the folder, never writes an empty database
over records, and merges against what is on disk. What changed is *when* it is called, not what it does.
Batching **coalesces**: five choices become one write of the whole file, which is why this is faster
rather than merely later.

**The switch.** `RankMaster2:SaveDelaySeconds` and `RankMaster2:MaxUnsavedChoices` in `appsettings.json`
(§ 2.4). **`SaveDelaySeconds = 0` restores the old behaviour exactly** — every choice saved before its
response, the invariant as it was written above the line. Neither key appears on `/ping` and neither is a
feature flag: this is not something a client negotiates or needs to know about.

**When a deferred write fails.** The failure is **latched** on the session. While it is latched the flush
keeps retrying every `SaveDelaySeconds`, and every mutating call first attempts a synchronous save; if
that fails too, the call answers `500 save_failed` with the snapshot attached and **nothing applied** —
the `pairToken` stays valid and the client retries it, which is exactly the existing "vote/skip throws in
`Save`" row of § 8.3. So unsaved choices cannot pile up on a dead disk: the second choice after the drive
goes away tells the owner. A successful `POST /session/save` clears the latch.

Response order for a plain choice is therefore:

```
acquire session lock
  → mutate in memory
  → advance the pair, recompute pairSeq and pairToken
  → mark the session dirty; arm the flush if this is the first unsaved choice,
    or flush now if this is the MaxUnsavedChoices-th
  → materialise the snapshot
release session lock
  → send the response
```

and for a move, a rename start, an explicit save, or a close, the forced `JsonCatalog.Save` sits where it
always did — before the response.

The corollary is now narrower, and worth stating in the narrow form: **a 200 from `POST /session/save`,
or from anything that moved a file, is proof of durability; the absence of a response still proves
nothing.**

**The retry and stale-token rules do not change, and MUST NOT be read as depending on the disk.** A
`pairToken` is derived from `(sessionId, pairSeq, left, right)` — all in memory — and `pairSeq` advances
when the engine *enters* an action, not when the write lands. § 13.3 is unaffected in every row: a client
that lost a response resends the same token once and gets either the vote or `409 stale_pair_token`, never
two votes, whether or not the first one has reached the disk yet.

**The second exception remains `POST /session/rename` (§ 10.16),** which returns `202` before the change
is durable at all, because the owner requires a progress bar and a cancel button. A rename's durability is
asserted only when its operation reaches `succeeded`; until then the folder is explicitly mid-flight, and
its safety rests on the journal (§ 10.16), not on the response.

### 13.2 Per-endpoint ordering

| Endpoint | Disk effects, in order | Committed before the response? |
|---|---|---|
| `POST /session` | lock file created/opened; JSON **read** only | yes; nothing is written |
| `GET /session`, `/session/pair` | none | — |
| `POST /session/save` | JSON written | **yes** — forced and synchronous; this is the endpoint that makes a point durable |
| `POST /session/vote`, `/skip` | the pair advances; JSON written **within the bound** (§ 13.1) | **no, and by design** — the response does not wait for the write. Applied at once, on disk within 2 s or 5 choices |
| `POST /session/discard`, `/special` | anything unsaved is flushed, then the file is moved, then JSON written | **yes**, and in that order — a move is never deferred |
| `POST /session/undo` | of a move: flush, file moved back, then JSON written — **yes**. Of a vote/skip: no file touched, JSON written within the bound — **no**, like the vote it reverses | as stated per case |
| `DELETE /session` | unsaved choices flushed, then the lock file closed and removed; **no other JSON write** | yes |
| `GET /media/*` | none | — |
| `POST /session/rename` | journal fsynced (and only then is the session marked rename-in-progress), then **one move per file**, then the database rewritten to the new names, then the journal deleted | **no** — `202` is issued once the journal is fsynced; the moves and the commit follow. On interruption, recovery on the next open reunites every rating with its file from the journal, before any scan (§ 10.16) |

Between the move and the save of a discard there is a window in which the file is in `discarded/`
and the JSON still lists it. If the process dies there, the next session's `Scan` simply does not see
the file and the next `Save` drops the row — `JsonCatalog.Save` merges against what is on disk, so
the inconsistency self-heals. Ratings are never lost this way.

### 13.3 If a request times out

The client does not know the outcome. It MUST resolve, never guess:

| Endpoint | Safe to retry blindly? | How to resolve |
|---|---|---|
| any `GET` | yes | — |
| `POST /session` | yes | same folder resumes, `200` |
| `POST /session/save` | yes | idempotent |
| `DELETE /session` | yes | a second call is `404 no_session` |
| `POST /session/vote`, `/skip` | **yes, with the same `pairToken`** | if it landed, the retry gets `409 stale_pair_token`; if it did not, the retry votes. Either way one vote, never two. A retry with a *new* token after resyncing is a **double vote** and MUST NOT be done. |
| `POST /session/discard`, `/special` | **yes, with the same `pairToken`** | same reasoning |
| `POST /session/undo` | yes | a second undo is `409 nothing_to_undo`; it cannot reach back two actions, because only one is ever recorded |
| `POST /pair` | **no** | the code is single-use; a retry of a landed pairing gets `401 invalid_pairing_code` and the token is lost. Restart pairing. |

**The rule that makes this work:** replaying the *same* `pairToken` is always safe, because a token
is consumed exactly once. Replaying an action after obtaining a *fresh* token is never safe. Clients
MUST hold the token of an in-flight action until they have a definite answer.

### 13.4 What survives a restart, and what does not

| State | Survives a server restart? |
|---|---|
| ratings, `matches`, `impressions`, `lastPlayed` | yes, **except the last ≤ 5 choices or ≤ 2 seconds of voting**, which the bounded write-behind may not have written yet (§ 13.1). The owner accepted that cost explicitly. Everything that moved a file, every explicit save, and a clean shutdown are on disk |
| files already moved to `discarded/` / `special 1/` | yes |
| the session itself, `sessionId`, `sessionSecret`, all `pairToken`s | **no** |
| `SessionVotes`, `cues`, the recent-shown set, warm pairs | **no** — session-only by design (`SPEC.md`) |
| the recorded last action, so `undoAvailable` | **no** — nothing done before the restart can be cancelled |
| `lastAction` | **no** |
| a rename interrupted by a restart | **the ratings survive**: the journal in the folder is found on the next open and reunites every rating with its file before anything scans (§ 10.16). The filenames may be left mixed; the ratings are not lost. This is a strict improvement over the general in-flight gap below. |

After a restart the client re-opens the folder and gets a new `sessionId`. Every old token is stale.

**The honest gap:** because `lastAction` is in-memory, a client whose request was in flight when the
server died cannot learn whether that vote landed — and with the write-behind of § 13.1, a vote that was
*answered* shortly before the server died may also be gone. Both are bounded by the same two seconds. The data is safe — the atomic save means the vote
is either fully applied or not at all, never half — but the *client's* knowledge is not. In that
single case the client MUST NOT retry the action; it MUST continue from the new pair and accept that
one vote is of unknown status. Recording an action journal in the server's own data directory would
close this; it is out of scope here and is listed in § 16.

---

## 14. `GET /ping`

Unauthenticated (public subset) or authenticated (full). Never requires a session.

```json
{
  "product": "Rank Master 3 server",
  "apiVersion": "v1",
  "version": "3.0.0",
  "ready": true,
  "authenticated": false,
  "certificateFingerprint": "sha256:3b1f…64 lowercase hex…",
  "serverTime": "2026-09-12T18:04:11.412Z",
  "features": {
    "rename": true,
    "videoTranscoding": false,
    "posterFrames": false,
    "videoProbe": false,
    "browse": "full-filesystem",
    "maxConcurrentSessions": 1
  },
  "limits": {
    "stillWidths": [360, 540, 720, 1080, 1440, 2160],
    "thumbWidth": 320,
    "maxJsonBodyBytes": 65536,
    "sessionLockTimeoutSeconds": 5
  },
  "session": null
}
```

- `certificateFingerprint` is `"sha256:"` followed by 64 lowercase hex characters: the SHA-256 of the
  certificate's DER encoding. It is what the client pins (`SERVER_PLAN.md` § 3.5) and it is in the
  public subset, because a client that cannot yet authenticate is exactly the client that needs it.
- `session` is `null` in the public subset. Authenticated, it is
  `{ "open": bool, "sessionId": string|null, "folder": string|null, "state": "ranking"|"exhausted"|null }`
  — a cheap way to ask "is a session open" without a `404`.
- `ready` is `false` only while the server is starting or shutting down; every other endpoint returns
  `503 server_shutting_down` in that window.
- `features` values are fixed by `SERVER_PLAN.md` § 7 and MUST NOT be configurable. `rename` is
  `true` when the server exposes the `/session/rename` operation (§ 10.16); a server predating this
  part answers `false`.

---

## 15. Limits

| Limit | Value | Breach |
|---|---|---|
| JSON request body | 65536 bytes | `413 payload_too_large` |
| `clientRequestId` | 64 characters | `400 invalid_request` |
| `folder` / `path` | 4096 characters | `400 invalid_path` |
| media id | 255 UTF-16 code units | `400 invalid_media_id` |
| session semaphore wait | 5 s | `503 session_busy`, `Retry-After: 1` |
| pairing rate | 5 per minute per source address | `429`, `Retry-After` |
| pairing guesses | 5 per source address **per window**; an address that spends its five is locked out of that window, which stays open for everyone else (§ 10.11) | `401 invalid_pairing_code` with `details.attemptsRemaining` |
| pairing window | 5 minutes, single use | `401 invalid_pairing_code` |
| concurrent sessions | 1 | `409 session_already_open` |

Rate limiting is REQUIRED on `POST /pair` and OPTIONAL elsewhere; if applied elsewhere it MUST use
`429 too_many_requests` with `Retry-After`.

---

## 16. Known gaps

Recorded rather than smoothed over. None of these is a licence to change `RankMaster2.Ranking`
without changing `SPEC.md` first.

1. **In-flight actions across a restart are unresolvable** (§ 13.4). A journal of the last action in
   the server's data directory would fix it. Not in scope.
2. **`SERVER_PLAN.md` § 4 says "every action takes `pairToken`" but lists `undo` with no body.**
   Resolved here as: undo takes no token (§ 10.10). If that is wrong, the plan must change, not the
   implementations.
3. **`SERVER_PLAN.md` § 4 lists no way to drop an unreadable or vanished file**, which `SPEC.md`
   requires the session to survive. Resolved here by folding it into discard as `drop_missing`
   (§ 10.8) rather than adding an endpoint.
4. **A file replaced with same-size content inside the mtime granularity keeps its ETag** (§ 12.5).
5. **`GET /libraries/browse?counts=true` is O(children × files)** and can be slow on a network share
   or a drive root. Mitigated with `counts=false`, not solved.
6. **The lock only binds this server, and that is an accepted risk.** Rank Master 2, the frozen desktop
   app, does not respect `<folder>/.rankmaster.lock` (`SERVER_PLAN.md` § 3.1) and will not be taught to:
   it is frozen, it cannot be built or tested on the machine this work is done on, and the owner intends
   to keep using it alongside Rank Master 3 for as long as he likes (2026-09-17). Running both against
   one folder at the same time can therefore still corrupt `rankmaster_db.json`, and no API response can
   warn about it. The mitigation is a rule, not code — **one program per folder at a time** — and it is
   stated in `README.md` in the owner's terms. The same decision is why the database schema is frozen:
   both programs must keep reading and writing the same file.
7. **`warmPairs` can be permanently empty** on a two-file library (§ 9.5), so a client that waits for
   a warm pair before rendering will hang. Clients MUST render from `pair` alone.
8. **Mixed folders show videos that can never be ranked.** They appear in `counts.total`, in
   `counts.videos` and in `/media`, but never in a pair (`SPEC.md` § Media policy). The client is
   responsible for not implying they are rankable.
9. **`progress` is a mean over `Rankable` only.** In a mixed folder it ignores videos entirely, and
   after a discard it can move upward for reasons unrelated to voting. It is an overlay number, not a
   measurement.
10. **A rename interrupted by a crash may leave filenames mixed until re-run.** This is untidy, never
    lossy: the journal (§ 10.16) reunites every rating with its file on the next open regardless of
    how far the moves got, so a mid-rename crash is recoverable for the one thing that matters. Because
    a name belongs to exactly one of the two disjoint sets, recovery never has to guess which. The
    residual gap is cosmetic — some files may still carry their old names until the owner reruns
    rename.
11. **Up to five choices, or two seconds of voting, can be lost to a crash or a power cut** (§ 13.1).
    The per-choice durability promise was relaxed deliberately, at the owner's request
    (*"I'm not afraid of losing a couple of votes, it's non-consequential"*), because an fsync per vote
    is the dominant cost of a vote on a large library and worse again on a USB drive. The bound is the
    gap: `SaveDelaySeconds` (default 2) and `MaxUnsavedChoices` (default 5) in `appsettings.json`, with
    `SaveDelaySeconds = 0` restoring the old behaviour exactly. Everything that moves a file, every
    `POST /session/save`, every `DELETE /session` and a clean shutdown are still durable before their
    response, so the exposure is votes and skips only, and only the most recent few.
