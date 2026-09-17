# Part G — remediation of the independent audit

**Status: plan, nothing built by this plan.** Closes the findings of `AUDIT.md` (2026-09-17: 14 that
would hurt, 34 that would annoy, 27 that would confuse, 4 about tests that prove nothing, 14
cosmetic) across the server, the phone and the Windows client, and carries out the owner's answers
of the same day (§ 0.2). Written for weaker agents who will execute it **in parallel, one package
each, without re-deciding anything**. Every decision below is taken, with its reason. Where a
decision is the owner's, it says so and is not argued again.

Read first: `AUDIT.md` in full — the findings, the tests that pass without proving anything, "What
holds up" (do not break it), the coverage map, and Appendix A (the throwaway tests, which are the
acceptance criteria for the findings they prove). Then `SPEC.md`, `SERVER_SPEC.md` § 10.16 and
§ 13, `PC_CLIENT_PARTS.md` § Settled, `pc/plans/F-server-rename.md` § 1–3.

## 0. Rules, constraints, and the tree as it stands

### 0.1 Rules (all of them already this project's)

1. **An agent owns a folder or an explicit file set, and no two packages running at the same time
   touch the same file.** The boundaries in § 3 are exact. If a fix needs a file another package
   owns, the fix is split at a named seam or moved to a later wave (§ 4) — never taken by force.
2. **Spec first.** The three programs share one contract and one database format. Every change one
   of them must know about is a change to `SERVER_SPEC.md` (or `SPEC.md`) first, and package D0
   makes every such change alone, before any code package starts.
3. **Verification is expected.** The server and its suites, both clients' suites, and `rm2ctl`
   against a real TLS server all run on this box; every package ends with its suite green. Nothing
   Windows and nothing on a real device runs here; each package says what it cannot prove and § 6
   turns that into a checklist in the owner's words.
4. **The auditor's failing tests are the acceptance criteria.** Each is assigned to exactly one
   package, which makes it pass by fixing the product — or, where the audit's test documents a
   defect by *passing*, flips it to assert the fixed behaviour. No audit test is deleted unless the
   contract change in D0 makes what it asserts wrong, and then this plan names it.
5. **Run no `git` command.** Commits and tags are the coordinator's (§ 7).

### 0.2 Constraints from the owner (2026-09-17, after the audit)

- **The database schema does not change. Hard constraint.** He will keep using Rank Master 2
  alongside Rank Master 3 for as long as he likes, so `rankmaster_db.json` must stay readable and
  writable by both. No finding in this plan needs a schema change; the one that could have tempted
  one (K8, validating `version`) is *documented*, not enforced, for exactly this reason. The rename
  journal is a separate file and is not the schema.
- **The folder lock needs no work.** He does not run two clients at once; H13 is an accepted risk.
  Nothing here teaches Rank Master 2 about `.rankmaster.lock` and nothing plans its retirement. The
  README stops claiming a single writer — a documentation fix.
- **Expect any filenames at all in a folder, including `000001.jpg`** — Rank Master 2's own rename
  produces exactly that, so his folders very likely contain them already. § 1.1 says why this is no
  longer a special case.
- **Old `rankmaster_backup_*` folders are out of scope.** Nothing here mentions them again.
- **The rename progress bar and cancel button stay**, and they are not for large folders: he does
  not know how a rename behaves on a **slow USB drive** and wants to stop it the moment it looks
  wrong. § 1.1 and PC-RENAME carry that.
- **The phone does not get rename.** Confirmed.
- **Skip is removed from the Windows client's interface.** `SPEC.md` § Keys changes; the server keeps
  `POST /session/skip` and every test of it.
- **Durability may be relaxed** — *"I'm not afraid of losing a couple of votes, it's
  non-consequential."* This reopens A2 and is a genuine change of a stated guarantee; § 1.3 designs
  it and S-DURABILITY (§ 3.14) builds it as its own package.
- **The mandate:** *"I want to weed out as much of the bugs without me in the loop as possible."*
  Another independent audit follows this round, given only the business task and the code. Where a
  fix is verifiable here it is made; what genuinely needs his PC or phone is said so in § 6.
- **Delivery is through GitHub** to an agent with access to his PC. § 7 says what to tag and what
  that agent builds and runs.

### 0.3 The tree at planning time (2026-09-17, 10:27)

Another agent is mid-way through H1. Already in the tree, uncommitted: `RenameEngine.cs` draws a
per-run suffix and builds `000001-a3f9.jpg` names, **but still moves through the `__rm2_` temporaries
in two phases**, still reports `total = 2 × N`, and still writes journal format 1 (no `suffix`
field); `RenameEngineTests.cs` and a renamed `RenameSecondRunTests.cs` (the audit's
`Audit_RenameSecondRunTests`, six tests) exist; `SERVER_SPEC.md` § 10.16 and § 12.5, `openapi.yaml`'s
`startRename` description and `SPEC.md`'s rename scoping note already describe the suffix. **S-RENAME
starts from that state and finishes it to § 1.1** (temporaries out, format 2, `total = N`); D0
rewrites the parts of the spec text that still describe two phases. Nothing else on the audit's list
has been closed by that work.

---

## 1. Decisions, settled before any code

| Question | Decision | Why |
|---|---|---|
| H1, the rename corruption | **Each rename run's names carry a short per-run suffix**: `000001-7f3a.jpg`. Old and new name sets can never overlap, so recovery never has to guess whether a file has moved. § 1.1 specifies it fully; the in-flight implementation (§ 0.3) is finished to it | The owner's decision, and the right one: he does not care that a file is called `000001.jpg`, only that sorting by name gives rank order. This *removes* the ambiguity the auditor's two suggestions (per-file journal progress; renaming twice through fresh names) would only manage |
| The temp-name machinery | **Deleted.** With disjoint namespaces there is one move per file, `old → new`, and no phase 2. `TempPrefix`, `TempNameOf`, `MovePhase2`, and the temp branches of `FinalizeBestEffort` and `Reunite` go; `total` on the wire becomes `N` | Leaving a two-phase engine whose second phase can never be needed is exactly the dead complexity the owner asked not to keep. The in-flight code keeps the temporaries and argues "new, temp and old are disjoint" — true, but a set that is never populated needs no proof. The frozen desktop app's `FileOps.RenameByConservativeScore` (random-GUID temporaries) is untouched — it cannot be built or tested here |
| Folders that already hold `000001.jpg …` (from Rank Master 2's rename) | **Ordinary input, not a special case.** A name without a suffix is an old name like any other; the next run gives every file a suffixed name and cannot collide with anything already there. The only names that never appear as *input* are those of a previous suffixed run, and those are handled by the suffix differing per run | The point of the owner's fix: with a per-run suffix, recovery is unambiguous whatever it finds |
| The rename progress bar and cancel | **Kept, and made responsive on a slow drive.** Cancel is checked before every move *and* between retries of a move that is waiting on a locked file; the PC client shows `done / total` and offers Cancel from the first instant | The owner's reason: on a slow USB drive he wants to stop it the moment it looks wrong. Cancel already stops-and-reunites (never a rollback); what it needed was to land quickly |
| Rename cancelled or failed (H2, A1, C1, C11) | **Both resync the session from disk the same way**, keeping `sessionVotes` and `cues`, clearing undo, `pairSeq` +1, `lastAction` null. A cancel that lands in `preparing` (nothing moved) leaves the session untouched, as today | § 7.2 promised "cancelled *or failed* → resynced" and only cancel did it. `Start()` zeroes the session counters § 10.1 goes out of its way to protect on a resume; a new `RankingSession.Resync()` re-reads the folder without that. `pairSeq` +1 says honestly that the generation changed (§ 8.3 already admits the old tokens are stale) |
| Flag before journal (H3), corrupt journal (H4) | Flag set **after** the journal write succeeds; the write is caught and answers `500 rename_failed`. Recovery on open is wrapped: an unreadable journal answers `rename_failed { reunited:false, journal }` and **releases the lock** | Both are one-line ordering faults behind false messages ("a rename is already running", "in use by another Rank Master process") that only a server restart cures |
| Undo reaching back (H5, A7) | `undoAvailable` has **one source**: a single per-session undo point that every structural change clears — `drop_missing`, rename, resync, undo itself, session open | Two sources (engine snapshot OR `LastMove`) disagreed with `lastAction`, and the disagreement moved a file the owner had discarded on purpose |
| `DELETE /session` during a rename (H14) | **Refused, `409 rename_in_progress`.** Contract § 10.4 says so | Releasing the lock under a running rename lets a second session recover a journal the first is still executing. The race was not built; the refusal makes it unbuildable |
| Any token reaches the whole filesystem (H11) | **Kept — the product decision.** One cheap thing is done: `POST /session`'s `folder` goes through the same `PathGuard` as `/libraries/browse`, so a `..`, UNC or device path is refused at the door | A single owner on his own LAN chose full-filesystem browse; the destructive endpoints inheriting it is the same choice. The UNC guess is Windows-only and post-compromise; `PathGuard` already rejects UNC, so routing `folder` through it closes it for free |
| Pairing window killed by a stranger (H12) | The five-attempt budget becomes **per source address per window**; a corrupt `devices.json` is **moved aside** (`devices.json.corrupt-<timestamp>`), never silently emptied, and **does not** auto-open pairing — auto-open happens only when the store is *absent* (first run). The tray says which happened | The wire shape (`attemptsRemaining`) survives; the owner's own phone still has five tries; a stranger burns their own budget and hits the per-address rate limit anyway. A store that cannot be read is a thing to tell him about, not to act on by itself |
| Rank Master 2 and the server both writing one file (H13) | **Accepted risk; no engineering.** `README.md` stops saying the server is "the only writer" and says instead that Rank Master 2 keeps reading and writing the same file, which is why the schema is frozen | The owner's ruling (§ 0.2) |
| Skip on the Windows client | **Removed from the surface**: `↓`/`S` map to nothing, the help sheet does not list them, `ISessionLink.SkipAsync` stays (the link's tests exercise the endpoint, which the server keeps) | The owner's ruling; the phone already dropped it. A client-surface change only — `SERVER_SPEC.md` § 10.7 and its tests are untouched |
| Durability (A2, and the owner's relaxation) | **Relaxed by contract, bounded, off by a switch**: a vote or skip is applied in memory and answered at once; the database is written within **2 seconds** or after **5 unsaved choices**, whichever first, and always before any file move, before a rename, on `POST /session/save`, on `DELETE /session`, and on shutdown. Every write that is made stays atomic and never empty. § 1.3 has the whole design; S-DURABILITY builds it last, alone | It is a change to § 13.1's promise, so it gets spec text, its own package, its own tests of the things that must not regress (atomic, never empty, retry and stale-token rules unchanged), and a measured number so the owner can judge whether a promise was worth changing for it |
| Phone hides refusals he can fix (H6) | Silent-versus-shown decided **by code, not by snapshot presence**. Silent iff the code is `stale_pair_token` or `no_current_pair` (both mean "the pair on your screen is not current; here is the current one"). Everything else shows its text **and** adopts the snapshot if one came | Matches the PC client's rule, which the audit found correct. The server attaches a snapshot to every refusal while a session is open, so presence means nothing |
| Phone closes the PC's session unasked (H7) | **Ask first**, with the flow that already exists as dead code (`OpenFailure.AlreadyOpen`, "Close it and open this") | The comment justifying the auto-close ("never both") predates the PC client |
| The two clients disagree (C-table "Where the phone and the PC do the same thing differently") | Where the contract recommends one way, the phone moves to it: `session_busy` → wait `retryAfterSeconds`, resend once, then the modal (A15); lost undo → resend once (A16); "Forget this PC" revokes (A18). Where both are allowed and neither is wrong (401 handling, certificate mismatch), **left as they are** and the difference is written down in `CLIENT_PLAN.md` | A client that does the recommended thing needs no defence; a difference that is merely a difference needs a sentence, not a change |
| Windows client, never run (H8, H9, H10, A20) | All four fixed before the owner installs anything; each is a unit-testable omission (a lease not disposed, a missing `catch`, a call with no caller, a timer that does not exist) | These are the ones that would end his first session |
| `Esc` blocking on video release (A21) | `Quit()` **stops** both video surfaces and returns; it does not wait for release. The bounded wait stays where it is needed — before a file move | `SPEC.md`: Esc quits immediately. On quit nobody is about to move the file |
| The corpus and fixture directories absent from a clean checkout (T1, T3) | Corpus-dependent tests **skip visibly** (`Xunit.SkippableFact`, `Skip.If(...)` with the reason) instead of returning green — and the acceptance gate (§ 5) **builds both** with `tests/corpus/build-corpus.sh` and `pc/tests/make-video-fixtures.sh` first, so on this box the count of skips is zero. A theory whose data set is empty yields one skipping row instead of failing "No data found" | A test that returns early is indistinguishable from a passing one in every summary. A skip is counted and printed. Both build scripts run here today (curl, ffmpeg with SVT-AV1/x264/VP9, python3 + Pillow are present) |
| The folder-lock test (T1) | Made **cross-process**: a tiny console project `tests/RankMaster2.LockHolder/` holds `.rankmaster.lock` with `FileShare.None` while the server is asked to open the folder | The existing test locks from the server's own process; the OS has never been asked |
| The duplication catalogue (C17, C18) | **Not a remediation item.** Each package fixes the copies inside its own boundary (the data-directory spelling, C12; the shared body-size constant, C14; the second `MediaFingerprint`, C16) and leaves the cross-project ones alone | A change that crosses package boundaries for tidiness costs more coordination than it saves |
| The PC client cannot rename (A23) | **PC-RENAME, in W2, not optional**: the `ISessionLink` seam grows three members and the start screen gets the slot plan E left for it, with the progress line and the Cancel key the owner asked for | The phone will not have rename and `rm2ctl` has no bar to watch; the PC client is the only place his cancel can exist. It changes a frozen seam, so it runs after the two W1 packages that own each side of it |
| Every [guess] the audit could not settle here (A12, A26, K12, H11-UNC, A27) | The code-side fix is made where one exists (A12, A26); the confirmation is on **the owner's checklist** (§ 6) | Nothing Windows and nothing on a device runs here |

### 1.1 The rename naming scheme, exactly

Normative. D0 writes it into `SPEC.md` § Rename by rank and `SERVER_SPEC.md` § 10.16; S-RENAME builds
it from the in-flight state (§ 0.3); nothing below is re-decided by either.

**The name.** `NNNNNN-ssss.ext`

- `NNNNNN` — six decimal digits, rank 1 = `000001`, by `μ − 3σ` descending, then filename
  (ordinal, case-insensitive) for ties. Fixed width **first**, so a byte-wise sort of the names is
  rank order.
- `-` — one hyphen.
- `ssss` — the **run suffix**: exactly four lowercase hexadecimal characters, drawn once per run from
  `System.Security.Cryptography.RandomNumberGenerator` (two bytes). **Never wider**: the in-flight
  code's fallback to a sixteen-character suffix after sixty-four collisions is removed — with the
  verification below, sixteen consecutive rejections cannot happen in a real folder, and if they
  did the run fails with `rename_failed` and nothing has moved.
- `.ext` — the file's own extension, unchanged, case preserved.
- Regex, used by every test and by `rm2ctl`: `^\d{6}-[0-9a-f]{4}\.[^.]+$`.

**The suffix is per run, not per file.** Every entry in one plan carries the same `ssss`.

**The suffix is verified against what is already on disk.** A candidate is rejected and redrawn while
*any* filename in the plan's input records, or *any* top-level media file listed from the folder at
plan time, has a name which — with its extension removed — **ends in `-ssss`** (compared
case-insensitively). This is deliberately stronger than the in-flight check (which tests only the
exact names the plan would produce): it makes the rule statable in one sentence and makes a folder
containing *any* file of a previous run with that suffix, whatever its rank, a rejection. After this
check **no `new` name in the plan equals any `old` name**, and `BuildPlan` asserts that
disjointness before returning (throws `InvalidOperationException` if violated — a bug, never a
runtime condition).

**Names never accumulate suffixes.** A run rebuilds each name from rank + suffix + extension only.
`000003-b91c.jpg` becomes `000001-7f3a.jpg`, never `000001-7f3a-b91c.jpg`.

**Any input name is ordinary.** `000001.jpg` from Rank Master 2, `IMG_4711.jpg` from a camera,
`000001-b91c.jpg` from the previous run — all are simply `old` names. No code recognises any of them
specially, and nothing is migrated.

**One move per file.** `old → new` directly. `FileOps.MoveWithRetry` never overwrites: a destination
that exists is a thrown `IOException`, which fails the run safely — the journal is still on disk and
the ratings are reunited from it. There is no phase 2 and no temporary name.

**Cancel lands within one retry interval.** `MoveAll` checks `shouldStop` before each move, and
`FileOps.MoveWithRetry` gains an overload taking `Func<bool> shouldStop` that is checked between
retries of a move waiting on a locked file — so on a slow or stubborn drive a cancel is honoured
within one retry delay, not after the whole retry budget. The desktop app's parameterless overload
is untouched.

**Recovery is unambiguous.** For each top-level media file on disk: its name is looked up as `new`
first, then as `old`; the two dictionaries are disjoint by construction, so the first hit is the only
possible hit. A file matching neither gets the default rating, as `Scan` gives one. `Finalize`
(best-effort) is: if `new` exists, skip; else if `old` exists, move `old → new`. Recovery re-checks
the journal's plan for disjointness before trusting it and reports `reunited:false` if a hand-edited
journal violates it.

**The journal is format 2.** `{ "format": 2, "state": "renaming", "createdAt": "…Z",
"suffix": "7f3a", "plan": [ { "old", "new", "mu", "sigma", "matches", "impressions", "lastPlayed" } ] }`.
The reader accepts format 1 (no `suffix`; such a journal exists only in `rm2ctl` scratch folders,
never on the owner's disk) and refuses anything else, a null `plan`, malformed JSON, or an
intersecting plan as **unreadable** — a typed `InvalidDataException` that the server turns into
`rename_failed`.

**Progress.** `total` = N (the number of plan entries), `done` counts moves; phases stay
`preparing | renaming | saving | reuniting | done`.

**What this changes elsewhere.** `SERVER_SPEC.md` § 12.5's ETag note already says a name assigned by
one run is never assigned again by another; D0 tightens it to "a suffix can recur only across
non-adjacent runs, and the `(name, size, mtime)` limit then applies as for any name — theoretical".
`rm2ctl cycle`, the compatibility audit and the server rename tests use the regex above.

### 1.2 Deliberately not done

Listed so nobody reopens them. Each was weighed.

- **Anything that changes `rankmaster_db.json`'s schema.** Hard constraint (§ 0.2). No finding
  needs it.
- **H13 in code.** Accepted risk. Text only.
- **H11 as a product change.** Restricting which folders a paired token may open is a different
  product. Only the `PathGuard` call is made.
- **C19 (the `List`-based session).** Visible only at 20 000 files next to A2, and A2 is now
  addressed by batching the save, not by a faster data structure.
- **A5 as a cap on children.** `counts=false` stops emitting the `counts` key at all (shorter wire),
  and that is all; a cap would make a root listing lie.
- **A13.** The full-screen viewer fetching a 2160 variant is the viewer doing its job. Bounded by
  the 15 % memory cache; no change.
- **C5 as a new status code.** A wrong method on a known path stays `404 not_found`; the contract
  says so now.
- **C2 as code.** Media errors carry `error.session: null`; the contract says so now. Building a
  lock-free snapshot for the media layer to attach to a 404 nobody reads is cost without value.
- **C3 as one envelope writer.** `details` and `session` may be omitted or `null` interchangeably;
  the contract says so, both clients already treat the two the same, and the harness's
  `RequireErrorBody` is therefore honest as written.
- **C17, C18** beyond the in-package copies named in § 1.
- **C20's `DefaultPrefetchPairs` duplicate.** The SPEC knob lives in the frozen app's
  `MediaPipeline`; the server cannot reference that assembly.
- **`StillLease`'s `#if DEBUG` finalizer (H8's would-be detector).** H8 is fixed and pinned by a
  test; an always-on finalizer is cost on every frame for a leak that no longer exists.
- **`pc/plans/F-server-rename.md`'s location (C27).** It stays where three code comments point; D0
  adds one line at its top saying it is a server plan and that § 1.1 here supersedes its naming and
  phase details.
- **K6.** The phone's overlay omissions are deliberate and stay recorded as a deviation.
- **The phone's `ErrorCodes` constants "declared, not branched on" (C25).** P-RANK now branches on
  two of them (H6); the rest stay declared. P-NETMEDIA must **not** delete any `ErrorCodes` member.
- **The in-flight wider-suffix fallback** (§ 1.1). Removed, not kept "just in case".

### 1.3 The durability relaxation, exactly

The owner has said a couple of lost votes are non-consequential. That reopens A2 (one vote on a
20 000-file library costs ~130 ms on a local disk, several times that on USB with fsync) and it
changes a promise: `SERVER_SPEC.md` § 13.1 says that when a 2xx leaves the server the change is
already durably on disk. This section is what replaces it. It is normative; D0 writes it into the
contract, S-DURABILITY builds it, and nothing below is re-decided.

**What is wanted.** Not a cache: a **bounded write-behind**. A choice (vote, skip, or the undo of a
vote or skip) is applied in memory, `pairSeq` advances, the response leaves — and the database is
written by the server, off the request path, **no later than the earlier of** `SaveDelaySeconds`
(default **2**) after the first unsaved choice **or** the `MaxUnsavedChoices`-th (default **5**)
unsaved choice, whichever first. The loss on a crash or kill is therefore bounded by 5 choices or
2 seconds. Both values are `RankMaster2:SaveDelaySeconds` / `RankMaster2:MaxUnsavedChoices` in
`appsettings.json`; **`SaveDelaySeconds = 0` restores the old behaviour exactly** (every choice saved
before its response). They are not on `/ping` (no wire change) and not features.

**What is never deferred.** A save is forced, synchronously, inside the request, **before** the
response, for everything that is not a plain choice: before a file move (`discard`, `special`, the
undo of a move — § 13.2's "file moved, then JSON written" ordering stands, so the discard self-heal
argument stands), before a rename starts, on `POST /session/save` (this is now *the* way a client
makes a point durable, and its 200 keeps § 13.1's old meaning), on `DELETE /session` (§ 10.4 changes
from "writes nothing" to "flushes unsaved choices; writes nothing otherwise"), and on server shutdown
(`ApplicationStopping`). The flush and the forced saves take the session gate; the flush waits for
it without the 5 s client timeout.

**A write that is made is exactly as safe as before.** `JsonCatalog.Save` is untouched: temp file,
`Flush(true)`, `File.Replace`, never creates the folder, never writes an empty database over
records, merges against disk. The write-behind changes *when* it is called, not what it does.
Batching *coalesces*: five choices become one write of the whole file.

**A flush that fails.** The failure is latched on the session. While latched: the flush keeps
retrying every `SaveDelaySeconds`; every mutating call first attempts a synchronous save and, if
that fails too, answers `500 save_failed` (snapshot attached) **with nothing applied** — the token
stays valid, exactly § 8.3's existing row for "vote/skip throws in Save". So the owner cannot pile
up unsaved choices on a dead disk: the second choice after the drive goes tells him (the phone now
shows `save_failed`, H6). `POST /session/save` clearing the latch on success is the recovery.

**The retry and stale-token rules are unchanged, and a test says so.** They are about in-memory
state: the token is derived from `(sessionId, pairSeq, left, right)`; `pairSeq` advances when the
engine *enters* an action, not when the disk is written; § 13.3 (a client that lost the response
resends the same token once and gets `409 stale_pair_token` with the truth) does not mention the
disk. S-DURABILITY re-runs `RetryAndDoubleVoteTests` and `rm2ctl cycle`'s never-twice checks
against the batched server and adds a test that a lost response followed by a flush followed by a
resend still yields exactly one applied vote.

**What changes in the contract, exactly** (D0 writes it): § 13.1 the invariant becomes "a 2xx means
the change is applied and will be on disk within the bound above; `POST /session/save`'s 2xx, and
every 2xx of an endpoint that moves a file, still mean it is on disk now"; § 8.3's vote/skip rows
gain "(saved within the bound)" and the "throws in Save" row is reworded for the latched case;
§ 10.4, § 10.5, § 13.2 (vote/skip row: "JSON written **within the bound**; the response does not
wait"), § 13.4 (ratings survive a restart "except the last ≤ 5 choices / 2 s, which the owner
accepted"), § 16 (a new numbered gap stating the bound and the switch). `SPEC.md` § Persistence
keeps "Save on every choice" for the desktop app and gains one sentence: the server relaxes this by
contract, bounded, `SERVER_SPEC.md` § 13.1.

**Where it lives.** `RankingSession` gains a constructor flag `saveOnChoice` (default `true`, so the
frozen desktop app and every existing engine test are unchanged); with it `false`, `ApplyVote`,
`Skip` and the undo of a vote/skip do not call `Save()`, and their save-failure rollback path is
simply never entered (the registry owns the failure now). `SessionRegistry` owns the dirty count,
the timer, the latch, the forced-save points and the shutdown flush.

**What it buys, measured.** S-DURABILITY adds a benchmark (`[Trait("Category","Benchmark")]`, prints,
never fails) that drives 100 votes over HTTP against a 2 000-file and a 20 000-file library with
`SaveDelaySeconds = 0` and `= 2`, on tmpfs and on this box's real disk, and prints mean and maximum
per-vote latency and total wall time for each. The numbers go in the package's completion note, in
one table, so the owner can judge. Honest expectation: at his library sizes on a local SSD the gain
per vote is around ten milliseconds; on a USB drive it is the fsync, which only his PC can measure.

**Tests that read the disk after a vote** — the PC link tests (63, asserting against
`rankmaster_db.json`), `FullCycleTests`, the state-machine audit, `rm2ctl cycle` — get one rule:
**call `POST /session/save` before reading the file**, the contract's own way to make a point
durable. S-DURABILITY owns those files in W3 and applies the rule; no test waits on a timer.

---

## 2. The weak tests, treated as findings

An audit that finds eight colour and orientation tests passing while exercising nothing has found a
hole in the evidence. Each hole, and how it is closed:

| Hole | Closed by | How |
|---|---|---|
| **T1a** `PingContractTests.Authenticated_ping_summarises_the_session_without_a_404` guards its assertion with an `if` and asserts no field | S-SESSIONS | The `if` goes; the test asserts `open == true`, `sessionId` equals the snapshot's, `folder`, and `state == "ranking"` while a session is open, and `open == false` with three nulls after close. It goes green only when `/ping` is fed by the real registry (A4, C15) |
| **T1b** `SessionLifecycleTests.A_folder_whose_lock_is_held_elsewhere_is_refused` locks from the server's own process | S-SESSIONS | The lock is held by a child process (`tests/RankMaster2.LockHolder`, a ten-line console app referenced by the test project, started with `dotnet <dll> <folder>` and released by closing its stdin). The "advisory locks" escape hatch is deleted: on Linux .NET emulates `FileShare.None` with `flock`, which is cross-process, so the 423 is real here too |
| **T1c** `StillRendererCorpusTests` (8 sites) and `StillColourAndOrientationTests` return "skipped: corpus not built" | S-MEDIA | `Xunit.SkippableFact` in `RankMaster2.Server.Tests.csproj`; every early `return` becomes `Skip.If(Stills is null, Corpus.Missing)` (or the more specific reason already printed). `tests/corpus/README.md` says "skips visibly", and the acceptance gate builds the corpus first so this box reports **0 skipped** |
| **T1d** `RenameEngineTests` never used a previously-renamed folder | S-RENAME | `RenameSecondRunTests.cs` now exists (§ 0.3, six tests); S-RENAME keeps it, drops its phase-1 wording, and adds a theory over a folder already named `000001-b91c.jpg …` interrupted at every step (before any move, after k of N moves, after all moves, after the DB save) asserting the new suffix differs from `b91c` and every rating stays on its bytes |
| **T1e** `ContractShape.RequireErrorBody` accepts both envelope shapes | D0 | The contract now allows both; the harness is honest as written. No change |
| **T2a** `RankViewModelTest` builds every `Refused` without a session — a shape the server never sends | P-RANK | Every refusal fixture gains the snapshot the real server attaches; the six `AUDIT …` tests are flipped to assert the shown/silent rule of § 1 |
| **T2b** `FakeRankClient` answers every unqueued call with a canned success | P-RANK | An unqueued call throws `IllegalStateException("unqueued <call>")`. Every test that relied on the canned answer is made to queue what it means |
| **T2c** `LoadControlBudgetTest`, `PaneMenuDesignTest:131-141`, `CancelNotchTest:118-132` assert constants against thresholds | P-NETMEDIA (the first), P-RANK (the other two) | Kept, renamed to say what they are (`…design guard`) so nobody mistakes them for behaviour |
| **T2d** `PairingFlowTest:160` fakes a server message the server never produces | P-BROWSE | The fixture carries `details: {"attemptsRemaining": 3}` and a plain message, as the server does; the code reads `details` (A19) |
| **T2e** `RankViewModelTest:236` asserts `sessionClosed` "before the screen goes", which the code does not guarantee | P-RANK | `leave()` navigates first by design; the test asserts the close happens, not that it happens first |
| **T3a** 56 early-return sites across 9 PC test files; two LibVLC theories fail outright with "No data found" | PC-STILLS (5 files), PC-VIDEO (`LibVlcTests`, `Video/MediaProbeTests` deleted with C22), PC-APP (`ShellTests`), PC-LINK (`Harness/RealServer`) | `Xunit.SkippableFact` in the two PC test projects; every `return` that stands for "cannot run here" becomes `Skip.If(...)` with its reason; a `MemberData` source that would be empty yields one row whose test skips with "no fixtures — run pc/tests/make-video-fixtures.sh". The PC solution is green from a clean checkout, with the skips counted |
| **T3b** `Video/MediaProbeTests` prove a class nothing uses | PC-VIDEO | Deleted with the class (C22) |
| **T4** Nothing has run on Windows or a device | § 6 | The owner's checklist, in his words |

Two rules for every package touching tests: (1) no test keeps the word `AUDIT`/`Audit_` in its name
once it asserts fixed behaviour — rename it to the behaviour it proves; (2) a test that documents a
defect by passing is never left passing after the defect is fixed — it is flipped, so a regression
makes it fail.

---

## 3. The packages

Each package: **Owns** (the only files it may create, edit or delete — exact), **Closes** (findings),
**Do** (the work, in order), **Done when**, **Proven here by**, **Only on the owner's machine**.
Packages in the same wave (§ 4) share no file. Later-wave packages may touch files a finished
earlier-wave package owned, and say which.

Run commands with `export PATH="$HOME/.dotnet:$PATH"`. Server: `dotnet test RankMaster2.Server.slnf`.
PC: `dotnet test pc/RankMaster2.Pc.sln`. Phone: `cd android && ./gradlew :app:testDebugUnitTest`.

### 3.0 D0 — the contract and the documents (W0, alone)

**Owns** `SPEC.md`, `SERVER_SPEC.md`, `openapi.yaml`, `README.md`, `SERVER_PLAN.md`,
`SERVER_RUNNING.md`, `PC_CLIENT_PLAN.md`, `PC_CLIENT_PARTS.md`, `CLIENT_PLAN.md`,
`pc/plans/F-server-rename.md` (one header line only), `tests/corpus/README.md`,
`Directory.Build.props` (root; the version only).

**Closes** C1 (text), C2, C3, C5, C8, C10, C11, C12 (text), C27, K2 (docs), K8, A23 (interim text),
A29 (docs), H13 (text), K6 (recorded); the skip removal (spec); and writes the contract for H1
(finishing the in-flight text), H2, H3, H4, H5, H7, H12, H14, A1, A5, A7, C9, and § 1.3.

**Do**

1. `Directory.Build.props`: `<Version>3.1.0</Version>` (a contract-visible change and client
   changes; README's "Current version" line to match). `src/RankMaster2.App/Directory.Build.props`
   (the frozen app's own) is not touched.
2. `SPEC.md` § Rename by rank: the scoping note already names the suffix; make step 4 say the
   desktop app produces `000001.ext` and the server produces `NNNNNN-ssss.ext` per § 1.1 of this plan
   (format, per-run suffix, verification against disk, suffixes never accumulate, unsuffixed names
   are ordinary old names, one move per file on the server). § Keys: the `↓` / `S` row becomes
   "Skip — **desktop app only**; not offered by the PC client or the phone; the server keeps
   `POST /session/skip`". § Persistence: keep "Save on every choice" and add the one-sentence server
   relaxation of § 1.3; document K8 (`version` not validated; `filename` ignored on load, the key
   wins) and say why it stays that way (Rank Master 2 keeps writing the file). § Ranking: one
   sentence — `Resync()` re-reads the folder like `Start()` but keeps the session vote count and the
   cues; the server uses it after a cancelled or failed rename. First paragraph: this file is the
   engine's and the desktop app's behaviour; the server contract is `SERVER_SPEC.md`; the "App
   version … 1.1.4" line describes the frozen app; drop the `C:\Utils\…` repo line.
3. `SERVER_SPEC.md` § 10.16: finish the in-flight text to § 1.1 — delete every mention of `__rm2_`
   temporaries and of two phases; `total` = N; the journal is format 2 with `suffix`; the
   unreadable-journal rule; the recovery table's rows become: after the journal, before any move /
   after k of N moves / after all moves, before the DB save / after the DB save, before the journal
   delete / after the delete — each reuniting "because a name is either an `old` or a `new`, never
   both". "The honest slow-phase truth" becomes **"Why there is a bar and a cancel"**: directory
   moves are near-instant on a local disk, but the owner does not know how a slow USB drive behaves
   and wants to stop a rename the moment it looks wrong; cancel is honoured before every move and
   between retries of a move waiting on a locked file; it stops where it is, undoes nothing already
   done, and loses no rating. Add **"Cancel or failure — the session effect"** per § 1's row (same
   `sessionId`, records re-read, `Current` re-picked, `pairSeq` +1, `sessionVotes` and `cues` kept,
   undo cleared, `lastAction` null, old tokens stale; a cancel in `preparing` leaves the session
   untouched; if the resync cannot read the folder the session is closed, the lock released, the
   operation `failed` with `reunited` truthful, and the client's next call gets `404 no_session`).
   Add: the flag is set after the journal write succeeds; a failed journal write answers
   `500 rename_failed { reunited:true, journal:null }` with the session untouched. Add: recovery on
   open that finds an unreadable journal answers `rename_failed { reunited:false, journal }`, does
   not open, and releases the lock. Add: the body of `POST /session/rename` is optional JSON
   (`{ clientRequestId? }`); malformed JSON → `400 invalid_json`. Add: `DELETE /session` while a
   rename runs → `409 rename_in_progress`.
4. `SERVER_SPEC.md` § 7.2: the rename-cancelled/failed row → `pairSeq` +1, "resynced; counters kept";
   add a row for `DELETE /session` during a rename → unchanged, `409`. § 8.3: same row → **+1**.
   § 10.4: the refusal during a rename; the flush of § 1.3. § 10.5: `POST /session/save` is the way a
   client makes a point durable. § 10.10: `undoAvailable` is true iff the last action was
   `vote|skip|discard|special` and nothing structural (drop_missing, rename, resync, undo, open) has
   happened since — one source. § 12.5: the tightened ETag sentence of § 1.1. § 13.1, § 13.2,
   § 13.4, § 16: the durability text of § 1.3, verbatim in meaning. § 16 item 10: drop `__rm2_`.
5. `SERVER_SPEC.md` § 4: `details` and `session` are nullable and **MAY be omitted when null**;
   clients MUST treat absent and `null` identically. `/media/*` errors always carry `session: null`
   (or omit it). § 6: add the row "a known path with an unsupported method → `404 not_found`; no
   `Allow` header is promised". § 10.11 / § 15: "5 attempts per source address per window"; a
   window is destroyed only when *the guessing address* exhausts its budget; the per-address rate
   limit stands. § 10.15: when `counts=false` the `counts` key is **absent**. § 2.4: one spelling of
   the data directory — `RankMaster2/Server` (Windows `%LOCALAPPDATA%\RankMaster2\Server`, Linux
   `$XDG_DATA_HOME/RankMaster2/Server`), the still cache at `<data dir>/cache`; add the two
   `appsettings.json` keys of § 1.3. § 14: `version` example `3.1.0`. § 5.3.1: unchanged.
6. `openapi.yaml`: `servers` block → a plain `https://{host}:{port}/api/v1`, `host` described as "the
   LAN address the tray shows; there is no mDNS" (C8); both `/ping` examples `version: "3.1.0"`
   (C10); `startRename` description: finish it to § 1.1 (no temporaries, one move per file, cancel
   for a slow drive); `RenameOperation.total` description → "N, the number of files";
   `RenameStartRequest` and its `400` stay (C9 is closed by the server reading the body);
   `POST /session/save` description gains the § 1.3 meaning. No route, type or enum changes.
7. `README.md`: title "Rank Master 3" (keep the "repository still says RankMaster2" paragraph);
   "Current version: 3.1.0"; the "only writer" bullet becomes "the only writer *among the three
   programs of Rank Master 3*; Rank Master 2 keeps reading and writing the same file, which is why
   the database format is frozen" (H13 — a statement, not a rule); "Run" names the server
   (`SERVER_RUNNING.md`), the PC client (`pc/`), says `src/RankMaster2.App` is frozen; Keys table:
   `Ctrl+Z` → "take back the last action (vote, discard, special)", the skip row → "desktop app
   only"; under 3.1.0: the audit round in three lines (rename names carry a per-run tag; votes are
   saved within two seconds, not on every press — `appsettings.json` restores the old way; the
   Windows client's first-run fixes), and "Rename by rank is started from the PC client's start
   screen (or `rm2ctl`)" — PC-RENAME ships in this round, so the interim `rm2ctl`-only line is not
   needed; write it as if PC-RENAME exists and let PC-RENAME's completion confirm it.
8. `SERVER_RUNNING.md`: the data-directory spelling; the two new keys with their defaults and the
   sentence "0 saves on every vote, as before"; under `ListenAddress`, in the owner's words: "On
   `127.0.0.1` the server is reachable from this PC only; a phone needs the PC's LAN address here,
   and the tray icon says which one it is listening on" (A29).
9. `PC_CLIENT_PLAN.md`: § 6.6 gets a leading line "**Superseded** — rename is the server's
   (`PC_CLIENT_PARTS.md`, 2026-09-16); the PC client calls `POST /session/rename` (part G,
   PC-RENAME)"; § 13's six decisions each get their answer and date (1: both, measured by the kit;
   2: keep, on the server; 3: any action; 4: no; **5: drop skip** (2026-09-17); 6: start it) so
   nothing there reads as open (C27).
10. `PC_CLIENT_PARTS.md` § Settled: append, dated 2026-09-17, the owner's answers that touch the PC:
    rename names carry a per-run suffix (his reasoning in two sentences, pointer to § 10.16); the
    bar and cancel are for a slow drive; skip is removed from the PC client; durability is relaxed
    by contract (pointer to § 13.1).
11. `CLIENT_PLAN.md`: title "Rank Master 3 — Android client"; a short table "Where the phone and the
    PC differ on purpose" — 401 mid-action (phone: re-pair by hand; PC: re-enrols from the local
    `pairing.json`, which only the PC can read) and certificate mismatch (same reason) — one line
    each; K6 recorded as a deliberate deviation from `SPEC.md` § Overlay; "the phone does not offer
    rename" stated as the owner's decision.
12. `SERVER_PLAN.md`: check § 4/§ 7 say journalled, no backup, suffixed names, single move phase;
    fix any "two-phase" wording. `pc/plans/F-server-rename.md`: the one header line of § 1.2.
    `tests/corpus/README.md`: "tests that use it skip cleanly" → "skip **visibly** (counted as
    skipped, never green); the acceptance gate builds it first".

**Done when** every item above is in the text; `dotnet test RankMaster2.Server.slnf` still passes
(the contract-triangle tests — `ErrorCodeTableTests`, `OpenApiContractTests`, `PingContractTests` —
parse these documents; no code changed, so no count moves except that `PingTests` and any test
comparing the version string to the assembly still pass with 3.1.0); the audit's drift table C1–C14
has a row-by-row answer in the text (either the spec now says what the server does, or the spec now
says what a later package will make the server do); and `grep -n "__rm2_\|2 × N\|two-phase" SERVER_SPEC.md openapi.yaml SPEC.md` finds only the desktop app's own step 3.

**Proven here by** the server suite green; a read-through of § 10.16 and § 13.1 against § 1.1 and
§ 1.3 of this plan by the coordinator. **Only on the owner's machine:** nothing.

### 3.1 S-RENAME — the rename engine and its gates (W1)

**Owns** `src/RankMaster2.Catalog/RenameEngine.cs`; `src/RankMaster2.Catalog/FileOps.cs` (**one
overload added; `RenameByConservativeScore` and the existing `MoveWithRetry` untouched**);
`tests/RankMaster2.Catalog.Tests/**`; `tests/RankMaster2.Server.Tests/RenameTests.cs`;
`tests/RankMaster2.Audit.Compatibility/**`; `src/rm2ctl/**`. Nothing else in `Catalog/`.

**Starts from** the in-flight state of § 0.3. If that work has moved on by the time this package
starts, the executor reads the file as it is and finishes it to § 1.1; the list below is what must
be true at the end, not a diff.

**Closes** H1; the engine half of H4; T1d; K7 (journal); K3; A31; C12 (rm2ctl's copy); C24 (rm2ctl
alternative offer filenames); A2's benchmark (kept, not failing).

**Do**

1. `RenameEngine`: § 1.1 exactly. `BuildPlan(records, runSuffix = null)` keeps its signature (the
   registry — not yours — calls it) and adds `BuildPlan(string folder, records, runSuffix = null)`
   which also lists the folder (`JsonCatalog.ListTopLevelMedia`) and verifies against that; the
   collision rule is "any name whose stem ends in `-ssss`"; sixteen draws, then
   `InvalidOperationException`; **no wider suffix**. `RenameJournalDto` gains `Suffix`
   (`"suffix"`), `JournalFormat = 2`; `ReadJournal` accepts 1 or 2 and throws `InvalidDataException`
   for any other format, a null `plan`, malformed JSON, or an intersecting plan. `WriteJournal`:
   delete-then-move rather than `File.Replace`, and on Windows set `FileAttributes.Hidden` after
   the write (K7).
2. Delete `TempPrefix`, `TempNameOf`, `MovePhase2`, and every temp branch (`Collides`,
   `FinalizeBestEffort`, `Reunite`). Rename `MovePhase1` → `MoveAll` (`old → new`; `shouldStop`
   before each move; `onProgress`). `MoveAll` passes `shouldStop` into the new
   `FileOps.MoveWithRetry(source, dest, Func<bool> shouldStop)` overload, which checks it between
   retries and throws `OperationCanceledException` when it fires. **Leave two `[Obsolete("Removed by
   G-audit-remediation § 3.9")]` one-line shims** so `SessionRegistry` still compiles in W1:
   `MovePhase1(...) => MoveAll(...)` and `MovePhase2(...)` which moves nothing and returns
   `plan.Count` (so `done` still reaches the current `2 × N` until S-SESSIONS sets N). Rewrite the
   class comment: no "deterministic temporaries" paragraph.
3. `RenameEngineTests`: fixed-name assertions → the regex; delete
   `Deterministic_temps_are_identifiable`; the crash theory keeps its rows minus the phase-2 ones;
   add `Run_suffix_is_never_one_already_on_disk` (a folder holding `000001-7f3a.jpg`, a forced RNG
   yielding `7f3a` first → the plan uses the second draw; a folder holding `IMG_9-7f3a.jpg` also
   rejects `7f3a`), `Sixteen_rejected_draws_fail_before_anything_moves`,
   `Names_of_one_run_sort_in_rank_order`, `Old_and_new_names_are_disjoint`,
   `An_unreadable_journal_is_refused_not_guessed_at` (truncated file, `plan: null`, format 3,
   intersecting sets), `Cancel_lands_between_retries_of_a_locked_move` (a destination held open
   with `FileShare.None`; cancel after the first retry; assert the move loop returned within one
   retry interval and nothing after it moved). `RenameSecondRunTests`: keep all six, drop
   "phase 1" from names and comments, add the theory of § 2 T1d.
4. `Audit_SaveCostTests.cs` → `SaveCostBenchmark.cs`, `[Trait("Category", "Benchmark")]`, prints,
   never asserts a time.
5. `tests/RankMaster2.Server.Tests/RenameTests.cs`: regex at ≈72 and ≈89 → § 1.1's; leave any
   assertion about `total` alone (S-SESSIONS changes it in W2).
6. `tests/RankMaster2.Audit.Compatibility/RoundTripTests.cs` ≈184: the regex. Also add
   `A_folder_already_named_by_Rank_Master_2_renames_cleanly` — files `000001.jpg … 000006.jpg` with a
   v1 database keyed by them, open, vote, rename to success, close, `Scan` → every rating on its
   bytes, keys match the regex, `Db.RequireV1Schema` passes (the schema constraint, proven).
7. `src/rm2ctl/Cycle.cs` ≈135, ≈153: the regex; the check text says "`NNNNNN-ssss.ext`". `Journal.cs`:
   the 110-character trim never applies to a line that carries a token or a code — print the token
   once, in full (K3). `Program.cs`/`Rm2Api.cs`: `pair --take` pins the fingerprint from the offer it
   just read; `--pin` overrides it; `--insecure` is the only way to skip pinning (A31).
   `Pairing.cs`: the § 2.4 spelling (C12); delete the `pair.offer`/`pairing.offer.json` fallbacks
   (C24).

**Done when** `grep -n "TempNameOf\|TempPrefix\|__rm2_\|RandomSuffix(8)" src/RankMaster2.Catalog/RenameEngine.cs`
finds nothing but the two `[Obsolete]` shims; `dotnet test tests/RankMaster2.Catalog.Tests` green
including all of `RenameSecondRunTests`; the server suite and the three audit suites green;
`rm2ctl cycle` against a real TLS server passes its rename step with the new regex.

**Proven here by** all of the above. **Only on the owner's machine:** that `.rankmaster-rename.json`
is hidden in Explorer (K7); how a rename behaves on his USB drive (the bar and the cancel, once
PC-RENAME exists) — § 6.

### 3.2 S-MEDIA — the media layer and the corpus (W1)

**Owns** `src/RankMaster2.Server/Media/**`; `tests/RankMaster2.Server.Tests/Media/**`;
`tests/RankMaster2.Server.Tests/MediaTests.cs`; `tests/RankMaster2.Server.Tests/Fixtures/**`;
`tests/RankMaster2.Server.Tests/RankMaster2.Server.Tests.csproj` (package reference only);
`tests/corpus/build-corpus.sh`, `tests/corpus/make_icc.py`.

**Closes** A6, C2 (code/comment side), K9, C12 (`MediaOptions.DefaultCacheDirectory`), T1c.

**Do**

1. `StillCache.EnsureScanned`: the full-directory enumeration runs once on a thread-pool task started
   from the cache's constructor (or first touch), never on a request thread; until it completes, a
   request bypasses the cache (renders, does not write). Test: a cache directory with 2 000 files —
   the first render returns before the scan finishes (a gate on the enumerator).
2. `MediaHttp.WriteErrorAsync`: keep `session: null`; the comment cites § 4 as amended (C2).
3. `StillCache.Dispose`: stop accepting renders, wait for in-flight ones (bounded), then dispose the
   semaphores (K9).
4. `MediaOptions.DefaultCacheDirectory` → `<data dir>/cache` with the § 2.4 spelling (C12).
5. Add `Xunit.SkippableFact` to the test project; `StillRendererCorpusTests` and
   `StillColourAndOrientationTests`: every `return` → `Skip.If(...)` with the existing reason text;
   delete the class comment explaining why `SkippableFact` was not used.

**Done when** the server suite is green; with the corpus **absent** (move `tests/corpus/media` aside
for the check, then back) the run reports the corpus tests as **skipped**, not passed; with it present,
0 skipped.

**Proven here by** the two runs above. **Only on the owner's machine:** nothing.

### 3.3 S-SECURITY — pairing, the gate, `/ping`'s fallback (W1)

**Owns** `src/RankMaster2.Server/Security/**` **except** `SessionStatus.cs` (S-SESSIONS's);
`tests/RankMaster2.Server.Tests/{AuthenticationTests,PairingTests,PingTests,TransportTests,LibraryBrowseTests}.cs`;
`tests/RankMaster2.Audit.Security/**`.

**Closes** H12, A5, A32, C12 (`DataDirectory.cs`, `Rm2SecurityOptions`), C14 (the constant), C24
(Security dead members), K4, K13; the security side of T1a.

**Do**

1. `PairingService`: the attempt budget is per `(window, source address)`; `attemptsRemaining` in
   `details` is that address's; the window is destroyed when *that* address hits zero, and the offer
   file is deleted then as now. `Redeem`: persist `devices.json` **before** marking the window
   consumed; if persisting throws, the window stays valid and the response is `500 internal_error`
   (A32).
2. `TokenStore.Load`: on `JsonException`, rename the file to `devices.json.corrupt-<yyyyMMdd-HHmmss>`,
   log at Error with the new path, load as empty, and expose `WasAbsentAtLoad`.
   `SecurityEndpoints` `ApplicationStarted`: auto-open pairing only when `WasAbsentAtLoad`, never
   when the store was unreadable.
3. `SecurityEndpoints.OpenPairingWindow` (≈193): the code is **not** written to the log at any level;
   the log line says a window opened, until when, and where the offer file is (K4). The tray and
   `rm2ctl` read the offer file, so nothing else needs the code. `/ping` fallback (≈77):
   `?? new ClosedSessionStatusProvider()` — the reflective class is deleted by S-SESSIONS in W2; do
   not touch `SessionStatus.cs`.
4. `LibraryBrowser`: with `counts=false` the `counts` property is omitted (A5).
5. `public static class Limits { public const int MaxJsonBodyBytes = 65536; }` in `Security/`; the
   middleware and `/ping` read it (C14). `SessionBody.MaxBytes` is S-SESSIONS's to point at it.
6. `DataDirectory.cs` / `Rm2SecurityOptions.ResolveDataDirectory`: the § 2.4 spelling (C12); add the
   two § 1.3 options to `Rm2SecurityOptions` (or the options class the registry reads — if that is
   in `Sessions/`, leave it for S-SESSIONS and say so in the completion note).
7. Delete `SecurityState.TlsConfigured`, `CertificateStore.WasGenerated`, `TokenStore.Devices`,
   `PairingService.IsWindowOpen` (C24). `PairingRateLimiter.KeyFor(null)` → bucket `"loopback"` (K13).
8. `tests/RankMaster2.Audit.Security`: `Five_wrong_guesses_from_a_stranger_destroy_the_owners_window`
   → flipped: five wrong guesses from address A leave address B's budget and the window intact;
   `A_corrupt_device_store_kills_every_token_and_opens_pairing_by_itself` → flipped: the file is
   moved aside, no window opens, the log names the path;
   `Session_open_accepts_a_dot_dot_path_that_browse_refuses` → **deleted** here (S-SESSIONS adds the
   flipped one in `SessionLifecycleTests` in W2);
   `Authenticated_ping_session_block_has_null_sessionId_and_state` → deleted (S-SESSIONS adds the
   honest assertion in `PingContractTests`); `A_401_while_a_session_is_open_carries_no_snapshot`
   and `Audit_RateLimitProbe` → kept, renamed without the audit prefix.

**Done when** the server suite and the security audit suite are green, `grep -rn "TlsConfigured\|WasGenerated\|IsWindowOpen" src/RankMaster2.Server` finds nothing, and no log line contains a pairing code.

**Proven here by** the suites. **Only on the owner's machine:** nothing.

### 3.4 S-TRAY — the notification-area host (W1)

**Owns** `src/RankMaster2.Tray/**`.

**Closes** A29 (tray), A30, A34, K5 (tray text), the tray half of H12.

**Do**

1. `TrayApp`: when `ListenAddress` is a loopback address, the status item reads "Listening on
   127.0.0.1:18611 — this PC only; phones cannot connect (see SERVER_RUNNING.md)". Otherwise as now.
2. `PairingForm`: poll the offer file every second; if it disappears before `ExpiresAt`, stop the
   countdown and show "Closed: too many wrong codes. Open a new one." (A30). If a
   `devices.json.corrupt-*` file exists in the data directory, the tray menu shows a disabled line
   "Device list was unreadable — pair the phone again" (H12).
3. `TrayApp.RefreshSession`: the poll runs on a background task and marshals its result to the UI
   thread; an exception is logged once and the previous text kept (A34).
4. "Could not open the data folder: " + `e.Message` → "The server's data folder could not be opened
   (<path>). Check that the folder exists and is writable." (K5).

**Done when** `dotnet build src/RankMaster2.Tray -r win-x64` succeeds. **Proven here by** the build
only. **Only on the owner's machine:** all four behaviours — § 6.

### 3.5 PC-LINK — the server link (W1)

**Owns** `pc/src/RankMaster2.Pc/Link/**`; `pc/tests/RankMaster2.Pc.Link.Tests/**`.

**Closes** the link half of H9; A28; A33; C12 (`ServerPaths`); T3a (`Harness/RealServer.cs`).

**Do**

1. `SessionLink.EnterBusy`: while `ConnectAsync` holds the gate, a caller **waits** for it (bounded by
   the connect's own budget, ~18 s) instead of throwing `InvalidOperationException`. Any other
   concurrent caller still throws (that rule stands). `AuditBusyGateTests` → flipped and renamed:
   `OpenAsync_during_ConnectAsync_waits_for_it_and_then_opens`.
2. `Enrolment/ServerPaths`: resolve the data directory in the server's order — `appsettings.json`
   beside the server exe (`RankMaster2:DataDirectory`), then `RM2_DATA_DIR`, then the § 2.4 default
   with its spelling (A28, C12). `NoServerFound` names the directory actually searched.
3. `Enrolment/OfferChannel.Read`: also catch `InvalidOperationException` from `GetString()` on a
   malformed `pairing.json` (A33).
4. `Harness/RealServer.cs`: the two `return`s that mean "cannot run" → `Skip.If` (add
   `Xunit.SkippableFact` to this test project).

**Done when** `dotnet test pc/tests/RankMaster2.Pc.Link.Tests` green (63 + the flipped one).
**Proven here by** the suite (real Kestrel, real TLS). **Only on the owner's machine:** A28 with a
real `appsettings.json` — § 6.

### 3.6 PC-UI — the ranking surface (W1)

**Owns** `pc/src/RankMaster2.Pc/Ui/**`; `pc/tests/RankMaster2.Pc.Tests/Ui/**`;
`pc/tests/RankMaster2.Pc.Tests/AuditLeaseLeakTests.cs`,
`pc/tests/RankMaster2.Pc.Tests/AuditOpenWhileBusyTests.cs`.

**Closes** H8, the surface half of H9, H10, A20, A21 (surface half), A22 (`panes_painted`), A24,
A27 (Esc inside the picker), C23, C26 (Ui items), K1; the skip removal.

**Do**

1. `RankCoordinator.OnStillChanged`: a `StillState.Ready` the pane does not consume (pane not
   `Waiting`/`Refining`, id mismatch, or superseded generation) has its lease **disposed** (H8).
   `AuditLeaseLeakTests` → `Reused_pane_disposes_the_lease_that_Show_raises_for_it`.
2. `OpenFolderAsync`, `TryUndoFromStartAsync`: wrap the link call; on any exception
   `Start.EndOpening()`, `Start.ShowMessage("Could not open the folder: <message>")`, `RaiseChanged()`
   (H9). `AuditOpenWhileBusyTests` → `A_throwing_OpenAsync_returns_the_start_screen_to_its_buttons`.
3. Pane size (H10): `RankCoordinator.SetPaneSize(int widthPx, int heightPx)` forwards to
   `IStillSource.SetPaneSize`; `RankView`/`PaneControl` call it from `Loaded` and `SizeChanged`
   with bounds × render scaling, and call each `IVideoSurface.SetPaneSize(PixelSize)` themselves
   (the Avalonia-typed call `RankCoordinator`'s header already assigns to Views). Tests:
   `FakeStillSource` records the size; a headless `UiRootTests` case asserts the call on layout.
4. A repaint tick (A20): `UiRoot` owns a 250 ms `DispatcherTimer` calling `RankCoordinator.Tick()`;
   `Tick` runs `RankModel.ClearToastIfExpired` and the late-action-line rule and raises `Changed`
   only when something changed. Tests with the fake clock: a toast expires; the late line appears at
   ≥ 300 ms into an action.
5. `Quit()` (A21): call `Stop()` on both video surfaces, do not `Dispose`/wait, raise
   `QuitRequested`. `ReleaseVideoSurfaces()` stays for the pre-move path. Test with a `FakeVideo`
   whose `Dispose` blocks: `Quit` returns in under 50 ms.
6. Skip removed: `KeyMap` maps `↓` and `S` to nothing (the "modifiers before letters" rule and its
   `Ctrl+S` test stay); `HelpRows` drops the skip row; `Intent.Skip` and the coordinator's skip path
   are deleted; `KeyMapTests`/`HelpRowsTests` updated; `ISessionLink.SkipAsync` (PC-LINK's) is
   simply no longer called.
7. `panes_painted` (A22): the Views call `StartupClock.Mark("panes_painted")` on the first render in
   which both panes are `Ready` (stills) or have a frame (video) — once per folder open.
8. A24: `Notices.ForAction` maps `ActionResult.Resynchronised` with `ResyncReason.SessionReplaced`
   to a toast "Folder reopened — session count restarts" (the enum value already exists in `Link/`).
9. A27: `Esc` while `Start.DialogOpen` or `Rank.DialogOpen` is ignored, in the coordinator, so it
   holds whether or not Avalonia routes the key from inside the native picker.
10. C23: `RankModel.TakeQuitting()` → `IsQuitting` (a getter named as one; once quitting, always).
    C26: `ApplySnapshotSync`'s `IsStill && IsStill` → the intended left/right check;
    `ReleaseHandlesBeforeMove` no longer disposes a lease `PaneControl.Render` already disposed
    (one owner per lease — write it in the comment); `PaneKind.Refining` doc says it is reserved.
    K1: `StartView.axaml` `Text="Rank Master 3"`.

**Done when** `dotnet test pc/tests/RankMaster2.Pc.Tests --filter FullyQualifiedName~Ui` green plus
the two flipped audit tests; `grep -rn '"Rank Master 2"' pc/src` finds nothing; `grep -rn "Skip" pc/src/RankMaster2.Pc/Ui` finds nothing.

**Proven here by** the suite (headless Avalonia). **Only on the owner's machine:** a sharp picture on
his monitor (H10), toasts that disappear (A20), instant Esc (A21), no exit from inside the folder
picker (A27), `↓`/`S` doing nothing — § 6.

### 3.7 PC-APP — startup, shell, packaging (W1)

**Owns** `pc/src/RankMaster2.Pc/App/**`; `pc/src/RankMaster2.Pc/RankMaster2.Pc.csproj`;
`pc/Directory.Build.props`; `pc/install.ps1`; `pc/publish.sh`; `pc/kit/**`;
`pc/tests/RankMaster2.Pc.Tests/App/**`.

**Closes** A22 (the link marks and the kit block), A25, A21 (the swallowed close failure), C26
(`MainWindow` cref, the props comment), K2 (props); T3a (`ShellTests`).

**Do**

1. `WakingSessionLink` / `MainWindow.OnOpened`: `StartupClock.Mark("link_connecting")` before
   `ConnectAsync`, `"link_ready"` on `ConnectResult.Connected`, `"tray_started"` when
   `Connected.StartedServer` is true. `pc/kit/kit.ps1`'s pasted block lists all five marks of plan A
   § 6.5 (`panes_painted` comes from PC-UI, `first_video_frame` from PC-VIDEO).
2. `CrashLog.Write`: `%LOCALAPPDATA%\RankMaster2\pc\crash-<yyyyMMdd-HHmmss-fff>[-n].txt`
   (`$XDG_DATA_HOME/RankMaster2/pc` elsewhere), never the install directory; `install.ps1` is left
   as it is (A25).
3. `AppLifetime.SafeCloseAsync`: the catch writes the exception's type and message to the startup
   log before giving up (A21). Esc still ends the process within the existing 500 ms cap.
4. `MainWindow.axaml.cs:7` cref → the real root type; `pc/Directory.Build.props` comment → "the
   root is 3.1.0" (C26, K2). `ShellTests`' one `return` → `Skip.If`.

**Done when** `dotnet test pc/tests/RankMaster2.Pc.Tests --filter FullyQualifiedName~App` green and
`pc/publish.sh --no-upload` produces a `win-x64` folder. **Proven here by** the suite and the
publish. **Only on the owner's machine:** the kit's five marks in the report block; a crash file in
the new place — § 6.

### 3.8 PC-STILLS and PC-VIDEO — the two media parts (W1)

**PC-STILLS owns** `pc/src/RankMaster2.Pc/Stills/**`; `pc/tests/RankMaster2.Pc.Stills.Tests/**`
(including its `.csproj`). **Closes** T3a for `StillDecoderCorpusTests`, `StillDecoderBrokenTests`,
`StillSourceCorpusTests`, `StillDecoderColourTests`, `MediaProbeTests` (Stills), `TimingReportTests`.
**Do:** add `Xunit.SkippableFact`; every "cannot run here" `return` → `Skip.If` with the reason
(corpus absent, file absent, Skia did not parse the profile); nothing in `Stills/` source changes.
**Done when** the Stills suite is green with 0 skipped after the corpus build, and reports skips
(not passes) with the corpus moved aside.

**PC-VIDEO owns** `pc/src/RankMaster2.Pc/Video/**`; `pc/tests/RankMaster2.Pc.Tests/Video/**`;
`pc/tests/RankMaster2.Pc.Tests/RankMaster2.Pc.Tests.csproj` (package reference only);
`pc/tests/make-video-fixtures.sh`; `pc/tests/run-video-linux.sh`. **Closes** A26, C22, T3a/T3b
(`LibVlcTests`, `Video/MediaProbeTests`), A22 (`first_video_frame`).
**Do:** (1) `LibVlcBackend.Statistics()`: `using var media = _player.Media;` — the wrapper is
disposed every call; `AuditLibVlcSharpMediaGetterTests` stays as the proof of the library fact,
renamed. (2) Delete `Video/MediaProbe.cs`, `Video/IMediaProbe.cs` and `FolderPolicy` if it lives
there, and `Video/MediaProbeTests.cs`; `Composition` (PC-APP's file) already binds
`Stills.MediaProbe` fully-qualified, so it needs no edit — confirm by building. (3) `LibVlcTests`:
`Xunit.SkippableFact`; `Skip.If(!LibVlcProbe.Available, "libvlc is not installed on this box; run pc/tests/run-video-linux.sh")`;
a `MemberData` that would be empty yields one row that skips with "no fixtures — run
pc/tests/make-video-fixtures.sh". (4) `VideoSurface`: `StartupClock.Mark("first_video_frame")` on
the first `FrameChanged` after a `Play` — once per process.
**Done when** `dotnet test pc/tests/RankMaster2.Pc.Tests --filter FullyQualifiedName~Video` is green
on this box with the LibVLC rows skipped (visibly), and `pc/tests/run-video-linux.sh` is green in the
container with the fixtures built. **Only on the owner's machine:** A26's native memory over ten
minutes of `rm2vidprobe --loop` — § 6.

### 3.9 S-SESSIONS — the registry, the ranking session, the host (W2)

**Owns** `src/RankMaster2.Server/Sessions/**`; `src/RankMaster2.Server/Rm2Host.cs`;
`src/RankMaster2.Server/Program.cs`; `src/RankMaster2.Server/Security/SessionStatus.cs`;
`src/RankMaster2.Ranking/RankingSession.cs`; `src/RankMaster2.Actions/LibraryActions.cs`;
`src/RankMaster2.Catalog/RenameEngine.cs` (shim removal only); `RankMaster2.sln`,
`RankMaster2.Server.slnf`; new `tests/RankMaster2.LockHolder/`;
`tests/RankMaster2.Server.Tests/{Audit_RegistryWedgeTests,Audit_ContractDrift,SessionLifecycleTests,CancelTests,FullCycleTests,PingContractTests,PairTokenTests,OpenApiContractTests,ErrorCodeTableTests,RenameTests}.cs`
and its `.csproj` (project reference); `tests/RankMaster2.Server.Tests/Harness/**` if needed;
`tests/RankMaster2.Ranking.Tests/RankingSessionTests.cs`; `tests/RankMaster2.Audit.StateMachine/**`.
May also move two media-related cases into `tests/RankMaster2.Server.Tests/Media/` (S-MEDIA is
finished).

**Closes** H2, H3, H4 (registry half), H5, H11 (the `PathGuard` call), H14, A1, A3, A4, A7, A8, A9,
C6, C9, C13, C14 (use the constant), C15, C16, C20 (the `null!`), C24 (`CurrentForMedia`'s dead
catch), K7 (lock), K14, T1a, T1b; and finishes H1 on the wire (`total` = N).

**Do**

1. `RankingSession.Resync()`: `Start()` minus `_cues.Clear()` and `SessionVotes = 0`; it does clear
   `_recent` and `_undo` and re-picks. `RestoreSnapshot` swaps the records list reference (a new
   list, assigned once) instead of `Clear()+AddRange()`, and `Records` returns the current reference
   (A8). Tests in `RankingSessionTests`. (The `saveOnChoice` flag is S-DURABILITY's, W3 — do not
   add it here.)
2. `StartRenameAsync`: `BuildPlan(open.Folder, records)` (the W1 overload); the journal write in a
   `try`; only on success set `RenameInProgress` and publish `_rename`; on failure answer
   `500 rename_failed { reunited:true, journal:null }` with the session untouched (H3).
   `RunRenameAsync`: `MoveAll` only; `total` = N; delete the two `[Obsolete]` shims from
   `RenameEngine.cs`; a `OperationCanceledException` from a cancelled retry is the cancel path, not
   a failure. Both failure branches and the `renaming`-phase cancel call one
   `ResyncAfterRename(open)` under the gate: `Resync()`, `pairSeq++`, undo cleared,
   `LastAction = null`; if `Resync()` throws, close the session (release the lock) and finish the
   run `failed` with `reunited` truthful (H2, A1). `FinishAsync` waits for the gate **without** a
   timeout (the 5 s limit is for HTTP callers).
3. `OpenAsync`: wrap recovery in `try/catch (Exception)`: dispose the lock, answer
   `rename_failed { reunited:false, journal }` (H4). Delete a stale `rankmaster_db.json.tmp` in the
   folder on open (A9). `ResolveFolder` runs `Security.PathGuard.Check` on the resolved path with
   the same refusals as `/libraries/browse` (H11). `CloseAsync`: `409 rename_in_progress` while
   `RenameInProgress` (H14).
4. Undo (H5, A7): one `open.UndoPoint` (`None | EngineSnapshot | LastMove`), set by vote/skip/
   discard/special, cleared by `drop_missing`, rename success, resync, undo, and open;
   `undoAvailable = open.UndoPoint != None`; `UndoAsync` consults it and nothing else. `Materialise`
   drops the OR. The `drop_missing` path calls `open.Actions.ClearLastMove()` too.
5. `Rm2Host`: `builder.Services.AddSingleton<Security.ISessionStatusProvider>(_ => SessionRegistry.Shared)`
   with `SessionRegistry : ISessionStatusProvider` returning `(open, sessionId, folder, state)`
   without taking the gate; delete `ReflectiveSessionStatusProvider` from `SessionStatus.cs` (A4,
   C15). Register `ApplicationStopping → SessionRegistry.Shared.Dispose()`, and `Dispose` closes the
   open session so the lock file is deleted (A3).
6. Delete `FolderLock.ReadHolder` and the `details.holder` object (C6). Delete
   `SessionRegistry.ReleaseMedia` and `NoOpMediaPipeline`'s claim that the media layer hooks in
   (C13). Delete `Sessions/MediaFingerprint.cs`; use `Media.MediaFingerprint` (C16).
   `OpenSession.Actions` via the constructor (C20). `CurrentForMedia`: no `catch` (C24, with A8).
   `SessionBody.MaxBytes` → `Security.Limits.MaxJsonBodyBytes` (C14). `RenameEndpoints`: read the
   body through `SessionBody` so malformed JSON is `400 invalid_json` (C9). `FolderLock`: on Windows
   set `Hidden` after creation (K7). `LibraryActions.UndoLastMove`: no `Directory.CreateDirectory`
   on the media folder — if it is gone, throw (K14).
7. Tests. `Audit_RegistryWedgeTests` → both pass, renamed (`A_journal_write_that_throws_answers_rename_failed_and_keeps_the_session_usable`,
   `A_corrupt_journal_on_open_answers_rename_failed_and_releases_the_lock`).
   `Audit_ContractDrift`: `…undo_after_drop_missing…` and `…after_a_failed_commit_the_session_is_resynced…`
   pass, renamed; `…media_errors_carry_error_session…` → flipped to assert `null`;
   `…rename_start_with_malformed_json_body` → asserts `400`; `…wrong_method_on_a_real_route` → asserts
   `404 not_found`; `…envelope_null_fields…` → deleted (the harness already accepts both shapes and
   the contract now blesses it); `…416_headers`, `…head_on_a_cold_still…` → moved to
   `tests/RankMaster2.Server.Tests/Media/ContractFollowupTests.cs`, kept if they pass, deleted if the
   contract does not support them; `…rename_in_exhausted_state_is_accepted` → kept, renamed. New:
   `SessionLifecycleTests.Session_open_refuses_what_browse_refuses` (`..`, UNC, device name → the
   same 400s), `Close_during_a_rename_is_refused`, `A_cancelled_rename_keeps_the_session_counters`,
   `A_failed_rename_resyncs_like_a_cancel`, `A_stale_tmp_is_removed_on_open`, `PingContractTests`
   per § 2 T1a, the cross-process lock test per § 2 T1b (`LockHolder` project added to
   `.sln`/`.slnf`, referenced by `Server.Tests.csproj`, started with `dotnet <dll>`).
   `RenameAuditTests`: `RenameFailureReunitesAndKeepsRatings` also asserts `pairSeq` +1 and
   `sessionVotes` kept. `RenameTests`: any `total`/`done` assertion → N.

**Done when** the server suite, the three audit suites and `rm2ctl cycle` are green;
`grep -rn "ReflectiveSessionStatusProvider\|ReadHolder\|MovePhase" src` finds nothing; the audit's
H2/H3/H4/H5 tests pass under their new names.

**Proven here by** all of it, including the OS-level lock. **Only on the owner's machine:** the lock
file hidden and gone after a clean exit (A3, K7) — § 6.

### 3.10 P-RANK — the phone's ranking screen (W1)

**Owns** `android/app/src/main/kotlin/com/rankmaster2/phone/ui/rank/**`;
`android/app/src/test/kotlin/com/rankmaster2/phone/rank/**`.

**Closes** H6, A10, A11, A15, A16, A17, C21 (the silent-by-code rule), K5 (phone), K11, T2a, T2b,
T2c (the two rank guards), T2e.

**Do**

1. `RankViewModel.finish()`: silent iff `code ∈ { STALE_PAIR_TOKEN, NO_CURRENT_PAIR }`; every other
   `Refused` shows `problemFor(result)` **and** adopts `result.session` when present (H6). The
   `problemFor` branches for `save_failed` (its text now also fits the § 1.3 latch: "the last
   choice did not count; nothing before it is lost"), `move_failed`, `rename_in_progress` ("The PC is
   renaming this folder; wait for it to finish"), `nothing_to_undo` are now reachable and tested.
2. `session_busy`: wait `details.retryAfterSeconds` (default 1), resend the same frozen request once,
   then show the modal (A15). A lost cancel: resend once; if still lost, "The cancel may or may not
   have landed — the screen shows the current pair" and a refresh (A16).
3. `refresh()`: the result is discarded if `busy` became true while it was in flight or if its
   `pairSeq` is older than the held one (A10). `resume()`: every branch leaves `busy = false` (A11).
4. `RankScreen` exhausted copy: "Fewer than two files left to compare" with the undo action first
   and "Pick another folder" second (A17). `RankState.kt:74-75`: the `special` notice names the file
   like `discard` does; `Refused` text never shows "(code)" — the `else` branch says "The PC refused
   that" with the server's message only (K5). `FullScreenViewer`: one page notion (K11).
5. Tests: `AuditRankViewModelTest` → six flipped, renamed; `RankViewModelTest` fixtures carry the
   snapshot; `FakeRankClient` throws on an unqueued call; `PaneMenuDesignTest`/`CancelNotchTest`
   guard tests renamed; `:236` per § 2 T2e.

**Done when** the phone suite is green (333 + the new ones) and no test name contains `AUDIT`.
**Proven here by** the suite. **Only on the owner's phone:** the wording, and one deliberate
`save_failed` (unplug the USB drive, vote twice) showing a message — § 6.

### 3.11 P-BROWSE — folders, pairing, the app shell (W1)

**Owns** `android/…/phone/ui/browse/**`, `android/…/phone/ui/pairing/**`, `android/…/phone/Rm2App.kt`,
`android/…/phone/MainActivity.kt`, `android/…/phone/CrashLog.kt`, `android/…/phone/store/**`;
tests `android/app/src/test/kotlin/com/rankmaster2/phone/{browse,pairing}/**`.

**Closes** H7, A14, A18, A19, the phone half of A29, the phone half of H14, C25 (stale comments in
`PairingFlow.kt:24`, `Credentials.clear()`), T2d.

**Do**

1. `BrowseViewModel.attemptOpen`: on `session_already_open` do **not** close; surface
   `OpenFailure.AlreadyOpen` with the existing "Close it and open this" button; only that button
   calls `DELETE /session` and retries (H7). A `409 rename_in_progress` from the close →
   "The PC is renaming that folder; try again when it has finished" (H14). `BrowseOpenTest:153,175,266`
   flipped.
2. `CrashLog.install`: install once per process (a static flag) (A14).
3. "Forget this PC" (`Rm2App.kt:114`): call `client.revoke(deviceId)` best-effort (ignore the
   result), then `credentials.clear()` (A18).
4. `PairingFlow.refusal`: `attemptsRemaining` from `refused.details["attemptsRemaining"]`; delete the
   regex; `PairingFlowTest:160` per § 2 T2d (A19). When the scanned payload's host is a loopback
   address, stop before the ping: "This code points at the PC itself (127.0.0.1). The server is not
   listening on the network — see SERVER_RUNNING.md on the PC." (A29).
5. Fix the two stale comments (C25).

**Done when** the phone suite is green. **Proven here by** the suite. **Only on the owner's phone:**
the ask-first dialog appearing when the PC has a folder open — § 6.

### 3.12 P-NETMEDIA — the phone's network and media layers (W1)

**Owns** `android/…/phone/net/**`, `android/…/phone/media/**`; tests
`android/app/src/test/kotlin/com/rankmaster2/phone/{net,media}/**`.

**Closes** A12, K10, C25 (dead members and stale comments in `net/` and `media/`), T2c
(`LoadControlBudgetTest`).

**Do**

1. A12: `Rm2VideoPlayers` keeps one slot per pane side; `acquire(side, url)` **releases** the side's
   previous player before the new one is built and prepared; `MediaPane` keys its `remember` on
   `(ref.id, url)` only and applies `playing` through `playWhenReady` on the existing player in a
   `LaunchedEffect`, never by rebuilding. The comment "releases the old one in the same breath"
   becomes true. Test in `Rm2VideoPlayersTest`: acquiring twice for `left` records `release(old)`
   before `prepare(new)`.
2. Delete `Rm2Result.onOk`, `ErrorEnvelope`/`ErrorBody`, `StillFormat.JPEG`/`NEGOTIATE`,
   `MediaWidths.THUMB`, `Rm2Video.stop()`, `Rm2Media.shutdown()`, `MediaPane.onState` and the
   `MediaPaneState` reporting path (C25). **Do not delete any `ErrorCodes` member** (§ 1.2).
   Fix the stale comments: `Rm2VideoPlayers.kt:112-113` (32 MB), `Rm2ImageLoader.kt:52-55` (512 MB).
3. `PairingPayloads.decode` (if it lives in `net/`; otherwise P-BROWSE's) percent-decodes as UTF-8 (K10).
4. `LoadControlBudgetTest` renamed as a design guard (T2c).

**Done when** the phone suite is green. **Only on the owner's phone:** twenty video pairs in a row
without the app dying (A12) — § 6.

### 3.13 PC-RENAME — the PC client offers rename, with a bar and a cancel (W2)

**Owns** `pc/src/RankMaster2.Pc/Link/**`, `pc/src/RankMaster2.Pc/Ui/**` and their tests (both W1
packages are finished); `README.md`'s rename line if it needs correcting.

**Closes** A23; supersedes `PC_CLIENT_PLAN.md` § 6.6 in code; gives the owner's cancel a place to
exist.

**Do**

1. `ISessionLink` gains `StartRenameAsync`, `GetRenameAsync`, `CancelRenameAsync` over the three
   routes of § 10.16 (no token; `202` is not completion; the link polls `GET /session/rename` every
   250 ms to a terminal state and raises progress). Link tests against the real server (the
   `Link.Tests` harness), including cancel while the test's blocking `ICatalog` holds the run.
2. The start screen gets the rename slot plan E reserved (`E-ranking-surface.md` § E6): one
   confirmation in the owner's words — "Rename every file in *folder* by rank? Names become
   `000001-xxxx.jpg …`. Nothing is copied and no rating is lost. Cancel stops where it is and undoes
   nothing already done." — then a `RenameView` with `done / total`, the phase, and **Esc = Cancel**
   from the first instant (Esc on this screen cancels the rename, not the program; the help sheet
   says so); on `succeeded`, `cancelled` or `failed` one line says which and the start screen
   returns; `failed` with `reunited:false` shows the journal path and "open the folder again to
   retry". Coordinator tests with a fake link for every terminus.
3. If README's rename line does not match, fix it.

**Done when** both PC suites are green and the start screen offers Rename with a working cancel
against the real server. **Only on the owner's machine:** the whole feature on screen, and the one
thing this plan exists to let him do — a rename on his USB drive that he can stop — § 6.

### 3.14 S-DURABILITY — the bounded write-behind (W3, alone)

**Owns** `src/RankMaster2.Ranking/RankingSession.cs`; `src/RankMaster2.Server/Sessions/**`;
`src/RankMaster2.Server/Security/Rm2SecurityOptions.cs` (the two options, if S-SECURITY left them
there); `tests/RankMaster2.Ranking.Tests/RankingSessionTests.cs`; `tests/RankMaster2.Server.Tests/**`;
`tests/RankMaster2.Audit.StateMachine/**`; `src/rm2ctl/Cycle.cs`;
`pc/tests/RankMaster2.Pc.Link.Tests/**` (the `POST /session/save` rule only). Everything else is
finished by W3, so nothing runs beside it.

**Closes** A2 (the owner's relaxation); implements § 1.3.

**Do**

1. `RankingSession(…, bool saveOnChoice = true)`; with `false`, `ApplyVote`, `Skip` and
   `UndoLastAction` of a vote/skip do not call `Save()`. Engine tests: the default is unchanged
   (every existing test passes untouched); with `false`, a jammed catalog does not roll a vote back.
2. `SessionRegistry`: `_unsavedChoices`, `_dirtySince`, `_saveFailed`; a flush task
   (`PeriodicTimer`, `SaveDelaySeconds`) that takes the gate without timeout and saves when dirty;
   the `MaxUnsavedChoices`-th choice saves synchronously inside the request; forced saves before
   `MoveAsync`, `UndoAsync` of a move, `StartRenameAsync`, in `SaveAsync`, `CloseAsync`, and
   `Dispose`; the latch behaviour of § 1.3 (mutating call while latched → synchronous save first →
   `save_failed` with nothing applied on failure). `SaveDelaySeconds = 0` → `saveOnChoice = true`
   and no timer.
3. Tests, all against a real server: `A_vote_is_on_disk_within_the_bound` (fake clock or a 200 ms
   setting), `Five_choices_force_a_write`, `A_move_forces_a_write_before_the_response`,
   `Close_and_shutdown_flush`, `A_failed_flush_latches_and_the_next_choice_reports_save_failed_with_nothing_applied`,
   `Save_clears_the_latch`, `A_lost_response_then_a_flush_then_a_resend_applies_exactly_one_vote`,
   `SaveDelaySeconds_zero_restores_save_on_every_choice`; `RollbackAsymmetryTests` re-derived for
   the two modes (the table has a "mode" column now); `RetryAndDoubleVoteTests` unchanged and
   green. Every test that reads `rankmaster_db.json` after a vote calls `POST /session/save` first
   (the PC link tests, `FullCycleTests`, the state-machine audit, `rm2ctl cycle` — add
   `SaveAsync` before its on-disk checks).
4. The benchmark of § 1.3; its table in the completion note.

**Done when** everything in § 5 is green with the defaults **and** with `SaveDelaySeconds = 0`
(run the server suite twice, once per mode, via the options); the benchmark table is in the
completion note.

**Proven here by** the two runs and the benchmark. **Only on the owner's machine:** the USB fsync
cost — not measurable here; the owner will feel it or not.

---

## 4. The order

| Wave | Packages | Why here |
|---|---|---|
| **W0** | D0 | Spec first; every code package builds against text that already says what it must do. Alone, because eleven agents editing `SERVER_SPEC.md` is the one thing this project has never allowed |
| **W1** (parallel, eleven agents) | S-RENAME, S-MEDIA, S-SECURITY, S-TRAY, PC-LINK, PC-UI, PC-APP, PC-STILLS, PC-VIDEO, P-RANK, P-BROWSE, P-NETMEDIA | No two share a file (§ 3 boundaries). **S-RENAME is the priority**: H1 is the one finding that corrupts judgement silently and permanently, and it is half-built already |
| **W2** (parallel, two agents) | S-SESSIONS, PC-RENAME | S-SESSIONS edits `SessionRegistry.cs`, which needs S-RENAME's engine (the shims come out), S-SECURITY's constant and fallback, and S-MEDIA's finished test folder. PC-RENAME changes the `ISessionLink` seam, so it runs after both packages that own a side of it. The two share no tree |
| **W3** (alone) | S-DURABILITY | Changes a promise, touches the registry, the engine, and every test that reads the disk after a vote; nothing may run beside it |

**Before the owner installs anything** (his PC has never run the client): all four waves complete
and the gate of § 5 passed. Everything in "would hurt" is closed by W2; the four Windows-client
findings that would end his first session (H8, H9, H10, A20) are in W1; his cancel exists after W2;
his durability relaxation after W3.

**If time is short**, the packages whose findings are all "would annoy / confuse" can ship in a
second round: S-TRAY, PC-APP, P-NETMEDIA. S-MEDIA and PC-STILLS/PC-VIDEO's *test* halves are not
optional: they are the evidence the next audit will read.

---

## 5. Acceptance gate

The remediation is complete when, on this box, in this order:

1. `tests/corpus/build-corpus.sh` and `pc/tests/make-video-fixtures.sh` have been run (both tools
   are present here).
2. `dotnet test RankMaster2.Server.slnf` → **0 failed, 0 skipped**, twice: with the default
   `SaveDelaySeconds` and with `0`. Every test the audit left (Appendix A of `AUDIT.md`) either
   passes under a name that says what it proves, or is listed in § 3 as deleted with the contract
   change that made it wrong. No test name contains `Audit_` or `AUDIT`.
3. `dotnet test pc/RankMaster2.Pc.sln` → **0 failed**; the only skips are `Category=LibVlc` rows
   (libvlc is not installed on the host), each printing why. `pc/tests/run-video-linux.sh` → green
   in the container.
4. `cd android && ./gradlew :app:testDebugUnitTest` → **0 failed**, no skips.
5. `rm2ctl cycle` against a real TLS server, as the audit ran it → every check green, including the
   rename step with names matching `^\d{6}-[0-9a-f]{4}\.` and the on-disk checks after a
   `POST /session/save`.
6. Five greps find nothing:
   `grep -rn "TempNameOf\|MovePhase\|__rm2_\|RandomSuffix(8)" src/RankMaster2.Catalog src/RankMaster2.Server`;
   `grep -rn "ReflectiveSessionStatusProvider\|ReadHolder" src`;
   `grep -rn '"Rank Master 2"' pc/src`;
   `grep -rn "Skip" pc/src/RankMaster2.Pc/Ui`;
   `grep -rn "return;" pc/tests --include=*Corpus*.cs --include=LibVlcTests.cs` (every one is now `Skip.If`).
7. With `tests/corpus/media` moved aside, step 2 reports the corpus tests as **skipped** (then move
   it back).
8. `SERVER_SPEC.md` § 10.16 describes, and `RenameEngineTests` + `RenameSecondRunTests` prove, the
   § 1.1 scheme, including a second rename of an already-renamed folder interrupted at every step
   with every rating still on its file — and the compatibility audit proves a folder full of
   `000001.jpg` from Rank Master 2 renames cleanly and still loads with a v1 schema.
9. The S-DURABILITY benchmark table is in its completion note.
10. Windows publishes build: `pc/publish.sh --no-upload`, and `dotnet publish` of
    `src/RankMaster2.Tray` and `src/RankMaster2.Server` for `win-x64` as `publish-tray.ps1` /
    `publish-server.ps1` do it.

Then the owner's checklist (§ 6) is the last gate, and it is his.

---

## 6. The owner — what he decided, what he will notice, what only his machines can show

Nothing here needs him to read code.

**Decisions already his, carried out here, not reopened**

- Rename names get a short tag per run (`000001-7f3a.jpg`); sorting by name is rank order; a folder
  already full of `000001.jpg` from Rank Master 2 needs nothing done to it.
- The database file does not change; Rank Master 2 keeps working on the same folders.
- The rename bar and Cancel stay, for a slow drive; Escape on the rename screen is Cancel.
- The phone does not get rename. Skip is gone from the Windows client (`↓` and `S` do nothing).
- Votes are saved within two seconds (or every five presses), not on every press. If the PC dies at
  the wrong moment, at most the last few presses are lost. `SaveDelaySeconds: 0` in the server's
  `appsettings.json` brings back saving on every press.

**Decisions this plan made in his name — say so if any is wrong**

1. **The phone asks before taking a folder away from the PC.** Today it just takes it. (H7)
2. **The phone now shows errors it used to hide** — a drive unplugged mid-vote says "The PC could
   not write the ranking file" instead of silently showing the same pair again. (H6)
3. **A stranger on the Wi-Fi can no longer cancel his pairing window** by guessing wrong five times;
   each device gets its own five tries. (H12)
4. **Escape quits at once even while a video is loading.** (A21)

**What only his PC can show** (in the order the kit asks him to do things)

1. First launch with the tray not running: the start screen appears, "Resume" pressed *immediately*
   either waits and opens or says why — it never sits greyed out with no message. (H9)
2. On his monitor the pictures are sharp — not softer than the same file in Explorer's preview. (H10)
3. A toast (for example after `Ctrl+S`) disappears on its own after a few seconds. (A20)
4. Ranking a **small** folder (fewer than 32 files) for 200 votes: pictures keep appearing. (H8)
5. `Esc` while a video is still loading: the window closes instantly. `Esc` **inside** the
   folder-picker dialog cancels the dialog, not the program. `↓` and `S` do nothing. (A21, A27)
6. **Rename on the USB drive**: from the start screen, Rename → the bar moves → press Esc part-way →
   it stops, says "cancelled", and the folder is a mix of old and new names with every rating still
   on its picture (open it and vote once to see the ratings are there). Rename again → it finishes;
   names are `000001-xxxx.jpg …` in rank order. Then open that folder in **Rank Master 2**: it loads
   with the ratings. (§ 1.1, the schema constraint)
7. The kit's report block lists `link_connecting`, `link_ready`, `tray_started`, `panes_painted` and,
   for a video folder, `first_video_frame`. (A22)
8. The tray's status line says "this PC only" while on `127.0.0.1`; after setting the LAN address in
   `appsettings.json` it shows that address — and the PC client still pairs with the server whose
   data folder is set in `appsettings.json`. (A29, A28)
9. After closing the client, the photo folder shows no `.rankmaster.lock`, and while ranking, that
   file and `.rankmaster-rename.json` (during a rename) are hidden in Explorer. (A3, K7)
10. If it ever crashes, the file is in `%LOCALAPPDATA%\RankMaster2\pc\`, not next to the exe. (A25)
11. `rm2vidprobe --loop` on a video folder for ten minutes: the "Memory (private bytes)" column in
    Task Manager stops climbing within the first minute. (A26)
12. After a few restarts, `%APPDATA%\Microsoft\Crypto\Keys` has not gained a new file per restart. (K12)
13. Whether voting feels quicker on the USB drive than before. (A2 — the only measurement of the
    fsync cost there is)

**What only his phone can show**

1. Open a folder the PC already has open: the phone asks "Close it and open this?" (H7)
2. Unplug the drive, tap a picture twice: a message on the second tap, not a frozen pair. (H6, § 1.3)
3. Twenty video pairs in a row: the app does not die. (A12)
4. Enter a wrong pairing code: "4 attempts left". (A19)
5. "Forget this PC", then look at the tray's device list: the phone is gone from it. (A18)
6. Scan a QR from a server still on `127.0.0.1`: the phone says the code points at the PC itself,
   before it tries the Wi-Fi. (A29)

---

## 7. Delivery — through GitHub, to the agent on his PC

The coordinator, after § 5 passes (this plan runs no `git`):

1. Commit the round on `master` in the repository's prose style; tag **`v3.1.0`** (the root
   `Directory.Build.props` says 3.1.0 after D0). If the round ships in two parts (§ 4 "if time is
   short"), the first part is `v3.1.0` and the second `v3.1.1`.
2. Push. The corpus and the fixtures are git-ignored and are **not** needed on his PC: nothing there
   runs tests.

The deploying agent, from the tag, on the owner's PC — everything below is already scripted:

1. **Server and tray:** `powershell -File publish-tray.ps1` from the repository root (publishes
   `RankMaster2.Tray.exe` and `RankMaster2.Server.exe` to `..\tray`). Stop the running tray first;
   start the new one. `SERVER_RUNNING.md` § ListenAddress if phones must reach it. The two new
   `appsettings.json` keys are optional; defaults apply.
2. **PC client:** the owner's one line, `irm https://bormin.fintebtc.de/rm2/install.ps1 | iex`, after
   the build box has run `pc/publish.sh` (which uploads the zip `install.ps1` fetches). If the
   deploying agent prefers to build on the PC: `dotnet publish pc/src/RankMaster2.Pc -c Release -r win-x64`
   and run `pc/install.ps1 -NoMeasure` against that output as the script's header describes. Then
   the kit: `pc/kit/Measure.cmd` — its report block is § 6 item 7.
3. **Phone:** `cd android && ./gradlew :app:assembleRelease` with the signing keystore that agent
   already holds (git-ignored, `*.jks`); install the APK over the existing one — the application id
   is unchanged, so the pairing survives. If the keystore is not to hand, `assembleDebug` and a fresh
   pairing.
4. Walk the owner through § 6 in that order, and report back which items he confirmed, in his words.
   Anything he reports that is not on the list is the next audit's first finding.

---

## Appendix A — every finding, its package, its fate

| Finding | Package | Fate |
|---|---|---|
| H1 | S-RENAME (+ S-SESSIONS for `total`) | fixed by § 1.1, from the in-flight state |
| H2, H3, H4, H5, H14 | S-SESSIONS (H4's engine half: S-RENAME) | fixed |
| H6 | P-RANK | fixed |
| H7 | P-BROWSE | fixed |
| H8, H9, H10 | PC-UI (H9's link half: PC-LINK) | fixed |
| H11 | S-SESSIONS (`PathGuard` call); the rest **accepted** | product decision |
| H12 | S-SECURITY, S-TRAY | fixed |
| H13 | D0 | **accepted risk** (the owner); README stops claiming a single writer |
| A1, A3, A4, A7, A8, A9 | S-SESSIONS | fixed |
| A2 | S-DURABILITY (the owner's relaxation); S-RENAME keeps the benchmark | fixed by contract, bounded, switchable |
| A5 | S-SECURITY | `counts` omitted; no cap |
| A6 | S-MEDIA | fixed |
| A10, A11, A15, A16, A17 | P-RANK | fixed |
| A12 | P-NETMEDIA | fixed in code; confirmed on the phone |
| A13 | — | **accepted** |
| A14, A18, A19 | P-BROWSE | fixed |
| A20, A21 (surface), A22 (`panes_painted`), A24, A27 | PC-UI | fixed / guarded |
| A21 (close log), A22 (link marks, kit), A25 | PC-APP | fixed |
| A23 | PC-RENAME | built, W2 |
| A26 | PC-VIDEO | fixed; confirmed on the PC |
| A28, A33 | PC-LINK | fixed |
| A29 | D0, S-TRAY, P-BROWSE | fixed in words on all three |
| A30, A34 | S-TRAY | fixed |
| A31 | S-RENAME (rm2ctl) | fixed |
| A32 | S-SECURITY | fixed |
| C1, C2, C3, C5, C8, C10, C11, C12 (text), C27 | D0 | contract/doc text |
| C4 | S-SESSIONS (= A4) | fixed |
| C6, C7, C9, C13, C16, C20 | S-SESSIONS | fixed (C20's prefetch duplicate accepted) |
| C12 (code) | S-SECURITY, S-MEDIA, S-RENAME (rm2ctl), PC-LINK | one spelling, four copies |
| C14 | S-SECURITY (constant), S-SESSIONS (use) | fixed |
| C15 | S-SESSIONS | fixed |
| C17, C18, C19 | — | **accepted** (§ 1.2) |
| C21 | P-RANK | the silent-by-code rule |
| C22 | PC-VIDEO | deleted |
| C23, C26 (Ui) | PC-UI | fixed |
| C24 | S-SECURITY, S-SESSIONS, S-RENAME (rm2ctl) | deleted within each boundary |
| C25 | P-NETMEDIA, P-BROWSE | deleted / corrected; `ErrorCodes` kept |
| C26 (App) | PC-APP | fixed |
| T1 | S-SESSIONS (a, b), S-MEDIA (c), S-RENAME (d), D0 (e) | made honest |
| T2 | P-RANK (a, b, c, e), P-BROWSE (d), P-NETMEDIA (c) | made honest |
| T3 | PC-STILLS, PC-VIDEO, PC-APP, PC-LINK | made honest |
| T4 | § 6 | the owner's checklist |
| K1, K11 | PC-UI, P-RANK | fixed |
| K2 | D0, PC-APP | fixed |
| K3 | S-RENAME (rm2ctl) | fixed |
| K4, K13 | S-SECURITY | fixed |
| K5 | S-TRAY, P-RANK | fixed |
| K6 | D0 | recorded as deliberate |
| K7 | S-RENAME (journal), S-SESSIONS (lock) | fixed; confirmed on the PC |
| K8 | D0 | documented; never enforced (schema constraint) |
| K9 | S-MEDIA | fixed |
| K10 | P-NETMEDIA | fixed |
| K12 | § 6 | the owner's check |
| K14 | S-SESSIONS | fixed |
| Skip on the PC (owner) | D0 (spec), PC-UI (surface) | removed |
| Durability (owner) | D0 (contract), S-DURABILITY (code) | relaxed, bounded, switchable |

## Appendix B — the auditor's tests, who owns each, what becomes of it

| File (as found in the tree at 10:27) | Owner | Becomes |
|---|---|---|
| `tests/RankMaster2.Catalog.Tests/RenameSecondRunTests.cs` (already renamed from `Audit_RenameSecondRunTests.cs` by the in-flight work; six tests) | S-RENAME | kept, phase-1 wording dropped, 6/6 pass — **H1's acceptance** |
| `tests/RankMaster2.Catalog.Tests/Audit_SaveCostTests.cs` | S-RENAME | `SaveCostBenchmark.cs`, prints, never fails |
| `tests/RankMaster2.Server.Tests/Audit_RegistryWedgeTests.cs` | S-SESSIONS | both pass, renamed — **H3, H4's acceptance** |
| `tests/RankMaster2.Server.Tests/Audit_ContractDrift.cs` (holds the media fork's H5 and H2 cases the audit lists under filenames that are not in the tree) | S-SESSIONS | resolved case by case per § 3.9 item 7 — **H2, H5's acceptance** |
| `tests/RankMaster2.Audit.Security/Audit_PairingAndGate.cs`, `Audit_RateLimitProbe.cs` | S-SECURITY | flipped / deleted / kept per § 3.3 item 8 — **H12's acceptance** |
| `android/…/rank/AuditRankViewModelTest.kt` | P-RANK | six flipped — **H6, A10, A11's acceptance** |
| `pc/tests/RankMaster2.Pc.Tests/AuditLeaseLeakTests.cs` | PC-UI | flipped — **H8's acceptance** |
| `pc/tests/RankMaster2.Pc.Tests/AuditOpenWhileBusyTests.cs` | PC-UI | flipped — **H9's surface acceptance** |
| `pc/tests/RankMaster2.Pc.Link.Tests/AuditBusyGateTests.cs` | PC-LINK | flipped — **H9's link acceptance** |
| `pc/tests/RankMaster2.Pc.Tests/Video/AuditLibVlcSharpMediaGetterTests.cs` | PC-VIDEO | kept as the proof of the library fact, renamed; A26's code fix sits beside it |

The audit also noted a process listening on `127.0.0.1:18777` that it did not start. It is not this
plan's; leave it alone as the audit did.
