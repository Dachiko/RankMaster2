# Rank Master 2 — Android client

**Status: proposed.** The server is complete and audited (`SERVER_PLAN.md` phase 4); this document
covers the phone.

Goal: rank a folder on the PC, start to finish, from the phone, with the PC screen off.

`SERVER_SPEC.md` is the contract and this client implements it — it does not extend it. Where this
document and `SERVER_SPEC.md` disagree, the spec wins. Where the client wants something the server
does not offer, the answer is to change the spec first, not to invent it on the phone.

---

## 1. Decisions, settled before any code

| Question | Answer | Why |
|---|---|---|
| Platform | **Native Android, Kotlin + Jetpack Compose** | the two hardest parts of this client are video decoding and pinning a self-signed certificate at runtime; both are first-class native and awkward through any cross-platform layer |
| iOS | **out of scope** | owner's decision, 2026-09-15 |
| `minSdk` | **29** (Android 10) | AV1 does not decode below it. The target device is Android 14 |
| `targetSdk` / `compileSdk` | 35 | current |
| Distribution | **sideloaded APK**, signed with one stable key | no store, no accounts, one user |
| Discovery | **none** | the pairing QR carries host, port and fingerprint; there is nothing left to discover |
| Recovering from a changed PC address | **not built** | owner's decision: does not happen in this setup. If it ever does, re-pair from `rm2ctl` |
| Shared code with the server | **none** | all ranking logic is server-side; the phone is a remote control |

### 1.1 Deliberately absent

Not in v1, and none of them are oversights:

- No rename-by-rank. The server does not expose it and will not (`SERVER_SPEC.md` § 1.1).
- No ratings on the ranking screen. Showing μ, σ or a score biases the vote being collected
  (`SERVER_SPEC.md` § 9.3). The match strip is the only rating signal, exactly as on the desktop.
- No offline mode. Without the server there is nothing to rank; the app shows that it cannot reach
  the PC and stops.
- No multi-server, no multi-device, no accounts. One phone, one PC, one token.
- No local library, no import, no upload. The phone never sends media anywhere.
- No background work, no notifications, no analytics.

---

## 2. Architecture

```
app/            Compose UI, navigation, screen state holders
  pairing/      QR scan, manual entry, the pairing exchange
  browse/       roots + folder tree, folder picking
  rank/         the ranking screen: pair, gestures, actions, match strip
  settings/     server details, forget this device, about
net/            one OkHttp client: pinning, bearer token, error envelope, retries
  Rm2Client     every endpoint in SERVER_SPEC.md § 10, one method each
  Snapshot      SessionSnapshot and friends (§ 9), kotlinx.serialization
  Rm2Error      the § 4 envelope and the § 5 code, never a raw HTTP status
media/          Coil for stills, Media3 for video, both over the same OkHttp client
store/          token + server details in EncryptedSharedPreferences
```

| Concern | Library | Note |
|---|---|---|
| UI | Jetpack Compose + Material 3 | one screen carries the app; declarative suits "snapshot in, screen out" |
| HTTP | OkHttp + kotlinx.serialization (Retrofit optional) | one client instance, so pinning and auth cannot be bypassed by accident |
| Stills | Coil 3, over that same OkHttp client | inherits the pin, the token and the HTTP cache for free |
| Video | Media3 / ExoPlayer | MP4, WebM, MKV native; HTTP Range is what the server already serves |
| QR | CameraX + ML Kit barcode scanning | offline, on-device |
| Secrets | EncryptedSharedPreferences (Keystore-backed) | one token, one fingerprint, one base URL |

**One HTTP client, no exceptions.** Coil and Media3 are both configured with the same OkHttp
instance. A second HTTP stack anywhere in the app is a second place the certificate pin and the
bearer token can be forgotten, which is exactly how a "we pin our certificate" app turns out not to.

---

## 3. The hard problems, and how we solve them

### 3.1 Trusting a certificate nobody signed

The server makes its own certificate. There is no CA, so the phone must pin it — and the fingerprint
arrives at runtime, in the QR code, not at build time.

- A custom `X509TrustManager` that accepts exactly one SHA-256 fingerprint of the peer's DER
  encoding, which is the same string `GET /ping` reports.
- OkHttp's `CertificatePinner` is **not** enough on its own: it validates the chain first, and a
  self-signed chain fails before the pin is ever consulted. The trust manager is the mechanism; the
  pin is the policy.
- Pin **before** sending the pairing code. The code is a bearer secret: handing it to an unverified
  peer hands it to whoever answered (`SERVER_SPEC.md` § 10.1.1).
- A fingerprint mismatch is a hard failure with a plain-language screen — never a "continue anyway"
  button. There is no legitimate reason for it on a LAN, and the one illegitimate reason is the
  whole attack.
- Cleartext HTTP is off in the manifest; `usesCleartextTraffic=false`.

### 3.2 Never voting twice

This is already solved by the server and the client's whole job is not to break it
(`SERVER_SPEC.md` § 13.3):

- Every action carries the `pairToken` of the pair it acted on, and a `clientRequestId`.
- A request that times out is retried **with the same token**, never with a fresh one. If the
  original landed, the retry returns `409 stale_pair_token` **carrying the current snapshot**, and
  the client simply adopts it. One round trip, no polling, no double vote.
- The client holds the in-flight token until it has a definite answer. Resyncing first and then
  retrying is the one sequence that double-votes; it must be impossible by construction, so the
  retry lives inside the action call and not in the UI.
- Consequence for the UI: the vote gesture is disabled until the action resolves, but the *displayed*
  pair does not flicker — the new pair arrives in the same response.

### 3.3 Images

The server hands out `links` that are already encoded and already carry `v=<mediaVersion>`
(`SERVER_SPEC.md` § 9.3). The client uses them verbatim and builds no media URLs of its own.

- Request width from the fixed list (360/540/720/1080/1440/2160) — the nearest at or above the pane
  width in pixels. Anything else is `400 unsupported_width`, deliberately.
- The server sends `ETag` and `Cache-Control: private, max-age=31536000, immutable`, so an OkHttp
  disk cache of a few hundred MB means each picture crosses the network once, ever.
- Prefetch `warmPairs` at the same width, after the current pair is on screen. Warm pairs have no
  token, must never be acted on, and may never become current (§ 9.5). Prefetching is not an
  impression.
- `sizeBytes: null` on a `MediaRef` means the file vanished under the session. The pane shows a
  "file is gone" state whose only offered action is to discard that side — which the server turns
  into `drop_missing` (§ 10.8).

### 3.4 Video

The corpus is AV1/MP4, VP9/WebM, H.264 in MP4/MKV/MOV, MPEG-4 in AVI. Media3 covers everything
except AVI comfortably, and AVI is the fallback case:

- Media3 with the default extractors; playback is `links.video`, which streams over Range.
- If a real `.avi` in the owner's library will not play, the fallback is libVLC for Android behind
  the same player interface. Decide that on evidence from his actual folders, not in advance.
- There are no poster frames and no still for a video, by decision (`SERVER_SPEC.md` § 12.1). A video
  pane shows the player, not a thumbnail.
- A video folder ranks with the same gestures; only the pane content differs.

### 3.5 Nothing leaks into the app switcher

Leaving the app must leave black behind, not the pair that was on screen. Android keeps that
thumbnail after you go, so without this a glance at someone's recent apps is a glance at whatever
they were ranking — and over a session, at the library.

`setRecentsScreenshotEnabled(false)` (Android 13+) blanks the switcher and does nothing else.
`FLAG_SECURE` blanks it too but also blocks screenshots, screen recording and mirroring — which
during development would block the owner from sending a screenshot of a layout problem, the only
way this app gets looked at on a real screen. So: the narrow API where it exists, `FLAG_SECURE`
only below Android 13 where nothing else will do it. The target device is Android 14.

**If full screenshot blocking is ever wanted**, it is one line — but it is a deliberate trade, not
a default, and it costs the ability to report a bug with a picture.

### 3.6 Layout

Two panes, and the phone is usually portrait:

- **Portrait:** stacked, top and bottom, each half the screen. "Left" is the top pane and the UI says
  so; the API's left/right is never shown to the user.
- **Landscape:** side by side, which matches the desktop.
- Tap a pane to vote for it. A long press opens that pane full-screen for a proper look, with no
  action attached — looking is not voting.
- The bottom bar carries skip, undo, and an overflow with discard / special / save / close.
- Discard and special act on **one side**, so they are offered as a per-pane control, not a bar
  button — a bar button would have to ask "which one?" every time.
- The match strip renders `cues` oldest-first, up to 10 (§ 9.1), exactly as the desktop does.

### 3.7 What the client must never do

Collected in one place because each of these is a way to corrupt a ranking that the server cannot
defend against:

1. Never compute a rating, a score or a pick. The server owns all of it.
2. Never act on a warm pair, and never send `pairSeq` in an action body.
3. Never retry an action with a token obtained after the failure.
4. Never build a media URL by hand when `links` has one.
5. Never treat a media `404` as a transport error — it means "this id is gone; refresh the snapshot".
6. Never show μ or σ on the ranking screen.

---

## 4. Screens

| Screen | What it does | Contract |
|---|---|---|
| **Connect** | scan the QR, or type host/port/code by hand. Pins, then pairs, then stores the token | § 10.11, § 10.1.1 |
| **Folders** | roots, then a folder tree with per-folder media counts; folders that cannot open are greyed out, not hidden | § 10.14, § 10.15 |
| **Rank** | the pair, the gestures, the match strip, progress | § 9, § 10.2 – § 10.10 |
| **Exhausted** | "nothing left to rank here" with save / close / pick another folder | § 7.1 |
| **Settings** | server address and fingerprint, "forget this device" (revokes the token), version | § 10.12, § 10.13 |

Browsing passes `counts=false` on anything that looks like a slow tree and fills counts in lazily;
a root with hundreds of children is one directory enumeration per child (`SERVER_SPEC.md` § 10.15).

---

## 5. Phases

**Phase 0 — me, alone.** Android SDK into `~/Android` (no root needed), Gradle project, one stable
signing key kept out of git, an empty app that builds and installs. Proves the toolchain before any
agent touches it.

**Phase 1 — the network layer, alone.** `Rm2Client`: every endpoint, the § 4 error envelope, the
pinning trust manager, the token store. Tested against the **real server** running on this box, not
against mocks — the server is right here and mocking it would only test my idea of it.

**Phase 2 — three agents in parallel, each owning its folder.**

| Agent | Owns | Deliverable |
|---|---|---|
| Pairing | `app/pairing/` | QR scan, manual entry, the pin-then-pair sequence, token storage |
| Browse | `app/browse/` | roots, tree, counts, the folder-not-rankable states |
| Rank | `app/rank/` | the pair screen, gestures, actions, retry protocol, match strip |

Media (Coil + Media3 wiring) lands with Rank; splitting it would put two agents in one screen.

**Phase 3 — integration, me.** One build, one flow, end to end against the real server.

**Phase 4 — adversarial review.** One agent on the retry/state protocol (the double-vote surface),
one on the certificate and token handling. Same shape as the server's phase 4, which found three real
faults — including one arbitrary-file read — and is the reason that phase exists.

---

## 6. Acceptance gate

The client is done when, on the owner's own phone, against the server on his PC, with the PC screen
off and the desktop app never launched:

1. Pair from cold by scanning the QR — no typing, no manual trust step.
2. Browse to a real folder and open it.
3. Rank at least 50 pairs: vote, skip, discard, special, undo, save.
4. Every picture appears at display size within a fraction of a second on second sight (cache proof).
5. A video folder plays both a modern and an older file.
6. Turn the phone's Wi-Fi off mid-vote and back on: the app recovers to the correct pair, and
   `rankmaster_db.json` shows no double-counted vote.
7. Open the same folder in the desktop app afterwards and see the ratings intact.

Point 6 is the one that matters, and point 7 is the compatibility contract — the same one the server
had to meet.

---

## 7. Risks

| Risk | Mitigation |
|---|---|
| AVI or an exotic codec will not play | evidence first from the owner's real folders; libVLC behind the same player interface if needed |
| No emulator on this box (the account cannot use virtualisation) | build and unit/Robolectric-test here; every real check happens on the owner's phone, which is where the certificate, the camera and the decoders are honest anyway |
| A second HTTP stack sneaks in via a library | one OkHttp instance is injected everywhere; a review item in phase 4 |
| Sideloaded updates refuse to install | one stable signing key, kept off GitHub and backed up; losing it costs one uninstall |
| The retry protocol is subtly wrong | it is the phase 4 review's first target, and the server answers a stale token with the full state precisely so this stays simple |

---

## 8. Delivery

Debug and release APKs are built on this box. The owner gets each build as a Telegram attachment
(well under the 50 MB bot limit) and the same file at a static HTTPS URL as a fallback. GitHub
Releases is available if versioned history is ever wanted; it needs a one-time `gh auth login` by the
owner, because the repository's deploy key can push code but cannot publish a release.
