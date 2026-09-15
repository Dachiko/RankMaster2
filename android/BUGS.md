# Open bugs — the Android client

Found by the owner, ranking his own library. Collected here rather than fixed one at a time, at his
request. Diagnosis is written down while it is fresh; **a diagnosis is a guess until the fix works
on his phone**, and where I am guessing it says so.

Fixed items are deleted, not archived — git remembers.

---

## 1. Video sometimes has the wrong proportions; rotating twice fixes it

**Seen:** a video pane draws the picture stretched or squashed. Turning the phone and turning it
back puts it right.

**Read:** that "rotating fixes it" is the whole clue. Rotation forces a fresh layout pass, so the
geometry is correct once something re-measures — meaning the first measurement happened before the
player knew the video's shape.

The pane measures itself, hands the size to a `PlayerView` inside an `AndroidView`, and the
`PlayerView` sets its own aspect ratio when the video size arrives. But the video size arrives
*after* the first frame is decoded, which is well after Compose has measured the pane. Nothing tells
Compose to look again, so the view keeps the box it was given.

**Likely fix:** take the ratio from the player rather than from the view — listen for the video size
(`onVideoSizeChanged` gives width, height and the pixel-aspect ratio) and apply it as an
`aspectRatio` modifier on the pane, so the *Compose* layout owns the shape and re-measures when it
changes. That also removes the need for `AspectRatioFrameLayout` to do it.

**Watch for:** anamorphic files, where the pixel aspect ratio is not 1. `onVideoSizeChanged` reports
it separately and it must be multiplied in, or the fix produces a subtler version of the same bug.

---

## 2. The cancel notch does not touch the edge, and is drawn facing the wrong way

**Seen:** in portrait, the tab floats a little away from the right edge, and the notch points the
wrong way.

**Read:** two mistakes in the same few lines, and I am fairly confident of both.

*It floats* because the notch is given `windowInsetsPadding(WindowInsets.safeDrawing)`, which is
correct for a control that must stay clear of the system bars and wrong for one whose whole design
is to grow out of the screen edge. The inset pushes it inward and leaves the gap.

*It faces the wrong way* because the shape is drawn with its open base along the bottom and then
turned with `rotate(90f)`. Rotating the content clockwise sends the bottom edge to the **left**, so
the tab grows out of the left edge while sitting on the right one. It wants `-90f`.

There is a third problem hiding behind those two: `rotate` turns the content, not the layout box, so
the rotated tab is still occupying a 112 × 28 box. Its footprint is therefore wrong even once it
faces the right way, and it will not sit where it appears to. The right-edge variant needs its
width and height swapped, or a draw that is genuinely vertical rather than a rotated horizontal one.

**Probably right answer:** draw the path in the orientation it will be used in, rather than drawing
one and rotating it. Two short paths are easier to get right than one path plus a transform, and
this is the third attempt at this shape.

---

## 3. Back leaves the app while browsing folders

**Seen:** walking down into folders, back does not come up a level — it leaves the app.

**Read:** not a subtle one. The back gesture was handled on the ranking screen and never on the
browser. Nothing intercepts it there, so Android does the default thing and finishes the activity.

**Fix:** a back handler on the browse screen, with the same shape as the ranking screen's — up a
level if there is a parent, and only at the drive list does back mean leave. The view model already
has `up()` and already knows whether it is at a root, because § 10.15's `parent` tells it; nothing
new has to be worked out, it just has to be wired to the gesture.

Five lines. It can be pulled out of the batch and shipped on its own if the browsing is in the way.

---

## 4. A vote is too easy to cast by accident near the screen edge

**Asked for:** a margin around the border — about 10% — where a tap does not vote.

**Why it is right:** a wrong vote is the one mistake on this screen that costs something, and the
edge of the screen is exactly where a hand rests while holding a phone. The seam already has a dead
band for the same reason; this is the same argument applied to the outside.

**The shape of it:**

- A margin down each side, and across the top and bottom, where a tap does nothing. 10% of the
  screen's width on the left and right, 10% of its height top and bottom, so it stays proportional
  in either orientation.
- It leaves the middle ~80% of each pane live, which is where the picture being judged actually is.
- It stacks with the seam band already there, so each pane's live area is bounded by the outside on
  three sides and the seam on the fourth.

**A distinction worth keeping:** the margin should kill *taps*, not long presses. An accidental vote
is a tap; nobody long-presses by accident, and the pane menu is the one thing a person might
deliberately reach for in a corner. So: no votes near the edge, but the menu still opens anywhere.

**Open:** 10% is the owner's guess and mine is no better. It should be a single number in one place,
easy to move after a session with it — and worth re-checking in landscape, where the thumbs sit on
the left and right edges rather than the bottom.
