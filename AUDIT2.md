# Rank Master 3 — second independent audit

Date: 2026-09-17. Tree audited: this worktree. No production file was changed; no `git` command was
run by me. Throwaway tests were written to prove findings, run, and then moved out of the repository
(see the Appendix), so the tree is left exactly as I found it.

Every item is labelled:

- **[proven]** — a test I wrote failed or passed exactly as the item says, or a real server answered as quoted.
- **[read]** — from reading the code; file and line cited; not executed.
- **[guess]** — an inference, or a Windows-only / phone-only claim that nothing here can run. Each says so plainly.

---

## How much of the system I actually reached

**Reached and executed:**

- **The whole server, as a real process.** I built it, started it under TLS, paired `rm2ctl` against
  it, and ran `rm2ctl cycle` end to end on a 39-file library of real corpus media: **112/112 contract
  checks passed**, including a real rename of 38 files. I then drove 40 votes over HTTPS with `curl`
  against a 60-file library and killed the process with `kill -9`: **all 40 votes were on disk.**
- `dotnet test RankMaster2.Server.slnf` → **679 passed, 0 failed, 0 skipped** (39 ranking + 55 catalog
  + 447 server + 10 compatibility + 29 state-machine + 99 security). Re-run clean at the end.
- `dotnet test pc/RankMaster2.Pc.sln` → **444 passed, 0 failed, 26 skipped** (I ran this myself; every
  skip is identified in § 5).
- `cd android && ./gradlew :app:testDebugUnitTest` → **354 tests, 0 failures, 0 skipped, 0 errors**
  (counted by me from the JUnit XML, not from the console summary).
- Both benchmarks, for real numbers rather than assertions.
- The ranking engine, catalog, rename engine, session registry, folder lock, tray host, publish
  scripts, `SPEC.md`, `SERVER_SPEC.md`, `README.md`, `SERVER_RUNNING.md`, `AUDIT.md` and
  `pc/plans/G-audit-remediation.md` — read by me directly. The PC client, the phone client, and the
  server's security and media layers were covered by three forks of me working in parallel; **I
  re-verified every headline finding of theirs against the code myself** before putting it here, and
  say so where I did not.

**Not reached:**

- **Nothing ran on Windows and nothing ran on a phone.** That is the largest gap and it is unchanged
  from the previous audit. The Windows client's real behaviour, the tray icon and its dialogs,
  WinForms shutdown on logoff, NTFS/SMB/USB file behaviour, ExoPlayer, Avalonia's rendering, and every
  hardware-decoder or GPU claim are code-reading only.
- **The frozen Rank Master 2 desktop app (`src/RankMaster2.App`)** — it does not build here and I did
  not try. Its compatibility with the database is argued from shared source, not observed.
- **The 111 KB `SERVER_SPEC.md` and the 107 KB `openapi.yaml`** were read in the parts that bear on
  findings, not cover to cover. `OpenApiContractTests` passes, so I did not re-derive the contract.
- **Real scale.** The largest libraries I ran were 20,000 one-byte files (the benchmark's), 240,000
  files for a browse test, and 60 real photographs. I did not rank a real 20,000-photograph folder.

---

## The short version

Seven things I would fix before he uses this on a folder he cares about, in order of what they cost him:

1. **The Windows client paints a pane from memory it has already freed, on the second pair.** It fires
   on any monitor bigger than 1920×1080 — so, almost certainly, on his. Either the app dies, or both
   panes keep showing the **previous** pair while his keypress is recorded against the new one. § 1.1.
2. **The server can serve one photograph's pixels under another photograph's name.** The cache key and
   the ETag do not include the folder. Proven end to end: a white photograph came back black. § 1.2.
3. **A tap on the phone is not tied to the pair it was aimed at.** Narrow window for a vote; a wide one
   — seconds — for the long-press Discard menu, which moves a photograph out of the folder. § 1.3.
4. **In a folder of large PNGs, one of every two pictures fails to load**, and the message tells him
   his file is broken when it is not. 12 out of 12 attempts. § 2.1.
5. **Rename can wedge the session for ever.** Cancel cannot clear it, every vote is refused, only
   restarting the server cures it. His ratings survive; his evening does not. § 2.2.
6. **There are five ways the server refuses to start, and all five print a stack trace.** The tray's
   friendlier dialog actively misleads for two of them. § 2.3.
7. **"Resume last folder" never appears, ever.** The only method that loads it is dead code — in the
   client that was rewritten because starting was too slow. § 3.1.

The ratings *file* itself is well defended: I could not make the server write an empty database, lose
a vote to a hard kill, apply one vote twice, or leave a rename unrecoverable. § 6 says what I tried.

---

## 1. Would lose or corrupt his work

### 1.1 The Windows client repaints a reused pane from a buffer it has already freed — [proven in its links; the end outcome is a guess]

**What happens to him.** He is ranking on a 1440p or 4K monitor. He votes. The next pair keeps one
photograph and brings in a new one — the ordinary case in pairwise ranking. At that moment the client
reads pixels out of memory it has already released. Either the app disappears and leaves a crash file,
or the repaint aborts half-done and **both panes keep showing the previous pair's photographs while the
counter and the server have moved on** — so his next keypress is recorded against pictures he cannot
see.

**The chain, three links:**

1. `pc/src/RankMaster2.Pc/Ui/Surface/RankCoordinator.cs:734-735` — when the next pair names the same
   id, the pane is reused with `old.WithGeneration(pairSeq)`, a `record with` that carries **the same
   `StillLease` object** forward. That lease was already disposed by `PaneControl.Render`
   (`pc/src/RankMaster2.Pc/Ui/Views/PaneControl.axaml.cs:79-80`). The repository's own test says so in
   as many words — `pc/tests/RankMaster2.Pc.Tests/AuditLeaseLeakTests.cs:87`:

   ```csharp
   Assert.Same(leaseA1, c.Rank.Left.Lease);   // still carries the already-disposed lease
   ```

2. `RankCoordinator.cs:709-712` — when *either* pane is new, `_stills.Show(folder, leftId, rightId)`
   runs for **both** ids, the reused one included. `StillSource.EnsureFreshLocked`
   (`pc/src/RankMaster2.Pc/Stills/StillSource.cs:186-197`) asks `DecodeGeometry.FrameCovers`; if the
   cached frame is smaller than the current pane it calls `_cache.ClearResult(id)`, the refcount hits
   zero, and the buffer is `FreeHGlobal`d. Proven with a real `StillSource` and a real 1000×1000 bitmap:

   ```
   decoded a.bmp at 300x300 (source 1000x1000), live bytes 720000
   reading Pixels through the reused pane's lease threw: ObjectDisposedException ; live bytes now 0
     at RankMaster2.Pc.Stills.BudgetedBuffer.get_Pointer()  DecodeBudget.cs:119
     at RankMaster2.Pc.Stills.StillFrame.get_Pixels()       StillFrame.cs:48
   ```

3. `PaneControl.axaml.cs:75-83` — `if (!ReferenceEquals(pane, _lastRenderedPane))` is **true** for the
   reused pane (the `with` produced a new instance), so it re-enters the copy branch and calls
   `CopyToBitmap(lease.Frame)`, whose line 149 is `var src = (byte*)frame.Pixels;`.

**Why the trigger is his monitor.** `StillSource` starts at `_paneW = 960, _paneH = 1080`
(`StillSource.cs:19-20`). The real pane size only arrives when `RankView.Loaded` fires
(`pc/src/RankMaster2.Pc/Ui/Views/RankView.axaml.cs:64`), and `UiRoot.Render`
(`pc/src/RankMaster2.Pc/Ui/Views/UiRoot.axaml.cs:73-78`) constructs `RankView` lazily — *after* the
open snapshot has already decoded the first pair at 960×1080. On a screen where half the window is
wider or taller than 960×1080, every later `Show` then finds the cached frame too small and frees it.
Exactly 1920×1080 is safe; 2560×1440, 3840×2160, and 1920×1080 at 150 % scaling are not. I traced this
chain myself in the source.

**Which of the two outcomes? [guess].** The repaint reaches `PaneControl.Render` through
`Dispatcher.UIThread.Post` (`UiRoot.axaml.cs:58-63`), so the exception is unhandled on the Avalonia
dispatcher and should reach `CrashLog`'s `AppDomain.UnhandledException` handler — a crash file and a
dead process. Nothing on this machine can say whether Avalonia 11.3.22 on Windows really terminates
there. If it swallows the exception instead, the outcome is the worse one: `_lastRenderedPane` was
already advanced, `_frame.Source` was never assigned, and `_right.Render` never runs at all because
left threw first — so the info card shows the new pair's numbers over the old pair's photographs, both
panes report `Ready`, and 200 ms later the action gate accepts a vote for the pair on the wire rather
than the pair on the glass.

**Why no test catches it.** Every coordinator test uses `FakeStillSource`, which never frees a frame;
and `RankCoordinatorTests.Unchanged_id_and_mediaVersion_is_reused_without_a_new_Show_call`
(`RankCoordinatorTests.cs:272`) reuses *both* panes, which is the one case where `Show` is not called
and nothing is freed.

### 1.2 The server can serve one photograph's pixels under another photograph's name — [proven]

**What happens to him.** He opens a folder; the phone shows him a picture from a *different* folder;
he votes on it. The vote is recorded against the file he never saw. This is the only finding that can
put wrong numbers into `rankmaster_db.json` without anything failing visibly.

`src/RankMaster2.Server/Media/MediaFingerprint.cs:29-45` computes

```
fingerprint = SHA-256( utf8(id) ‖ 0x00 ‖ decimal(sizeBytes) ‖ 0x00 ‖ decimal(mtimeUtcTicks) )
```

**The folder is not an input.** I read this myself. That fingerprint is both the ETag
(`MediaFingerprint.cs:62`) and the server's own disk-cache key
(`src/RankMaster2.Server/Media/MediaEndpoints.cs:276` — `EntityHex + "-" + variant + "-q" + quality`,
and nothing else). Two files in two folders sharing a name, a byte size and an mtime are the same
object to this server.

Proven end to end against a real Kestrel server over TLS. Two folders, each with a `holiday.jpg` of
exactly 9000 bytes and an identical mtime; folder A's near-black, folder B's near-white:

```
--- folderA ---   etag: "s360j-442a4fa845dd785d871dd8460ce32e83"   md5 0fb080d4…
--- folderB ---   etag: "s360j-442a4fa845dd785d871dd8460ce32e83"   md5 0fb080d4…
folderA served pixel (10, 10, 10)      folderA SOURCE pixel (10, 10, 10)
folderB served pixel (10, 10, 10)      folderB SOURCE pixel (245, 245, 245)
```

Byte-identical responses. Folder B's white photograph came back as folder A's black one, while `meta`
still reported folder B's real size, so nothing else looked wrong.

It bites twice: the server's own cache under `<data>/cache`, and — because `Cache-Control: private,
max-age=31536000, immutable` (`Media/MediaHttp.cs:211`) plus the spec's promise that "the same ETag
always means the same bytes… a client MAY cache indefinitely, keyed by ETag" (§ 12.5.1/12.5.3) — the
phone will keep the wrong picture for a **year** without revalidating.

**How likely in his life:** low but not zero. It needs same name + same byte length + same mtime with
*different* content. Exact duplicates (harmless) are the common case; the harmful case needs something
like a cloud-sync client or a restore that preserves name, size and mtime while changing bytes. I rank
it here anyway because it is the one failure that silently writes wrong ratings, and the fix is one
argument.

`SERVER_SPEC.md` § 12.5's "Known limit" documents only the *same-folder* replacement case, and § 12.2
defines the fingerprint without the folder — so the spec would need amending too, not just the code.

### 1.3 The phone records a vote for whichever pair is current when the tap is *processed*, not the one he looked at — [proven]

`android/app/src/main/kotlin/com/rankmaster2/phone/ui/rank/RankScreen.kt:238` sends only a `Side`.
`RankViewModel.act` (`.../ui/rank/RankViewModel.kt:204-209`) then reads
`current.snapshot?.pairToken` — the token as it stands when the tap is *handled*. Nothing compares
that with what was on the glass when his finger went down. The file header at `RankViewModel.kt:21-37`
argues carefully that the token cannot drift *within* one action; the step before that, picture →
finger, is unguarded.

The window is real because `refresh()` is deliberately **not** gated by `busy`
(`RankViewModel.kt:140-155`; the A10 comment says so), so the screen stays fully tappable for the whole
duration of a `GET /session` — and `RankRoute.kt:40` fires exactly that on every `ON_START`, i.e. every
time he comes back to the app:

```
PROBE1 saw=alpha.jpg votedToken=token-2 pairAtVote=charlie.jpg/delta.jpg
```

I verified both code sites myself. What this proves is that **no guard exists**; the probe forced the
interleaving rather than racing it, so for a plain vote the real-world frequency is low — he would
usually see the pictures change first.

**The same hole on Discard is much worse, and I verified that one end to end.**
`RankViewModel.finish` (`RankViewModel.kt:252-279`) never clears `paneMenu` — only `act` and `open` do
(`RankViewModel.kt:72, 133, 209`). `PaneMenu.kt:119` reads `state.left`/`state.right` live. So: he
long-presses a photograph, the menu opens, he reads the filename and thinks about it — and if a
`refresh()` lands in those seconds (coming back to the app, a resync), the menu is now describing a
different photograph and "Discard" moves *that* one into `discarded/`. Recoverable if he notices. He
has no reason to notice.

**Cost to him:** a judgement recorded on two pictures he never compared, or a photograph moved out of
the folder that he did not choose. One line of plumbing — send the pair token or `pairSeq` with the
gesture, drop the action if it no longer matches — closes both.

### 1.4 Moving photographs out of the folder deletes their ratings, silently and by design — [proven by reading; specified]

`src/RankMaster2.Catalog/JsonCatalog.cs:56-88`: `Save` lists the folder and writes a row **only for
files that are on disk right now**. Any row in the existing database whose file is not currently listed
is dropped. `SPEC.md:135` says exactly that ("drop only files that vanished") and
`tests/RankMaster2.Catalog.Tests/JsonCatalogTests.cs:43` pins it.

This is specified behaviour and I am not calling it a bug. I am flagging what it costs him, because he
is not a programmer and this is not obvious:

- He moves 300 ranked photographs into a subfolder to look at them. He votes once. **Those 300 ratings
  are gone**, permanently, and nothing says so. Moving them back gives him 300 unranked files.
- The same applies to any file that acquires the **Hidden or System** attribute —
  `ListTopLevelMedia` (`JsonCatalog.cs:170-175`) skips those, so a hidden photograph is "vanished" for
  this purpose. That part is **not** in the spec. **[read]**; **[guess]** as to how a photograph would
  acquire that attribute in his workflow.

### 1.5 `RankingSession` violates the thread-safety invariant its own comment states — [proven]

`src/RankMaster2.Ranking/RankingSession.cs:8-20` documents the rule the media layer depends on:

> Every wholesale replacement — a scan, a resync, a rollback — builds a complete new list and assigns
> it in one reference write, because the media layer reads this collection without the session gate.

`Drop` (`RankingSession.cs:206`, `_records.RemoveAt(i)`) and `Restore` (`RankingSession.cs:243`,
`_records.Add(record)`) mutate the live list in place. `SessionRegistry.BuildMediaView`
(`src/RankMaster2.Server/Sessions/SessionRegistry.cs:225`) calls `open.Session.Records.ToArray()` off
the session gate, and its own comment repeats the false claim: *"this copy can never see a half-built
list and there is nothing here to catch (A8)"*.

`Enumerable.ToArray` on a `List<T>` reads `Count`, allocates, then `CopyTo`. If the list shrinks
between those steps the tail stays `null`, and `MediaExtensions.RankPolicy(records.Select(r => r.Kind))`
dereferences it. My throwaway test — one thread building the media view exactly as `BuildMediaView`
does, another doing `Drop`/`Restore` — threw in **19 ms**:

```
READER THREW: NullReferenceException: Object reference not set to an instance of an object.
```

**Cost to him:** one picture fails to load, at the instant he discards something. The transport
middleware (`src/RankMaster2.Server/Security/SecurityMiddleware.cs:86-94`) catches it and returns a
proper `500 internal_error` envelope, so it is not a crash and not data loss. I rank it here only
because the comment asserting it is impossible is what will stop the next person looking. Honest about
the window: I needed a hammer loop to hit it; per discard the odds are tiny.

---

## 2. Would stop him working

### 2.1 In a folder of large PNGs, one of every two pictures fails to load — and he is told his file is broken — [proven]

**What happens to him.** A folder of big PNGs. Every time the phone shows him a pair, one of the two
images is replaced by **"The file is present but could not be decoded as an image."** The file is
fine. Reload, and the *other* one may fail instead.

`MediaOptions.cs:38` allows `MaxConcurrentDecodes = 2` while `MaxOptions.cs:52` gives every decode a
**single shared** `DecodeMemoryLimitMegabytes = 128` budget. I confirmed the shape in
`src/RankMaster2.Server/Media/StillRenderer.cs:59-60` — one `SemaphoreSlim(2)`, one `DecodeBudget`. Two
concurrent decodes of anything over 64 MB of pixels cannot both fit. Since the product's whole
interaction is *showing a pair*, the phone asks for both at once, so the two settings collide by
construction. Not a rare race — **12 out of 12 attempts**, two 5000×5000 PNGs at `w=2160`, cold cache
each time:

```
422|200    "media_decode_failed","The file is present but could not be decoded as an image."
422200|    …  (×12, one of the two always 422)
p1.png solo: 200        p2.png solo: 200
```

Separately, a **single** PNG over ~33 megapixels is always refused, because PNG has no sampled decode
and needs `w × h × 4`:

```
bigjpeg.jpg (8000×6000 JPEG)   200  21416B   ← fine, JPEG scales in the IDCT
bigpng.png  (6000×6000 PNG)    422  media_decode_failed   (a 120 KB file)
hugepng.png (9000×9000 PNG)    422  media_decode_failed
```

The ceiling is a deliberate, documented trade (`MediaOptions.cs:44-52` even says "caps a non-JPEG
still at about 32 megapixels"), but the *message he sees* says his file is corrupt, and because § 11.3
forbids a GET mutating session state, the pair picker keeps offering that file for ever — he has to
skip past it every time it comes up.

### 2.2 A rename that hits a file it cannot move wedges the session until the server is restarted — [proven]

This is the same class of fault as `AUDIT.md` H3/H4 ("a wedge only a restart cures"), which
`pc/plans/G-audit-remediation.md` § 1 calls closed. The two *specific* orderings it named are fixed;
the general one is not.

`SessionRegistry.RunRenameAsync` (`SessionRegistry.cs:1093-1181`) runs as
`_ = Task.Run(() => RunRenameAsync(open, run))` — fire and forget. Its `catch (Exception)` handler
calls `RenameEngine.RecoverIfPresent`, and **that call can itself throw**:
`RenameEngine.FinalizeBestEffort` (`src/RankMaster2.Catalog/RenameEngine.cs:404-428`) catches only
`IOException`, and so does `FileOps.MoveWithRetry` (`src/RankMaster2.Catalog/FileOps.cs:39-58`). An
`UnauthorizedAccessException` — access denied on a file or on the folder — walks straight out of the
catch handler, faults a task nobody observes, and leaves `open.RenameInProgress` set to `true` for the
life of the process.

My throwaway test made the folder unwritable between the journal write and the first move, using the
existing `IRenameJournalWriter` seam (no production code touched):

```
start: status=202
terminal state after 8s: running
cancel -> state=cancelling
after cancel: cancelling phase=renaming
vote after wedge -> rename_in_progress
```

**Cost to him:** the progress bar sits at "renaming" for ever. Cancel turns it to "cancelling" and it
stays there. Every vote, skip, discard and undo answers `409 rename_in_progress`. `DELETE /session` is
refused too (§ 10.4, deliberately). He can do nothing but stop the server.

**The reassuring half, also proven.** A second test restarted the registry with the folder writable
again: the journal was still there, recovery finished the moves and reunited every rating.

```
matches before: 4
wedged state: running
reopen: ok
matches after: 4
files: rankmaster_db.json, 000003-d901.jpg, 000002-d901.jpg, 000001-d901.jpg
```

So this costs him a restart, not his ranking — which is why it is here and not in § 1.

**How likely is the trigger on Windows? [guess].** I forced it with a permission change. On Windows,
`File.Move` raises `UnauthorizedAccessException` on an ACL denial (a file copied off a share or a
camera card with odd permissions), and `Directory.EnumerateFiles` raises it too. A read-only
*attribute* does not block a rename, and a file merely held open by another program raises
`IOException`, which **is** handled. So: reachable, not common.

### 2.3 Five ways the server refuses to start, all of them a stack trace — and the tray's friendly dialog is wrong for two — [proven]

Each of these was driven against the real console host. Every one ends in an unhandled exception and a
fourteen-line .NET stack trace; the process aborts.

| What he did | Where it dies | What the console prints |
|---|---|---|
| Port 18611 already taken (a second copy, or another app) | Kestrel bind | stack trace only |
| `ListenAddress` set to an address the PC no longer has (DHCP changed) | Kestrel bind | stack trace only |
| `certificate.pfx` unreadable (antivirus or a backup agent holding it; bad ACLs after a restore) | `Security/CertificateStore.cs:140` | `UnauthorizedAccessException` + trace |
| Data directory's parent not writable | `Security/DataDirectory.cs:12` | `UnauthorizedAccessException` + trace |
| `certificate.pfx` is a directory | `Security/CertificateStore.cs:133` | `IOException` + trace |

The certificate case is a genuine hole in otherwise careful code: `CertificateStore.LoadOrCreate`
wraps `Load` in `catch (CryptographicException)` only (`CertificateStore.cs:59`), so an I/O or
permissions failure sails straight past. Only `ListenAddress` *parsing* errors produce a sentence he
can act on (`Rm2SecurityOptions.cs:133`), and even that is followed by a stack trace.

**The tray build is better but misleads.** `src/RankMaster2.Tray/Program.cs:60-67` does catch and show
a MessageBox, but `Describe()` (line 89) walks to the *innermost* exception and appends "Is another
copy of the server already running?" when it is a `SocketException` **or an `IOException`**. So for the
unreadable certificate and the unwritable data directory, the owner gets a dialog reading, in full:

> **Permission denied**
>
> Is another copy of the server already running?

The path has been thrown away — it lived on the outer `UnauthorizedAccessException` — and the suggested
cause is wrong. He will go hunting for a second copy of the program that does not exist.

### 2.4 The day the certificate changes, his phone stops and nothing tells him — [proven]

**What happens to him.** The phone says "certificate problem" and will not connect. The server is
running fine, the tray icon looks normal, the log says nothing alarming, and no pairing window opens.
He has to know, unprompted, to open the tray and re-pair — the phone *and* the desktop client.

A device was paired, `certificate.pfx` replaced with an expired one, and the server restarted:

```
fingerprint before: sha256:c164b29702f40478ea9a32868e7a235873185db14aad3ea598ce03a241af81bf
log (all levels):   [info] Security layer ready. … certificate sha256:1834ced9…
ping fingerprint after: sha256:1834ced93bf519f338082918b8565922cf80845b00beae499abc3de47d1754c7
old token still works:  200          (the token is fine — the phone just cannot reach it)
pairing window opened:  no pairing.json
client pinned to the old cert: curl exit=60 (SSL certificate problem)
```

`CertificateStore.cs:50, 59, 66` regenerate silently whenever the file is expired, not-yet-valid, has
no private key, or throws `CryptographicException`. Nothing is logged above `Information`, and the new
fingerprint is buried mid-sentence in a routine startup line. `SecurityEndpoints.cs:147` only auto-opens
a pairing window when `devices.json` was **absent**, which it is not here; the tray's one warning
(`TrayApp.cs:191`) keys on `devices.json.corrupt-*`, which does not exist here either.

Three realistic triggers, in likelihood order: a corrupt or truncated `certificate.pfx` (an interrupted
write, a sync client's conflicted copy) — today, not in ten years; the PC clock moving backwards past
`NotBefore` (dead CMOS battery, a manual date fix); and actual expiry in ~10 years, which lands **a day
early** (`NotAfter > UtcNow.AddDays(1)`, line 50).

Note the asymmetry: a damaged `devices.json` is deliberately **quarantined** so he can see what
happened (`TokenStore.cs:271-290`), but a damaged `certificate.pfx` is **overwritten**
(`CertificateStore.cs:133`) — the old identity is destroyed even if the read failure was transient.
`SERVER_RUNNING.md:251` covers only one cause ("the data directory was deleted or moved"); the fix it
gives is right, the causes are incomplete.

### 2.5 On a video folder the phone holds every player alive at once — the case its own notes say has already killed it twice — [read; verified independently by me]

`android/app/src/main/kotlin/com/rankmaster2/phone/ui/rank/RankState.kt:27-32` states the rule:

> While something is [full screen], the panes underneath must stop: a second player on the same file
> means two hardware decoders on one video, which is how the app died the first time it was asked to
> watch one.

What the code does: `Rm2VideoPlayers.setPlaying` (`.../media/Rm2VideoPlayers.kt:388-390`) only flips
`playWhenReady`. It does not release the decoder, and `MediaPane.kt:148-151` says so explicitly — *"Not
in front means black, not gone: the player keeps its buffer and its place."* The player is released only
when the composable leaves the composition (`MediaPane.kt:220-224`), and `RankRoute.kt:95-133` composes
`FullScreenViewer` **in addition to** `RankScreen`, not instead of it. So with the viewer open there are
two pane players plus the viewer's own — and a pager mid-swipe composes both pages, making four. At
`TARGET_BUFFER_BYTES = 32 MB` each that is up to 128 MB of buffer and four concurrent decoder instances.

`android/MEMORY_PROPOSALS.md:11-14` lists, under *"Already done and deliberately not re-proposed"*:
**"releasing a covered pane rather than pausing it"**. The code pauses. The same document opens with
*"The app has died twice with `OutOfMemoryError`"* on two 4K AV1 clips side by side.

**[guess]** on the consequence: whether four concurrent AV1 instances actually fail is
device-dependent, and nothing here runs ExoPlayer. That four *players* exist is plain from the code; I
traced the composition myself.

### 2.6 A refusal whose embedded snapshot will not parse leaves the phone screen completely dead — [proven]

`RankViewModel.finish` (`RankViewModel.kt:263-273`): `stale_pair_token` and `no_current_pair` are
silent by code, and the snapshot is adopted as `result.session ?: it.snapshot`. If `session` is `null`,
**nothing happens at all** — no picture change, no message, no spinner — and `actionable` stays true, so
every further tap does the same nothing.

```
PROBE5 sends=3 problem=null notice=null seq=1 busy=false actionable=true
PROBE7 code=stale_pair_token session=null   (a real 409 through MockWebServer, one required field missing)
```

`OkHttpRm2Client.kt:235-237` decodes the embedded snapshot inside `runCatching { … }.getOrNull()` — a
deliberate and defensible choice ("an unreadable snapshot costs the snapshot alone"), but for these two
codes it costs *all* feedback. Today the server always attaches one (`SessionRegistry.cs:1550-1568`), so
this needs a schema drift between phone and server — which is exactly the situation the README
describes, because the three programs are updated separately and by hand.

**Cost to him:** the app stops responding to taps with nothing on screen to say why. He restarts it.

### 2.7 One tap on a stalled PC costs about a minute of dead screen — [proven, phone]

`Rm2Http.kt:101-106` sets connect 5 s, read 30 s, write 15 s and **no `callTimeout`**:

```
PROBE10 connect=5000 read=30000 write=15000 call=0 retryOnConnectionFailure=false
```

`sendWithRetries` resends once on `Unreachable` (`RankViewModel.kt:227-233`), so a PC that accepts the
connection and then stalls costs 2 × 30 s with `busy = true`, no spinner on the ranking screen, and no
way to abandon it.

Related, in both clients at once: `retryAfterSeconds` is taken from the wire with a floor and **no
ceiling** — `RankViewModel.kt:235` on the phone, `SessionLink.cs:652-655` on the PC. A server saying
`86400` freezes either client for a day. **[guess]** as a live risk (the server sends 1), but it is an
unbounded number off the network driving a UI lock.

### 2.8 Up to four seconds of frozen client every time he leaves a video folder — [read]

Returning to the start screen calls `ReleaseVideoSurfaces()` → `VideoSurface.Dispose()` →
`StopAsync().GetAwaiter().GetResult()`, and `WaitReleasedAsync`
(`pc/src/RankMaster2.Pc/Video/VideoSurface.cs:378-399`) waits up to 2 s per surface
(`ReleaseWaitAttempts = 40 × 50 ms`). Two surfaces, so up to 4 s of frozen "busy" after the last pair,
or after `Ctrl+Z` from the exhausted screen. `Quit()` deliberately avoids this
(`RankCoordinator.cs:516-522`); the other three callers do not.

---

## 3. Would annoy him

### 3.1 "Resume last folder" never appears, and the connection line never updates — the only method that loads them is never called — [proven]

`RankCoordinator.InitializeAsync` (`pc/src/RankMaster2.Pc/Ui/Surface/RankCoordinator.cs:93-98`) is the
only place that does `Start.LastFolder = _folderStore.Load()` and the only place that raises `Changed`
after `ConnectAsync` finishes. **Nothing calls it:**

```
$ grep -rn "InitializeAsync" pc/src pc/tests --include=*.cs | grep -v /obj/ | grep -v /bin/
pc/src/RankMaster2.Pc/Ui/Surface/RankCoordinator.cs:93        (the definition)
pc/tests/RankMaster2.Pc.Link.Tests/Harness/RealServer.cs:40   (an unrelated xunit fixture)
```

`MainWindow.OnOpened` (`pc/src/RankMaster2.Pc/App/MainWindow.axaml.cs:28-41`) fires
`_services.Link.ConnectAsync()` directly and discards the result. Consequences:

- `Start.LastFolder` stays null on every launch, so `StartView.Refresh`
  (`pc/src/RankMaster2.Pc/Ui/Views/StartView.axaml.cs:106`) hides the Resume button. **He clicks Open
  and walks the folder picker every single session.** `LastFolderStore.Save` *is* called on a successful
  open (`RankCoordinator.cs:373`), so the file is written faithfully and never read — including the
  hand-over of the old WPF client's last folder, which was its stated purpose.
- The start screen paints once from the `UiRoot` constructor while `LinkState` is still
  `Disconnected`, so it says **"Not connected to the ranking server yet."** — and nothing raises
  `Changed` when the connect completes (the 250 ms `Tick` repaints only for toasts and the late-action
  line, `RankCoordinator.cs:103-121`), so that sentence stays on screen after the server is ready.

This is the client that was rewritten because starting was too slow. No test covers it:
`StartModelTests` tests `StatusLineFor` in isolation; `UiRootTests` builds its own coordinator and
never exercises launch.

### 3.2 The pairing window tells him he was attacked, every time he pairs successfully — [read + the decisive half proven]

He scans the QR, the phone pairs, and within half a second the PC window turns red and says **"Closed:
too many wrong codes. Open a new one."** He has just succeeded and is told he failed because of an
intruder.

- `src/RankMaster2.Tray/PairingForm.cs:144-149` — its 500 ms tick treats *the offer file being gone* as
  one thing only: five wrong guesses.
- `src/RankMaster2.Server/Security/PairingService.cs` deletes that file in **three** situations: a
  stranger burning their budget (line 169), shutdown (line 122), and **a successful pairing** (line 190).

The third was confirmed against the live server:

```
=== open session === … 201
=== offer file after successful pair ===
certificate.pfx  devices.json          ← pairing.json is gone
```

Nothing closes the form after a successful pair (`TrayApp.cs:99-111`), so the message is guaranteed on
the happy path. **[read]** for the dialog itself — WinForms does not build here. Likely consequence: he
clicks "New code" and pairs the same phone a second time, leaving a duplicate device in `devices.json`.

### 3.3 A stranger on the LAN can still repeatedly deny his pairing, and the spec says otherwise — [proven]

`SERVER_SPEC.md` § 10.11 introduced the per-address guess budget precisely to stop this: *"it handed
any host on the LAN a one-packet denial of the owner's own pairing, repeatable forever."* Half the fix
landed. The window survives a stranger's five guesses — but `PairingService.cs:169` still deletes
`pairing.json`, the file `rm2ctl` and the tray read the code from:

```
=== does the offer file survive the budget burn? ===
pairing.json GONE
```

He clicks "New code"; the stranger spends another five; repeat. The rate limiter (5/min,
`Retry-After: 45`, verified correct) slows it but does not stop it.

**The spec contradicts the code here.** § 10.11 says "When an address exhausts its five, **the window
is destroyed**… the first address to spend its five ends the window anyway", and § 15's limits table
(line 1709) repeats it. `PairingService.cs:158-172` destroys only that address's budget, and says so in
its own comment. `tests/RankMaster2.Audit.Security/PairingWindowSecurityTests.cs:28` asserts the
*code's* behaviour. So two sentences of the contract are simply false — and they are the sentences the
tray's wording in § 3.2 was built from.

### 3.4 After re-opening a folder the phone screen can jump backwards to a pair he already voted past — [proven]

`RankViewModel.resume` (`RankViewModel.kt:122-135`) sets `busy = false` unconditionally, and `finish`
(`RankViewModel.kt:252-279`) has **no `pairSeq` guard** — unlike `refresh`, which has exactly that
guard at `RankViewModel.kt:158-165`. So a vote whose response is still outstanding when he backs out
and re-enters the folder can land later and overwrite a newer screen:

```
PROBE2 finalSeq=2 finalLeft=charlie.jpg busy=false
```

No double vote results — the server's pair token catches every stale send — but the screen and the PC
disagree until the next round trip.

### 3.5 The still cache ignores the data directory he configured — [proven]

`SERVER_RUNNING.md:180` lists `cache\` as one of the files *in the data directory*.
`Media/MediaOptions.cs:66-76` reads only its own `Media:CacheDirectory` key, falling back to a
hard-coded `%LOCALAPPDATA%\RankMaster2\Server\cache` — it never consults `RankMaster2:DataDirectory` or
`RM2_DATA_DIR`. With `RankMaster2__DataDirectory` pointed at a temp folder:

```
=== configured data dir ===   certificate.pfx  devices.json     (that's all)
=== default cache dir ===     271M  ~/.local/share/RankMaster2/Server/cache
```

So if he moves the data directory because C: is full — which is the only reason anyone moves it — up to
512 MB of cached JPEGs keeps filling C: anyway. `Media:CacheDirectory` is documented nowhere, so he has
no way to move it deliberately either.

### 3.6 The VLC plugin index is probably rebuilt on every launch — [read; the decisive fact is a guess]

`VideoEngineGate` is constructed with the literal default `libvlcVersion: "3.0.21"`
(`pc/src/RankMaster2.Pc/App/VideoEngineGate.cs:29`) and compares the on-disk stamp against it (`:72`).
`LibVlcIndex.Build` writes that stamp using the **runtime** string from `lib.Version`
(`pc/src/RankMaster2.Pc/App/LibVlcIndex.cs:86-88, :103`). **[guess]:** LibVLC 3.0.21 reports its
version with a codename appended ("3.0.21 Vetinari"). If it does, `IsCurrent` is false on every launch,
and because the install directory (`C:\Utils\rank-master-2\pc`) is user-writable and passes `CanWrite`,
the gate runs a full `new LibVLC("--reset-plugins-cache")` plugin scan **before every first video**,
defeating the index the installer built. Settling it needs one line printed from a real libvlc on
Windows.

### 3.7 Re-pairing the phone opens a second OkHttp disk cache on the same directory — [proven]

`Rm2Http.kt:78-81` keys its client map on `fingerprint + "|" + token.take(12)` and never evicts. A new
token is a new key, a new `build()`, and a second `Cache(directory, …)` at `Rm2Http.kt:108` over the
same folder as the first — which is still open.

```
PROBE8 sameClient=false firstDir=…/probe8/rm2-media secondDir=…/probe8/rm2-media
```

OkHttp documents two caches on one directory as corrupting. **[guess]** on what Android's DiskLruCache
then does: at best every photograph is re-downloaded, at worst it throws inside the cache writer.
`Rm2HttpCacheTest` covers `configure()` ordering carefully and never covers re-pairing. (Same line,
cosmetic: `take(12)` of a token whose first four characters are the literal `rm2_` leaves eight
distinguishing characters — the truncation buys nothing.)

### 3.8 A cancelled prefetch on the phone keeps downloading and slows down the picture in front of him — [proven]

`MediaPrefetcher.kt:100-113` uses `Call.execute()` — blocking, and uninterruptible by coroutine
cancellation; `ensureActive()` at line 70 only checks *between* URLs. The caller is a
`LaunchedEffect(state.snapshot?.pairSeq, …)` (`RankRoute.kt:89-93`), so every vote cancels the previous
warm-up without joining it.

```
PROBE11 job.join() returned after 1554ms; requests the server served=1
```

Cancelled at 200 ms into a 1500 ms body; the server still served the whole thing. This is exactly the
outcome the class comment at `MediaPrefetcher.kt:70-72` says the class exists to prevent. Symptom:
while the PC is slow, voting *faster* makes the next picture arrive *later*.

### 3.9 The durability he traded away bought the average, not the wait he actually notices — [proven, measured]

He agreed to risk a couple of votes to make voting feel instant.
`tests/RankMaster2.Server.Tests/VoteCostBenchmark.cs`, run here, 100 real `POST /session/vote` calls:

```
n=20000  disk   SaveDelaySeconds=0  mean 88.9 ms  median 88.6 ms  max 104.5 ms
n=20000  disk   SaveDelaySeconds=2  mean 25.4 ms  median  7.2 ms  max 108.0 ms
n=2000   disk   SaveDelaySeconds=0  mean 18.7 ms  median 18.1 ms  max  36.4 ms
n=2000   disk   SaveDelaySeconds=2  mean  4.3 ms  median  1.3 ms  max  25.1 ms
```

The median collapses; **the maximum does not move.** `SessionRegistry.AfterChoice`
(`SessionRegistry.cs:1533-1542`) still does the whole write synchronously, inside the request, on every
fifth choice. One vote in five costs what every vote used to cost. `SaveCostBenchmark` measures the
write itself at **133 ms per save for 20,000 files** on this box's local disk.

**[guess]** for his machine: on a USB drive an fsync of a ~4 MB file is the thing he feels, and every
fifth vote will pay it. If such a write ever exceeded the five-second gate timeout
(`SessionRegistry.cs:39`), the votes queued behind it would be answered `503 session_busy`.

### 3.10 A cold connect can leave him looking at "Opening…" for up to 18 seconds — [read]

`LinkOptions.cs:32-33` allows 8 s for the pairing offer plus 10 s for the server to start. Open and
Resume are enabled during it and `Gated` makes them *wait* rather than refuse (that was the H9 fix), so
a press can sit on "Opening …" for 18 s with nothing explaining why. The window itself comes up fast;
the first pair does not.

### 3.11 A trailing slash on a media URL returns a 403 naming a file nobody asked for — [proven]

```
GET /api/v1/media/alpha.jpg/still?w=360    → 200 image/jpeg 1348 bytes
GET /api/v1/media/alpha.jpg/still/?w=360   → 403
  {"code":"media_extension_not_allowed", …, "details":{"id":"still","extension":""}}
```

Routing tolerates the trailing slash and matches with `id=alpha.jpg`, but
`MediaEndpoints.RawIdSegment` (`Media/MediaEndpoints.cs:415-428`) takes `segments[^2]` of the raw
target, which is now `"still"`. Low likelihood — clients use the `links` verbatim — but the error is
flatly wrong, and it is invisible to the suite: the in-memory `TestServer` exposes no `RawTarget`, so
that whole code path is untested by construction.

### 3.12 The still cache can hand back a file it has just deleted — [proven at unit level; not reproduced end to end]

`StillCache.GetOrAddAsync` writes the entry, runs eviction, then returns the path
(`Media/StillCache.cs:186-190`); `MediaEndpoints.StillAsync:300` immediately does
`new FileInfo(cached).Length`. If eviction took that entry, that throws `FileNotFoundException`, which
the handler converts to **404 `media_file_missing` — "The file for this id has gone from disk."**
naming a photograph that is sitting right there.

```
scanned=True
returned path:  /tmp/rm2-zz-b4dae879/ab/abcdef…-s720j-q85.jpg
exists on disk: False
new FileInfo(path).Length -> FileNotFoundException
```

Honest limit: **180/180 requests came back 200** under an artificially tiny cache bound with 8-way
concurrency. The natural trigger is a concurrent eviction at the 512 MB bound, which could not be
arranged on demand. The path is reachable and the message is wrong; the frequency is unknown.

### 3.13 Small ones, one line each — [read]

- **Any paired device can revoke any other.** `SecurityEndpoints.cs:327-338` never consults
  `SecurityMiddleware.AuthenticatedDevice`, though its own comment on line 329 says "A device may
  revoke itself (§ 10.12)". Proven: a second device revoked the phone —
  `DELETE /pair/dev_8ce0… → 204`, the phone's own ping → 401. Not an auth bypass (a valid token is
  required), but a lent or stolen phone can log out his desktop client.
- **A stale `pair.request` opens an unattended pairing window.** `PairingChannel.Request`
  (`Tray/PairingChannel.cs:46`) writes the sentinel and gives up after 8 s, leaving the file behind.
  The next server start consumes it and opens a live five-minute window with nobody at the screen.
- **`If-Range` with an HTTP-date is never honoured** (`MediaHttp.cs:319-326`), so a player that sends a
  date gets the whole video instead of the range. Matches § 12.4 as written, so the spec is the narrow
  thing, not the code.
- `FrameCache` is keyed by **filename only**, not (folder, filename)
  (`pc/src/RankMaster2.Pc/Stills/FrameCache.cs:15-17`); nothing calls `ReleaseAllAsync` when a folder
  closes. The desktop twin of § 1.2, with an extra `(length, mtime)` check, so much narrower.
- `HandleCheck.CanOpenExclusivelyAsync` opens with `FileAccess.ReadWrite`
  (`pc/src/RankMaster2.Pc/Stills/HandleCheck.cs:28`), so on a read-only file every discard burns the
  full 2 s retry loop before anything is sent.
- `ActionResult.NotSent` maps to `Notice.Silent` (`pc/src/RankMaster2.Pc/Ui/Surface/Notices.cs:56`) — a
  keypress that produces it does absolutely nothing visible.
- The phone's "this last one did not count" (`RankViewModel.kt:338-343`) asserts something it cannot
  know after two lost responses. Its own cancel path (`RankViewModel.kt:78-88`) and the PC client both
  say "may or may not have landed". Same product, two standards of honesty for the same uncertainty.
- Containers with no demuxer in the pruned 26-plugin set: `.ts/.m2ts`, `.wmv/.asf`, `.flv`, `.ogv`.
- **Browse reach is genuinely unrestricted**, as advertised (`ping` reports `browse: "full-filesystem"`):
  drive roots, `/etc`, other users' home directories, `/proc/self`. Folder *names* only — never files,
  never bytes, never recursive — so the documented boundary holds. Worth him knowing plainly: a paired
  device can map every folder on his PC.

---

## 4. Would confuse the next reader

### 4.1 Three places still tell him that nothing is ever unsaved — [read; contradicted by the code beside them]

- `SERVER_RUNNING.md:224`: *"Every response the server has already sent was written to disk before it
  was sent (`SERVER_SPEC.md` § 13.1), so there is no unsaved work and no prompt."*
- `src/RankMaster2.Tray/TrayApp.cs:140-142`: the same sentence.
- `src/RankMaster2.Tray/Program.cs:82`: *"every response the server has already sent was durable before
  it was sent"*.

§ 13.1 says the opposite, and `SERVER_RUNNING.md:94` — 130 lines above — says the opposite correctly
(*"the most a crash or a power cut can cost is the last few votes"*). The **conclusion** is still right
for a clean Exit, because `Rm2Host` registers `SessionRegistry.Dispose` on `ApplicationStopping` and
Dispose flushes; the **reason given** is false, and it is exactly the reason someone would rely on when
deciding whether a new shutdown path needs a flush.

### 4.2 `SessionRegistry.Dispose` flushes only the first time it is called — [read]

`SessionRegistry.cs:1326-1338`: `StopFlushLoop()` runs first, then `if (_disposed) return;` guards the
flush. Nothing else in the class checks `_disposed`, so a registry that was disposed and then used
again — the static `SessionRegistry.Shared` across a rebuilt host, which the test harnesses do — serves
requests normally and will **not** flush at the next shutdown. One process, one host in production, so
this is not a live fault; it is a trap, under a comment that explains at length why the *semaphore* is
not disposed and says nothing about this.

### 4.3 A broken shortcut is reported as an attempted escape, on a false premise — [proven]

`MediaResolver.cs:151-155` justifies skipping the containment check with *"File.Exists is false for a
broken link too"*. That is **true** on modern .NET:

```
File.Exists(dangling.jpg) = True
ResolveLinkTarget(final)  = /nowhere/at/all.jpg
resolution = OutsideSession, reason = symlink-outside-folder
```

So a dangling `.jpg` shortcut in his folder gets `403 media_outside_session` ("That id would leave the
session folder") where the code's own intent is `404 media_file_missing`. Harmless outcome; wrong
reasoning, in a security comment.

### 4.4 `ActionGate` rule 5 is wired to itself and can never fire — [read]

`ActionGate.Try`'s rule 5 compares `input.RequestedPairSeq != input.CurrentPairSeq`
(`pc/src/RankMaster2.Pc/Ui/Surface/ActionGate.cs:43-44`). Its only production caller builds both fields
from the same local (`RankModel.cs:72, 79-80`), so `DropReason.TargetMismatch` is unreachable. The one
test that exercises it (`ActionGateTests.cs:68`) hand-constructs a state no caller can produce. Not a
live bug — the real protection is rule 1 plus the link's own pairSeq/pairToken check — but the plan's
"rule 5 as an invariant rather than a hope" is not in force, and the test would convince a reviewer it is.

### 4.5 The PC coordinator mutates the model off the UI thread, contradicting its own documented invariant — [read]

`IUiThread`'s doc says everything in `RankCoordinator` that touches `RankModel` goes through it first.
It does not: `RunAction` uses `.ConfigureAwait(false)` on every await
(`RankCoordinator.cs:546, 552, 553, 558, 561, 569, 574`), so `ApplyResult`, `ApplySnapshotSync`,
`Rank.SetPanes`, `Rank.SetSnapshot` and `Rank.ExitBusy` all run on a thread-pool thread while
`RankView.Refresh` reads `Rank.Left/Right/Snapshot/Busy` on the UI thread with no barrier (`Busy` is a
plain non-volatile `bool`, `RankModel.cs:17`). `OnStillChanged` and `OnVideoStateChanged` *do* marshal
correctly; the result path does not. This is also what makes § 1.1 deterministic — the free happens on
the pool thread, the paint on the dispatcher.

### 4.6 Five complete stale copies of the source tree sit inside the repository — [proven]

`.claude/worktrees/` holds five agent checkouts, 1.4 GB, each containing an older
`src/RankMaster2.Server/Sessions/SessionRegistry.cs` — **977 lines against the real 1,966**. They are
`.gitignore`d, so git does not see them, but `grep -r` and `find` do, and every search of this tree
returns six answers where one is right. This cost me time at the start of the audit and it will cost
the next reader the same. Smaller, same bucket: `pc/dist/` holds `RankMaster2-pc-2.0.0.zip` beside
`RankMaster2-pc-3.0.0.zip`, and a 555 MB `kit/`.

### 4.7 The README uses three names for two programs — [read]

`README.md` distinguishes "Rank Master 2 — the WPF desktop app" from "the PC client". Then its
what's-new list has *"The Windows client's first-run faults are fixed"* and, four bullets later, *"The
**desktop app** starts far less work — the video engine is no longer loaded…"*, which describes the new
Avalonia client, not the frozen app the same page just defined "desktop app" to mean. And
`README.md:15` still says *"The runnable app is the single file one level up: `..\RankMaster2.exe`"*,
which is the old app's layout; the three programs of Rank Master 3 go to three other places.

Both the frozen app (`src/RankMaster2.App`) and the new client (`pc/src/RankMaster2.Pc`) have
`AssemblyName` = `RankMaster2` and produce a file called `RankMaster2.exe`. `pc/install.ps1:45-50`
handles that correctly by filtering on path when it stops a running client; Task Manager will not.

### 4.8 A comment on the phone contradicts the code one line away — [read]

`RankScreen.kt:194-196`: *"Videos run only while this screen is in front **and nothing is in
flight**"*. `RankState.kt:50` is `foreground && viewing == null`. `busy` is not in it. Harmless
behaviourally; in a codebase where the comments are the specification, it is a trap.

### 4.9 `pc/plans/*` still spell the server data directory in lower case — [read; documentation only]

`PC_CLIENT_PLAN.md:333` and `pc/plans/B-server-link.md:78` say `%LOCALAPPDATA%\RankMaster2\server`. The
code — `Rm2SecurityOptions.ResolveDataDirectory` (`Rm2SecurityOptions.cs:99-110`) and
`pc/src/RankMaster2.Pc/Link/Enrolment/ServerPaths.cs:70-83` — agrees on `RankMaster2/Server` with a
capital S, and `SERVER_RUNNING.md` was corrected. Only the plans are stale, and on Windows the
difference is invisible. Noted because `AUDIT.md` C12 is marked closed and this is the residue.

### 4.10 Packaging: correct, but the pc zip alone is not a working install — [read + inspection of `pc/dist`]

`pc/dist/RankMaster2-pc/` is 250 files / 137 MB and genuinely self-contained — I counted them. It has
`coreclr.dll`, `hostfxr.dll`, `libSkiaSharp.dll`, `av_libglesv2.dll`, `libHarfBuzzSharp.dll`, and
`libvlc/win-x64/{libvlc.dll, libvlccore.dll, plugins/…}` with **26 plugin DLLs matching the 26-line
manifest exactly**, including the `vmem` output plugin the video callbacks need and the three
`*dummy*` plugins the `--intf=dummy --aout=dummy` arguments need. Nothing needs a VC++ redist or a VLC
install. `publish.sh:53-95` enforces all of that with a hard shape check, and the
`-r win-x64 --self-contained` that the libvlc copy target depends on (`RankMaster2.Pc.csproj:89`)
exists **only** on that script's command line — a plain `dotnet publish` produces a client with no
`libvlc\` and silently no video.

Two gaps: `Link/Paths.cs:30-35` defaults the server launcher to
`{app dir}\..\tray\RankMaster2.Tray.exe` and `pc/install.ps1:7-8` deliberately never installs `tray\`
or `server\`, so the zip is a working install only on a machine that already has them (his does, since
`publish-tray.ps1` writes to exactly that path; a genuinely clean one would not). And
`pc/install.ps1:3` and `publish.sh`'s closing line point at
`https://bormin.fintebtc.de/rm2/install.ps1`, while `/var/www/bormin/web/rm2` does not exist on this
box — the one-liner 404s until `publish.sh` is run with upload enabled.

---

## 5. Tests that prove less than they appear to

The previous audit's main complaint — tests that `return` early when their fixture is missing and are
counted as passing — **was genuinely fixed on the server and on the phone, and I checked rather than
took it on trust.**

- **Server:** `Skipped: 0` across all 679 tests with the corpus present, and `Xunit.SkippableFact` is
  used where it should be, so a clean checkout would *count* the skips instead of reporting green.
  `ContractShape.RequireErrorBody` (`tests/RankMaster2.Server.Tests/Harness/ContractShape.cs:202-224`)
  is now strict about the envelope's exact five fields.
  `PingContractTests.Authenticated_ping_summarises_the_open_session` (`PingContractTests.cs:112-147`)
  now asserts `sessionId`, `folder` and `state` instead of guarding one structural check behind an `if`.
  `SessionLifecycleTests.A_folder_whose_lock_is_held_by_another_process_is_refused`
  (`SessionLifecycleTests.cs:236-268`) now spawns the real `tests/RankMaster2.LockHolder` process, so
  the OS is actually asked.
- **Phone:** 354 passed, 0 failed, 0 skipped, and a grep of the whole Kotlin test tree for `assumeTrue`,
  `Assume`, `@Ignore`, early `return` inside a test body, and swallowed `catch` blocks returns **zero
  hits**. `FakeRankClient`
  (`android/app/src/test/kotlin/com/rankmaster2/phone/rank/FakeRankClient.kt:17-33`) now *throws* on an
  unqueued call instead of returning a canned success — the worst item in the previous audit's T2,
  properly gone.

**It was not fixed everywhere. The PC suite is where it still bites.**

- **25 of the PC's 26 skips are the entire real-LibVLC suite, and they will skip on his machine too.**
  `LibVlcProbe.Probe()` (`pc/tests/RankMaster2.Pc.Tests/Video/LibVlcTests.cs:359-374`) calls
  `backend.Initialize(null, …)` — a *null* native directory, i.e. "find a system-installed VLC". The
  test project references `VideoLAN.LibVLC.Windows` with `ExcludeAssets="all"`
  (`RankMaster2.Pc.Tests.csproj:34`), so no libvlc is ever copied into the test output, and the shipped
  layout (`LibVlcLayout.NativeDir`) is a path this probe never tries. I verified both facts myself.
  `Two_4K_AV1_surfaces_both_reach_Playing`, `Repeated_play_stop_and_create_dispose_does_not_leak` and
  `StopAsync_releases_the_file` are among them. The suite runs only inside
  `pc/tests/run-video-linux.sh`'s Debian container. This is a visible, *counted* skip, which is better
  than the previous audit's silent `return`; it is still 25 tests that never run where the product runs.
  The 26th skip (`ShellTests.cs:53`) skips because `LibVlcProbe` already loaded LibVLCSharp in-process —
  same on his machine.
- **`CompositionTests.Composition_Build_IsFastAndLoadsNoVideoEngine`**
  (`pc/tests/RankMaster2.Pc.Tests/App/CompositionTests.cs:27-28, 44-49`) wraps its load-check in
  `if (!alreadyLoaded)`. In a full run that is usually true, so the half the name advertises is silently
  skipped and the test still reports **Passed**. Whether it runs at all is xunit scheduling luck. This
  is precisely the pattern the last audit named, surviving in one file.
- **The Stills suite's 0 skips depend on this box.** Run from a copy outside the repository so
  `Corpus.Find()` fails: **67 passed, 38 skipped**. On a clean checkout, 38 of 105 stills tests — the
  broken-file, EXIF-orientation, colour-profile and 40 MP-bomb ones — vanish and the suite still says
  Passed. Designed and honest, but "the PC suite is green" means much less without `tests/corpus/media`.
- **`HelpRowsTests.Every_single_key_row_matches_KeyMap`** (`.../Ui/Surface/HelpRowsTests.cs:12-21`) —
  the file's header says it exists "so the two cannot drift", but the method never reads `HelpRows`; it
  re-asserts `KeyMap` against hard-coded expectations. Change `HelpRows`' "Ctrl+Z" caption to "Ctrl+Y"
  and both tests still pass.
- **`LibVlcIndexAndGateTests`** passes `"3.0.21"` on both sides of every stamp comparison, which is
  exactly why § 3.6 is structurally invisible to it.
- **`SmokeTests.Project_builds`** in `RankMaster2.Pc.Stills.Tests` is literally `Assert.True(true)`.
- **`ManifestTests`** is not vacuous — it fails hard if the NuGet folder is missing — but it proves only
  manifest ⊆ package, never that the 26-plugin set is *sufficient*. Sufficiency is checked only by
  `pc/tools/prune-check.sh`, which needs Docker and is not in the suite.

**Weaker than their names, on the server:**

- **`BrokenFilesAreRefusedCleanly`** (`tests/RankMaster2.Server.Tests/Media/StillRendererCorpusTests.cs:214-243`)
  accepts either "threw `StillDecodeException`" **or** "didn't throw and produced some bytes". For
  `empty.jpg` (0 bytes) and `text_pretending.jpg` only one of those can be correct. Measured: all five
  throw today, so the permissive branch is dead code that would silently absorb a regression where a
  text file starts rendering as a grey rectangle.
- **`RealExifOrientationsAllDisplayTheSameWayUp`** (same file, line 77) does
  `if (!File.Exists(path)) continue;` inside the loop and only skips when **zero** orientation files are
  present (line 110). With one of the eight present, `distinct.Length == 1` and the test passes green
  while proving nothing about orientation at all. The name promises all eight.
- **`TheRealWideGamutPhotographIsConverted`** (line 173) has two `Skip.If` calls *after* the corpus
  check (lines 191-192) that depend on what Skia makes of the profile. It ran here; on a machine where
  Skia parses that ICC differently it vanishes into a skip with no colour management tested.
- **Three-way outcome assertions in the security audit** — `AuthGateTests.cs:193` accepts
  `401 or 404 or 400` for twelve path spellings, and line 211 accepts `404 or 405 or 401` for non-GET
  `/ping`. For `/api/v1/session/` and `/api/v1/SESSION` only `401` is correct: routing *does* reach
  those, so a `404` would mean the client can no longer talk to the server, and the test would still be
  green. `Malformed_authorization_header_is_401:98` likewise accepts either code for every case.
- **`PingContractTests.An_invalid_token_is_rejected_rather_than_downgraded`** accepts either
  `invalid_token` or `unauthenticated` where `SERVER_SPEC.md:129` names one. Defensible here
  (`"not-a-real-token"` is not well-formed), and the specific case is covered properly by
  `AuthenticationTests.A_well_formed_but_unknown_token_is_an_invalid_token`. Noted so the pattern is not
  copied.
- **`V1SchemaTests.The_v1_schema_is_byte_for_byte_unchanged`**
  (`tests/RankMaster2.Audit.Compatibility/V1SchemaTests.cs:36-53`) compares against a golden file
  produced by the code under test. It pins the writer against *drift*, which is what it claims, and it
  is a good test for that. It is not evidence that the real Rank Master 2 reads what this writes —
  nothing in the tree is. The input side is honest: `Support/Rm1.cs` hand-rolls the bytes rather than
  round-tripping through `JsonCatalog`. Worth saying out loud, because the database format is his hard
  constraint and the strongest claim available here is "we did not change our own output".
- **`V1SchemaTests.The_conservative_score_is_never_stored`** asserts the saved text does not contain the
  substring `"rank"`. Its fixture is `Stills(3)`, so it passes; a library containing a photograph called
  `rank.jpg` would fail it for a reason unrelated to the rule.

**Weaker than their names, on the phone:**

- **The ranking fixtures make § 1.3 untestable.** `RankFixtures.ranking()`
  (`rank/FakeRankClient.kt:134`) hardcodes `alpha.jpg` / `bravo.jpg` regardless of `pairSeq` or token.
  Every ranking test therefore reasons about tokens and sequence numbers only; **no test in the
  repository can observe a vote landing on the wrong picture, because in every fixture both pairs are
  the same picture.** That is the single highest-value change to that suite.
- **`opening the viewer stops the panes underneath`** (`rank/RankViewModelTest.kt:445-457`) asserts a
  boolean flips, under a comment about two hardware decoders on one file. It proves the boolean; the
  decoder claim in that comment is false (§ 2.5).
- **`LoadControlBudgetDesignGuardTest`** (3 tests) is arithmetic on constants — its own doc comment says
  so, which makes it disclosed rather than misleading. **`PaneMenuDesignTest`** is 32 of the phone's 354
  tests — 9 % of the suite on star geometry, glyph bounds and scrim alpha, against 33 on the vote path.
  The weight is inverted relative to what can cost him a rating.
- **`RankViewModelTest`** still exercises `save()` and `skip()`, which the ranking screen no longer calls
  (`RankScreen.kt:42-47`). Correct tests of unreachable code.
- **`Rm2HttpCacheTest`** mutates process-wide `Rm2Http` state (a singleton map and a static cache
  directory). It passes because each test uses a distinct `TemporaryFolder`, but it is order-coupled by
  construction and never covers the re-pair case of § 3.7.
- **`AuditLeaseLeakTests.cs:87`** (PC) asserts, with an approving comment, the very state that § 1.1
  exploits: `Assert.Same(leaseA1, c.Rank.Left.Lease); // still carries the already-disposed lease`. The
  test is correct about the leak it was written for (H8); it pins the disposed-lease reuse as intended.
- **`LazinessTests.Nothing_in_this_process_has_loaded_libvlc`**
  (`pc/tests/RankMaster2.Pc.Tests/Video/LazinessTests.cs:49-51`) returns early on anything that is not
  Linux — it asserts nothing on the only platform the product runs on.

**Gaps, stated plainly.** Nothing in any suite covers: the cross-folder fingerprint collision (§ 1.2);
the concurrent-decode budget (§ 2.1); `RawIdSegment` against a real raw target (§ 3.11) — `MediaHost`
and `WebApplicationFactory` both use the in-memory `TestServer`, which exposes no `RawTarget`, so that
path is untested by construction; or any of the five startup failures (§ 2.3).

---

## 6. What I tried and could not break

This is the part I would want if I were him, so it is specific.

- **A double vote, from any direction.** The pair token is
  `HMAC(session secret, sessionId | pairSeq | left | right)`
  (`src/RankMaster2.Server/Sessions/PairTokens.cs:28-42`), minted per pair generation and compared in
  constant time. A retry after a lost response finds `pairSeq` has moved and gets
  `409 stale_pair_token` with the current snapshot attached, so the client resynchronises in one round
  trip. On the phone, `sendWithRetries` closes over the token and the `clientRequestId` so a retry is
  the identical logical request, and `retryOnConnectionFailure(false)` (`Rm2Http.kt:106`) shuts OkHttp's
  own back door. On the PC, `FrozenRequest` serialises once and the *same bytes* are resent
  (`SessionLink.cs:641-683`). Three of us attacked this from three sides; none could make one choice
  count twice.
- **A vote credited to the wrong pair over the wire, from the PC client.** The UI's `pairSeq` is only
  ever a veto — the actual `pairToken` is read from the **link's own** snapshot inside
  `SessionLink.RunPairAction` (`SessionLink.cs:440-448`), atomically with the `onPairSeq` check. Even
  with a stale UI model the worst outcome is `NotSent(PairMoved)` or a server rejection. This is the one
  design decision that keeps § 4.5 from becoming a § 1 finding.
- **Losing votes to a hard kill.** I cast 40 votes over HTTPS against a running server and then
  `kill -9`'d it. **All 40 were in `rankmaster_db.json`.** The bound holds: `MaxUnsavedChoices = 5`
  forces a synchronous write every fifth choice, so the worst case really is four votes plus up to two
  seconds — the "couple of votes" he agreed to.
- **A full real ranking cycle.** `rm2ctl cycle` against a real TLS server on 39 real photographs:
  **112/112 contract checks passed** — open, pair, media meta/still/thumb/ETag revalidation, vote,
  cancel-vote, skip, discard, special, undo, save, every specified refusal, a rename of 38 files to
  `000001-f1e9.jpg …`, and close. I then read the resulting database directly: the skip's two
  impressions had survived the rename attached to the right (renamed) files, the cancelled vote had left
  no trace, and the discarded file sat in `discarded/` under its original name and out of the database.
  Nothing lost, nothing attached to the wrong picture.
- **Rename recovery.** After deliberately wedging a rename (§ 2.2), a restart recovered every rating and
  finished every move. The journal design does what it claims.
- **Writing an empty database over real ratings.** `JsonCatalog.Save` refuses when the folder is gone
  and refuses when the folder lists no media while the session still holds records
  (`JsonCatalog.cs:56-88`), and never creates the folder. Both paths are tested. I could not get past
  them.
- **A corrupt database being overwritten.** `LoadRequired` throws on unparseable JSON and `OpenAsync`
  turns it into `library_json_unreadable` with the lock released (`SessionRegistry.cs:361-368`). The file
  is left alone.
- **The auth gate.** Every mapped route, plus routes that do not exist, refuse without a bearer token
  (`GET /api/v1/nope` → `401 unauthenticated`, not 404 — correctly fail-closed). `?token=` and
  `?access_token=` are ignored. Two `Authorization` headers are refused. `/ping` with a *bad* token is
  401 rather than the public subset. `/api/v1//ping` is 401. `POST /pair` is the only anonymous non-ping
  route and it is method-checked. No route is unauthenticated by accident.
- **`PathGuard`.** `/etc/../etc`, `%2e%2e`, `%252e` (correctly treated as a literal filename, not
  re-decoded), `//server/share`, `\\server\share`, `\\?\C:\`, relative paths, `CON`, trailing dot,
  trailing space, NUL, newline, empty, 4097 characters, duplicate `path=` — all refused. Nothing got
  through.
- **Media-id escapes and symlinks.** A link named `escape.jpg` pointing outside the folder → 403;
  `passwd.jpg` → `/etc/passwd` → 403; a symlink loop → 403; a link to a sibling *inside* the folder →
  200, which is correct. `%2F`, `%5C`, `..`, rooted ids, control characters, non-UTF-8 — all refused.
- **Range requests.** Every RFC 9110 edge tried is right: `bytes=-0` → 416; `bytes=0-` on an empty file
  → 416; `bytes=5-2` → 200 whole; unknown unit → 200 whole; a 20-digit number → 200 whole; multi-range →
  first only as a single 206; `bytes=0-999999` clamped; `If-Range` match → 206, mismatch → 200. Videos
  stream via `SendFileAsync`, so memory does not grow with file size.
- **Decode bombs.** The 40 MP corpus JPEG renders in a few MB; an 8000×6000 JPEG renders fine; oversized
  PNGs are *refused* rather than OOM-ing the process. The server could not be made to exhaust memory.
  (§ 2.1 is the flip side of that ceiling being too tight and shared.)
- **The pairing rate limiter.** Exactly five `POST /pair` per minute, then `429` with a correct
  `Retry-After: 45`. The bucket table is capped and pruned. `X-Forwarded-For` is correctly never
  consulted.
- **Browse on a huge tree.** 60 folders × 4000 files = 240,000 files with `counts=true` took 0.32 s and
  the server stayed instantly responsive (`ping` in 3 ms) during and after.
- **Wedging the PC client's link after an abort.** `Gated`/`GatedConnect` (`SessionLink.cs:73-88`)
  release the flag in a `finally`, `EnterBusy` is deliberately outside the `try`, and `Rm2Http.Send`
  never throws — every path returns a `Reply` (`Rm2Http.cs:90-126`). No sequence leaves `_busyFlag`
  stuck.
- **Double-submit from the keyboard.** Busy is set synchronously before the first `await`
  (`RankCoordinator.cs:537`), and a held key is covered by `_consumedWhileHeld` (`RankModel.cs:57-62`),
  cleared only on KeyUp — OS auto-repeat cannot get a second vote through.
- **Certificate pinning, on both clients.** On the phone the pin *is* the trust decision rather than a
  check layered on platform validation, which is the only arrangement that works for a self-signed
  certificate; `OkHttpRm2ClientPinningTest` proves the handshake fails **and** `server.requestCount == 0`,
  so the bearer token never leaves the device. On the PC, `PinnedHandler.Create` compares the DER's
  SHA-256 with `FixedTimeEquals` and a mismatch is `Fatal`, never retried (`PinnedHandler.cs:51-71`,
  `SessionLink.cs:645, :503`), verified against a real server by `N11_PinMismatchNeverRetried`.
- **A double undo from the phone's unguarded cancel retry.** `cancel()` carries no token, so its retry
  really is unguarded — but the server's undo is one level and self-clearing
  (`SessionRegistry.cs:1624-1625`), so a retry of an undo that already landed is `nothing_to_undo`, and
  the phone's message for that is accurate.
- **Wrong bytes in a pane on the phone, from the client's own caches.** Keyed on the server's verbatim
  `links.still` URL (which already carries `v=<mediaVersion>`) all the way down; Coil's disk cache is
  off; no id-derived key exists that two files could share. (§ 1.2 defeats this from the server side,
  which is why it is a server finding, not a phone one.)
- **The rename plan colliding with names already in the folder.** The per-run four-hex suffix is drawn
  against every name in the plan *and* every media file the folder currently lists
  (`RenameEngine.cs:155-175`), and the disjointness it buys is re-checked when the journal is read back.
  `000001.jpg` from a previous Rank Master 2 rename is ordinary input, as intended.
- **Stale lock files.** After `kill -9` the `.rankmaster.lock` file remains in the folder, which is what
  the design says: the OS released the handle, so the next open succeeds. A clean close deletes it.
- **The PC client's fast start.** The claim holds. Nothing native but Avalonia's own touches the first
  frame; `Composition.Build()` is constructors plus two `File.Exists`; `new LibVlcBackend()` does not
  pull in LibVLCSharp; `ConnectAsync` is fired from inside `RequestAnimationFrame`, after `first_frame`;
  a stills folder never initialises VLC at all. `PublishReadyToRun=true`, `PublishTrimmed=false`,
  `InvariantGlobalization=true` are the right settings for a cold start. § 3.6 is the one place work may
  have moved rather than gone.
- **Hygiene.** `grep` for `TODO`, `FIXME`, `HACK`, `XXX` and `NotImplementedException` across every
  production source file in `src/`, `pc/src/` and `android/app/src/main` returns **nothing**.

---

## Appendix — the throwaway tests

Kept outside the repository at
`/tmp/claude-1012/-var-www-bormin/fe4dff48-a725-4cf2-9d43-ad14f7dbf86e/scratchpad/proof/`. Each drops
into `tests/RankMaster2.Audit.StateMachine/` and runs with
`dotnet test tests/RankMaster2.Audit.StateMachine/RankMaster2.Audit.StateMachine.csproj --filter ZzScratch`.

| File | Proves | Result |
|---|---|---|
| `ZzScratchWedgeTests.cs` | § 2.2 — a move that throws `UnauthorizedAccessException` leaves the rename `running` for ever; cancel cannot clear it and votes are refused | **fails** (correctly) |
| `ZzScratchRecoverTests.cs` | § 2.2's other half — a restart after that wedge recovers every rating and finishes the moves | **passes** |
| `ZzScratchRaceTests.cs` | § 1.5 — building the media view races `Drop`; `NullReferenceException` in 19 ms | **fails** (correctly) |

The forks that audited the PC client, the phone and the server's security layer wrote and deleted their
own scratch files; all three suites were re-run clean afterwards, and I confirmed the final counts
myself. One of them ran a single read-only `git status` against the brief's instruction; nothing was
mutated.
