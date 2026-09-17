# Changelog

Newest first. This is a wiki-facing merge of the Unison.Socket architecture PR, the product “What’s New” notes, the Socket Broker work, v6.9, and v6.8. It is not a substitute for git history.

---

## A toast for a poll that was nowhere in the app

Found by comparing `MessageRenderReader` against `BackgroundMessagePreviewEngine`, which cannot
reference Core and so carries its own reading of an envelope — and turned out to be the more
complete of the two.

Polls come in three proto fields, `PollCreationMessage` and `V2` and `V3`, and a poll sent by a
current WhatsApp arrives as V3. Core read only V1. So a poll received today fell off the end of the
cascade as an unknown type and was never drawn anywhere: not live, not after a sync. The background
task reads all three, so it still raised the notification — the user got a toast and then found
nothing in the conversation it pointed at.

Same shape, smaller blast radius, for `LiveLocationMessage` and `ContactsArrayMessage`: known to the
background reader, unknown to Core, so sharing live location or a batch of contact cards produced
the same silence.

One divergence is left on purpose. For an envelope neither side recognises, the background reader
falls back to `[Message]` and Core draws nothing; which of the two is right is a product call and
is not made here.

---

## Polls, shared contacts and locations disappeared from the conversation after a sync

Not blank — gone. The same short classification behind the quote bug turned out to sit in a much
worse place as well: `TryGetListableContent`, the gate that decides whether a message from a history
sync becomes a row in the conversation at all, and whether it can be the line the chat list shows.

It asked `ExtractContent`, which knows plain text and the four media kinds. A poll, a shared
contact, a location or a call log came back as empty text of kind `Text`, `HasRenderableContent`
said there was nothing to draw, and the message was dropped: no timeline row
(`HistoryMessageBuilder` returned null) and not even a candidate for the preview
(`FindNewestListable` skipped past it). So sending a location left the chat list still showing
whatever was said before it, and after a reinstall or a resync the location itself was not in the
conversation either. Live, all four showed normally — they were there until the next sync.

Both paths now classify through `MessageRenderReader`. The label these carry is still the raw
`[Poll] …` / `[Contact] …` / `[Location]` the live path has always shown, in English regardless of
app language; giving them proper `ChatPreviewKind` values with localized labels is worth doing and
is not done here.

The same comparison settled a document sent with a caption, where the two paths disagreed about
what to show: the sync used the caption, live used the file name. The caption is what the sender
wrote, so that is what both use now, falling back to the file name when nothing was written.

---

## Quoting a poll, a contact or a location came back blank after a sync

History sync read quotes through `HistorySyncContentFilter.ExtractContent`, which knows plain text
and the four media kinds and nothing else. A quoted poll, contact, location or call log fell through
it as empty text of kind `Text`, so the strip on the bubble drew nothing at all. The live path, on
the same quote, showed its label — which of the two you saw depended on whether the message arrived
while the app happened to be open.

Both paths now read through `QuotedContext`, so a quote means the same thing however it got here.

---

## Quotes arriving live were stored truncated at 50 characters

Reading the context info moved to `QuotedContext` in Core, and putting the live path beside the
history-sync one showed they did not agree.

The live path normalized the quote with `ChatPreviewNormalizer.Normalize`, which collapses to a
single line and caps at 50 characters — the helper says on that method not to use it on text that
gets persisted. But this text is persisted, as `HistoryMessage.QuotedBody`, and the strip that draws
it normalizes again anyway (`ChatMessageViewModel.QuotedStripText`). So the cap changed nothing on
screen and stored a shortened quote for good, while the same quote arriving through history sync
was stored whole.

It now uses `NormalizeBody`, like the sync path. The one-line cap stays where it belongs, in the
strip that draws it.

---

## Panels and overlays that kept listening after they left the screen

A sweep of the view code-behind for subscriptions with no matching release, since the views are
transient and most of what they subscribe to is not.

Five info controls (`ChatDetailInfoControl`, `ChatDetailUserInfoControl`,
`ChatDetailGroupInfoControl`, `ChatDetailGroupMemberInfoControl`,
`ChatDetailGroupMemberInfoPane`) hooked `ChatDetailInfoViewModel.PropertyChanged` and only
unhooked when the `DependencyProperty` swapped — never at unload. Closing the pane or leaving the
conversation left a handler pointing at a visual tree that went on laying itself out.

`ImageViewerView` unsubscribed at unload but kept the view model, and the full-screen bitmap it
holds, which is the app's memory peak. Its sibling `VideoViewerView` already released everything
through the setter; the image path now does the same, and picked up the `ReferenceEquals` guard it
was missing, so re-assigning the same view model no longer reloads the image and drops the zoom.

`ChatDetailView` also never released the `LayoutStates` visual state group it hooks on load, and
`ChatListView.ViewModel_OpenChatRequested` — raised by the view model rather than by XAML — waits up
to a quarter second before writing to the list, by which time the view can be gone.

---

## Every conversation with a voice note left a media player behind

`ChatDetailView` creates a `MediaPlayer` on the first voice note and subscribes to four of its
events. Leaving the conversation paused it and cleared the source, and that was all: no
unsubscribe, no `SetMediaPlayer(null)`, no `Dispose`, and the field kept pointing at it.

Its `CommandManager` and transport controls keep it registered with the system, and those four
handlers keep the page alive through it, so each player held a whole `ChatDetailView` — with its
message list — past navigation. On a phone this reads as the app getting heavier the more
conversations you open, until the OS kills it, with the lock-screen controls still driving the
oldest player.

Release is now separate from stop, because stopping also happens when a video takes over and the
next voice note reuses the instance. `VideoViewerView` already did this correctly; the audio path
now mirrors it.

The `MediaEnded` and `MediaFailed` handlers also ran unguarded, while their two siblings in the same
file were wrapped. These callbacks arrive on a Media Foundation thread where nothing above catches
what escapes, so an exception ended the process — on the failure path, precisely when something had
already gone wrong.

---

## Sending in groups could unlink the device

A `LocalSettings` value stops at 8192 bytes and throws past it, and `AuthStore` kept the whole
`AuthState` in one. The keys are about a kilobyte, but the same value carried `SenderKeyMemory` —
per group, every participant device that already holds our sender key. A 50-member group is around
2 KB, so a few active groups reached the ceiling.

The write that overflowed it was the group send: relaying emits `CredsUpdate`, which saves the
credentials. From the first throw they stopped being saved at all, and because a load that fails
returns null, the next cold start read that as "never paired" and built a fresh state over the top.
The account unlinked itself, and the only trace was a `Debug.WriteLine` that release builds do not
have.

The memory moved to its own file, written after the credentials and allowed to fail quietly — it is
a cache, and losing it costs one re-send of the sender key. Installs written before the split are
migrated on the next load.

The load path now separates "no account saved" from "an account is saved and I could not read it".
In the second case it refuses to write an unregistered state over the stored one, so a read that
fails costs a session rather than the device link. A pairing the user actually completes is
registered, and still replaces what is there.

---

## A failed connection attempt left its socket open behind it

`ConnectAsync` subscribes to the transport and then opens it, sends the client hello and waits for
the handshake. Any failure after that point — a handshake timeout, a rejected payload, a parse error
— propagated straight out, which is the one thing it could not do: the socket was already open and
those two handlers were still attached.

The host reads that exception as "connect failed" and builds a new session, so the abandoned one
stays behind holding a live connection that nobody will ever close, still receiving frames. On a
phone reconnecting across a patchy network this accumulates, and the symptom is not an error message
but the app getting slower and heavier the longer it stays on.

Every exit from the connect path now goes through the existing close, which unsubscribes, fails the
pending waiters and closes the transport. It was already idempotent, so the transport dropping
underneath us on the way out is not a second close.

---

## Deleting a conversation left it asking the server for its messages

A message that fails to decrypt is remembered so the app can ask the account to send it again. That
register was cleaned up in two places — deleting a chat, and merging a conversation onto its
canonical address — and both did it by removing the key inline, on the UI thread, while the incoming
pump reads and writes the same dictionary from a background thread under a lock those two did not
take. A plain dictionary mutated from two threads at once fails rarely and never in the same place
twice.

They also dropped the pending entries without cancelling the resends already scheduled for them, so
a timer stayed alive to ask the server for a message belonging to a conversation the user had just
deleted.

Both now go through one path that takes the lock, cancels the scheduled work, and forgets the
in-flight request ids along with it.

---

## A message that arrived with the app minimised was announced but never counted

The toast fired and the conversation showed nothing new. Dismiss the toast, or miss it, and the
message was gone as far as the list was concerned — no badge on the row, no count on the tile.

Two lines a few dozen apart decided this, and they were answering the same question differently.
The badge asked whether the conversation was the one open in the detail view. The toast asked
whether it was open *and* the window was visible. Nothing closes the open conversation when the app
goes to the background, so with a chat open and the app minimised the first said "the user is
looking at this, do not count it" while the second said "they cannot see it, tell them". Each line
is defensible on its own, which is why this survived: you have to read both together to see that a
message can fall through the gap between them.

It is one decision now — `IncomingAttention` (11 tests) — returning both answers from one reading of
"is the user actually looking at this". The test that matters is the invariant rather than the
cases: unless the message is already on screen, it is both counted and announced. Doing neither is
how one goes missing.

The same rule had two more copies in the offline replay path, with the same omission, and they are
worth more than they look: a replay runs when the app reconnects, which is precisely when it tends
to be in the background with a conversation still open behind it.

---

## Muting a chat from the info panel deleted its pin

Pinned and muted conversations came back from a sync with their icons gone. The preview table was
the obvious suspect and is innocent — it has no pin or mute columns at all. Those live in a separate
table, and the damage was in how it was written.

Every caller wrote the whole row: status, tile pin, chat pin and mute together. That means each one
needed a current copy of the three fields it was not changing, and the list view read them first
while the chat detail and info panel did not. So muting a conversation from the info panel stored
"not pinned" over a real pin. Nothing looked wrong at the time; the pin was gone from disk, and the
next read restored the absence exactly as it found it.

Writes now name one field each, through the read-modify-write that was already there. App-state
writes only what the mutation actually mentioned, which is what it already did in memory.

A second cause sat next to it: warming the store cleared the cache before refilling it from disk, and
the read path consults the cache only. Anything hydrated inside that window was told nothing was
stored — and since two different callers warm the store, the window opened more than once per launch,
including during a sync, which is the moment the list is being rebuilt. The cache is filled first now
and stale entries dropped afterwards.

The read rule moved to `ChatLocalStateApply` (10 tests) and pins the distinction the old code made by
accident: nothing stored says nothing about pin or mute, because either can arrive before a row
exists — but it does say "no tile", because a tile is on the Start screen or it is not.

---

## Searching for a contact with brackets in the number found nobody

The new-chat box takes whatever the user types and hands it, near enough untouched, to the contact
lookup: `'+'`, spaces and hyphens were stripped, and nothing else was. A number written the way most
people write one — `(11) 99999-9999` — therefore reached the server as `+(11)999999999`.

The failure is quiet in the worst way. The query is well formed, the server answers honestly that no
such account exists, and the user is told the number is not on WhatsApp. Nothing in the app is aware
that it asked the wrong question. The same digits pasted without brackets work, which makes it look
like the contact's problem rather than ours.

`PhoneNumberHelper` next door already read numbers properly, so this was a rule written twice where
only one copy was right. `ContactLookupNumber` (Core, 20 tests) is now the single answer to "what
number do we ask about", covering both callers — the search box and the background name refresh —
and returning *why* an address was skipped instead of a bare null, so the log says `SelfAccount` or
`NotDirectChat` rather than leaving the reason to be guessed.

One ordering detail is pinned by a test because it is easy to write backwards: the device suffix is
cut before the digits are read. Taken the other way round, `5511988887777:12` folds into
`551198888777712` — a well-formed query about nobody, i.e. the same silent failure in a new place.

Two behaviour changes fall out of the merge. Searching for your own number no longer goes to the
server (it still matches locally); and a number typed with a `00` international prefix now has it
dropped, which is what the rest of the app already did.

---

## A participant who deleted their photo went on showing it forever

`ChatAvatarOutcome` (phase 3.1c) records how a picture lookup ended, and the whole reason it exists is
that "has no photo" and "could not reach it" look identical on screen while meaning opposite things
about whether to ask again. Group participants had their own copy of those rules, and it differed on
exactly that outcome: a confirmed miss stamped the answer but kept the old url.

That is not cosmetic, because `GroupMember.NeedsAvatarLookup` returns false as soon as there is a url.
So the picture a participant deliberately deleted stayed on screen, and the one mechanism that would
have revisited it was switched off by the very field that should have been cleared. The model's own
documentation states the intended contract — *"Empty AvatarUrl plus this stamp means 'asked, nobody has
a picture'"* — which the applier never produced once a photo had been cached.

Rather than fix the copy, the two were merged. `IAvatarSubject` names the four fields as the group they
are, `ChatItem` and `GroupMember` both carry it, and `ChatAvatarOutcome` now serves both. The same
person seen from the chat list and from a group roster can no longer disagree about whether they have a
picture.

---

## `AliasPairPolicy` — the identity guard, which existed twice and did not agree

The alias table is what decides that two addresses are the same person, so a wrong entry there does not
produce a wrong label — it merges two conversations. The guard against the worst version of that (a pair
filing some contact under the user's own identity, after which every message from that contact lands in
the self chat) was written in two places: the live path in `TryRecordAliasMapping`, and
`IsSelfPoisoningAliasPair`, added later for the startup restore after a stored bad pair was found coming
back on every launch.

The two did not agree, and the difference is a false positive in the newer one. Both allow the one
legitimate pair that reaches the guard — the user's own LID and phone address pointing at each other —
by looking for the reverse entry already in the table. The live path reduces a dotted LID carried on the
phone domain (`123.45@s.whatsapp.net`) to the plain `123@lid` the table files it under before comparing.
The restore path compared the dotted form as it stood, so it never matched, and discarded the user's own
legitimate alias on every launch that had one stored that way.

Both now call `AliasPairPolicy`. The pair-shape check moved with it: a LID-like address sitting on the
phone domain is still a LID and cannot stand on the phone side, or the canonicalization that depends on
the phone address being canonical is inverted.

Testing the unified rule then found the same bug surviving on the other end of that comparison: only the
argument was being reduced, never the value read back from the table. Since the dotted form is accepted
on the LID side of a pair, it is also what can have been stored — so a legitimate self alias written
that way was still discarded on every launch. Both sides are reduced now.

**The guard became a gate.** Four places wrote straight into `JidAlias[...]` without consulting it — two
in the usync path and two in `WhatsAppService.cs` — which is the most likely route by which a poisoned
pair reached disk in the first place. Closing that is worth more than any refinement of the check that
runs afterwards, so it was closed first.

What kept those callers out of the policy was that none of them knows which side of the pair is the LID:
a usync answer, a contact record and two chat rows found to be the same person all arrive unordered.
Leaving each caller to work it out is how four of them ended up not doing it at all. `TryAcceptPair`
determines the orientation and answers in one call. Each caller keeps its own local condition — the row
merge still merges the rows and withholds only the alias; the mapped-LID path still only fires the first
time it sees an address.

### The same media forwarded five times was downloaded five times

Two halves, and only naming them both makes the fix true.

The cached file is named after the content hash — image, video and document already were; audio used
the message id, so five forwards of one voice note wrote five identical files to eMMC. That is now
uniform.

Naming alone changes nothing about the network, though, because the file name is chosen *after* the
download. The only cache check was `message.ImageUri` and its siblings — a per-row field, which a
forwarded copy has empty, being a different message describing byte-identical media. So every copy
was fetched again. There is now a lookup on disk before the download: named by content hash, the
answer is yes for every forward after the first.

Media the server does not address by content hash falls back to the message id, and the lookup
correctly finds nothing. For images the extension has to be guessed from the declared mime, since the
real one is read from bytes we have not fetched; a wrong guess misses the cache and downloads, which
is the safe direction for it to fail.

One guard came with it: `TryGetUriAsync` now rejects a zero-byte file. Existing is not the same as
usable, and a write interrupted by suspension — routine on Mobile — would otherwise be served forever.

The four `Ensure*AvailableAsync` routines each read the message's media fields by hand at the top,
which is how the copies drifted apart in the first place. That reading is now `MediaDownloadPlan` in
Core — url, direct path, key, expected hash, cache file base, mime — with 13 tests, including one that
pins the audio naming against exactly this regression.

What deliberately stayed behind is the part that genuinely differs per kind: whether a missing key is
fatal (a sticker marks itself failed and stays quiet; a video raises), what to do with the bytes, and
when to write the row.

### A second tap on the same video left the row without a thumbnail

`EnsureVideoAvailableAsync` checks the cache twice — once before taking the download lock and once
after waiting on it — and the two checks did not agree. The first built the poster if the row lacked
one; the second returned the URI bare. So of two taps on the same uncached video, whichever lost the
race came back with no thumbnail. Both now take the same path.

### Five error messages reached the user as garbled text

"A chave do áudio não está disponível" was stored in the source as "A chave do Ã¡udio nÃ£o estÃ¡
disponÃ­vel", and that is what the user read when a media download failed without a key. The same for
image, video, document, and "Falha ao guardar o vídeo".

The cause was a round trip: the files were written as UTF-8, read back as CP1252, and written as UTF-8
again. That is not the same as Latin-1, and the difference is exactly where punctuation lives — an em
dash is `E2 80 94`, and CP1252 reads `80` as a euro sign. So a repair through Latin-1 fixes the accents
and leaves every dash and arrow unrecoverable. Undone through CP1252 instead, repeated until it
converged, applied only to runs that decode as valid UTF-8 and never to a whole file.

Eighteen files were affected; most of the damage was in comments, where `—`, `→`, `↔` and the `──` rules
in `NewChatDialogViewModel` had turned into noise. The script is kept at `tools/fix-mojibake.ps1`.

### The connection dropped when a message and the keep-alive ping went out together

Each Noise frame carries the nonce it was encrypted with, and the counter advances per frame. The
send path encrypted *outside* the gate that serialises writes and only took the gate afterwards — the
comment right beneath it saying "Noise counters and framing are order-sensitive, so writes are
serialised" described an invariant the code did not keep. Two callers could encrypt as N and N+1 and
then cross the gate in the other order; the server fails to authenticate the frame it did not expect
and closes the socket. The keep-alive ping runs on a timer, so it meets a user's message eventually.
Encoding now happens inside the gate. `EncodeFrame` also takes the state lock that every other member
of `NoiseHandler` takes — without it, two callers could both prepend the intro header, or neither.

### A malformed length from the server could ask us to allocate two gigabytes

`BINARY_32` read a 32-bit length and cast it to `int` before checking it. Any length with the high bit
set became negative, and `CanRead` tested `position + length <= data.Length` — a sum that overflows to
negative for lengths near `int.MaxValue`. Both cases passed the guard and reached `new byte[length]`:
an overflow exception, or an attempt to allocate whatever the server named. On a 512 MB phone that is
the process being killed rather than a clean disconnect. The length now stays unsigned until it has
been checked, and the guard asks how much is left instead of where the read would end.

### A 255-digit attribute would have shifted the rest of the node

The packed-string length byte is seven bits of count and one of flag. The encoder accepted strings up
to 255 characters, whose packed length is 128 — the arithmetic set the flag bit that is supposed to
mean "odd length", the decoder read a count of zero, and 128 bytes were left orphaned in the stream.
The correct bound was already defined in this repo and never used: `PACKED_MAX`. Anything longer
encodes fine as a plain binary string.

### A self-test that tested nothing, on the critical send path

`VerifyDecryption` ran on every `pkmsg` send, hand-rolled a protobuf parser with no bounds checks, and
then never compared the plaintext it was handed against anything. It parsed, logged
"SELF-TEST COMPLETED", and returned. Deleted. The comment above the pkmsg serializer was worse than
useless: it claimed the generated proto had the field numbers scrambled and listed a numbering that is
neither what the generated code uses nor what the code below writes. Anyone "fixing" the serializer to
match that list would break every pkmsg. Replaced with the numbering actually in force.

### Padding drawn from the clock instead of the crypto RNG

`PadRandomMax16` built a fresh `System.Random` per call, seeded from a low-resolution clock, so two
messages encrypted in the same tick got the same padding length and the same bytes. It goes inside the
ciphertext and reveals nothing the message size does not, so this is hygiene rather than exposure —
but a non-cryptographic PRNG inside a crypto routine is a trap for whoever reads it next.

### Four background tasks reading the chat list that belongs to the UI thread

The chat list is an observable collection bound to the screen. Enumerating it while the UI thread
adds a row throws `InvalidOperationException`, and on a background task there is no caller to catch it.

`ChatAvatarPolicy` already knew this — it has a `SnapshotChatsAsync` whose comment says the list "must
not be enumerated from a background task" — and then reached for the live list anyway, from inside the
predicate that filters that very snapshot. The sibling-group lookup now takes its source as a
parameter, so each caller has to say where it reads from: the batch paths pass their snapshot, and the
one path that legitimately runs on the UI thread says so.

`ContactNameResolver` had the same rule written both ways in one file: `ResolveMissing` marshals to the
UI thread to copy the list, while `RefreshAsync` forty lines above walked it directly. Now both marshal.

### Two reconciles could each walk away holding the other's cancellation token

`ChatListViewModel` swapped its reconcile token source with a read and a write that were not a unit,
outside the gate the neighbouring queue uses. Two reconciles starting together could each end up with
the other's token: cancelling one stopped the wrong worker, and the one meant to stop carried on
writing previews over the fresher pass. The swap now happens under that gate and returns the token from
the local source rather than re-reading the field. The retire path also handles the source being
disposed underneath it, which is how a losing racer finds out it lost.

### A failed panel rebuild closed the app

`ChatDetailInfoViewModel.ScheduleRebuild` was `async void` without being an event handler. Nothing
awaited it, so a throw from the rebuild went to the synchronisation context, where there is no caller
to catch it — the app closed instead of logging a refresh that failed. Split into a scheduler and an
awaited half that reports.

### A cached answer about a contact's two addresses became permanent

`LidMappingStore` coalesces identical in-flight lookups so ten chats resolving at once cost one query.
The slot was published *after* the work started:

```
var task = RunAndReleaseAsync(...);   // finishes synchronously when the cache answers
inflight[key] = task;                 // and is filed here, after its own cleanup ran
```

A lookup served from cache never really awaits, so it completed — and ran the removal in its `finally`
— before that assignment put anything in the dictionary. The removal hit nothing, and the finished
task was then filed forever. Every later request for the same addresses got that one frozen answer:
not after the three-day life expired, not after a correction arrived on the wire, not after `Close()`.
The dictionary also gained a permanent entry per distinct set of addresses.

This is a hot path — session setup consults it on every encrypt — and from the second call per contact
onward the cache answers without awaiting, which is exactly the case that froze.

### The server told us our group list was stale and the refresh was thrown away

`GroupsUpdate` is emitted in three places. Two send `GroupUpdate`; the stale-list refresh sent the raw
`GroupMetadata` list. The batch reader casts with `as`, so the mismatch came back null, `TryGet`
reported "no such event", and the refresh vanished without a log or an exception. Sending still worked,
because the send cache was updated directly — but a group renamed while we were away only caught up on
the next history sync or a cold start.

### The offline queue kept draining against a dead socket

The queue checks a connection predicate between nodes, documented as: when the socket drops mid-burst,
abandon the rest rather than process against a dead connection. It was wired to `() => true`. So a drop
mid-replay meant the whole remaining backlog was handled anyway, each node trying to ack, each ack
throwing and being swallowed — a burst of wasted work during a reconnect, which is when there is least
to spare. The session exposes the real thing.

### Disposing a semaphore out from under the send that was holding it

The host gives a close three seconds before abandoning the session, so `Dispose` runs precisely when a
send is stuck on the send gate — that being why the close timed out. Disposing it under a waiter makes
that send's `Release` throw `ObjectDisposedException` on the way out, replacing the real error with a
lifecycle one. It is no longer disposed, and the transport handlers it left subscribed are now dropped.

### `GC.KeepAlive` does not watch a task

A fire-and-forget write of LID pairings ended in `GC.KeepAlive(stored)`, which keeps an object alive
and observes nothing. A failed write took the pairing with it in silence, and the contact went back to
showing a bare number. Same shape in the offline buffer's safety release, where a `ContinueWith` was
never unwrapped.

### The alias table was stricter in memory than on disk, and the conversion between them could throw

`JidAliasTable` holds the pairing between a contact's two addresses. It is saved to a file and read
back, and both ends of that file use case-insensitive dictionaries — but the table in memory was
ordinal. So two addresses differing only in case were two aliases in memory and one on disk, and one
of them disappeared on the next start without a word.

The sharper edge: `Snapshot()` copies into a case-insensitive dictionary, and that constructor throws
outright when both keys are present. `Snapshot()` runs inside the suspend write, where the throw would
take the chat catalogue down with it — losing a save at the one moment the system gives no second
chance.

The table is now case-insensitive too, which is what the file always was. The same mismatch was
dropping contact names on the way out: the filter deciding which names belong to a loaded conversation
built an ordinal set, while the alias save right below it built a case-insensitive one.

### A one-row save was reading the whole catalogue twice

`PersistChatCatalogSliceAsync` exists so clearing a badge does not rewrite everything — and then the
store it writes to ran two unfiltered `SELECT`s over the whole table before every write, however small.
On a few hundred conversations, each mark-as-read paid two full scans. Those reads are now scoped to
the addresses in the batch when the batch is small, and left as scans for a sync chunk, where the
address list would exceed what SQLite takes as bound parameters and scanning is cheaper anyway.

They also moved inside the write lock. They decide insert vs. update vs. skip, so reading them outside
let two concurrent writers judge against the same stale picture and one of them conclude "nothing
changed" about a row the other had already moved.

### Two catalogue saves enumerated the chat list from a background thread

Both were several awaits deep on a pool thread calling `Chats.ToList()`. That collection belongs to the
UI thread; enumerating it while the UI inserts a row throws, and in both cases the `catch` around it
would have turned that into a save that silently did not happen. They snapshot on the UI thread now,
like every other catalogue write in the file.

### The unread badge came back every time the app started

The list row is written to disk through a gate that asks whether anything changed. The gate asked
only about the tip — the newest message — and there was a second clause meant to cover the unread
count that read `tipNewer && prior.UnreadCount != model.UnreadCount`, which can never be true unless
the tip already moved. So it decided nothing.

Reading a conversation does not add a message to it. The tip stays exactly where it was, the row was
judged unchanged, and the badge on disk kept the old number — which is what the list hydrates from on
the next start. The same gate was swallowing the delivery tick advancing over an unchanged message
(Pending → Sent → Delivered → Read, all on the same tip, by definition) and a name resolving on a
quiet conversation.

The gate now asks about the strip around the tip as well, and the same-tip branch writes those
columns instead of only the lifecycle flags.

### A contact came back as two rows after a restart

`InsertOrReplace` rewrites the whole row, and the live catalog writer had no value for `LidJid` and
`PnJid` — only the history sync learns which phone number goes with which LID. It wrote them anyway,
as null. So the first ordinary write after a sync erased the pairing, and the hydrate that uses those
two columns to fold a contact's two addresses into one row had nothing left to match on.

They are now carried from the stored row when the incoming one cannot know them.

### Marking a conversation read could quietly send nothing

The tail of a conversation is filtered to messages that have an id, because a receipt names a range of
ids and one without an id cannot go in it. The count used to size the tail was taken from the list
*before* that filter and then skipped over the list *after* it, so every dropped message overshot the
end by one. With enough of them the tail came back empty — and both callers read an empty tail as
"nothing to do" and returned without sending anything. The user read the conversation and the other
side never got the blue ticks.

Now in `MessageTail`, with tests, including the case that used to come back empty.

### Two ViewModels held on to singletons they never let go of

`SettingsViewModel` and `DebugViewModel` are transient; the shell and the diagnostics console they
subscribe to are singletons. Neither dropped its subscription, so every visit left another instance
alive, raising property changes for pages that no longer exist. The debug one is the visible case: one
of the two events it never released opens a fullscreen QR dialog, so running a socket slice after a few
visits opened one dialog per abandoned instance.

### A group message could break its own run in half

Whether two messages belong to the same run is decided by sender name when the participant ids differ,
which is the ordinary case for a contact appearing under both a phone number and a LID. Names were
resolved inside the layout loop, so when a message asked "does the run continue past me?", the next
message had not been named yet and the answer was no — while that next message, once named, decided
the run did continue. The two flags contradicted each other across the same pair: a tail on a bubble
mid-run, and the one below it stuck on with no avatar and no name.

Names are now resolved for the whole batch before any run is measured.

### "Is this name just a stand-in for me?" knew only English in three of four places

The canonical list in `SelfChatNaming.KnownFallbacks` carries the localized ones — "Você", "Anda",
"Tu". Three of the four decisions that ask this question compared against the literals "Me" and "You",
and one of those three is the guard that decides what gets cached as a participant's name. On a device
running in any other language the localized stand-in walked past it as though it were a real name.

Now one predicate, `SelfChatNaming.IsKnownFallback`, used by all four.

### A failed save was forgotten instead of retried

`TryBeginPersist` clears the pending flag before the write runs, so a write that failed consumed the
debt: the change stayed unsaved until something unrelated dirtied the catalogue again. The message
queue already restores its drain on failure; the catalogue now does the same.

### The Status cap kept whichever items arrived first

Fifty per author, applied in the order the sync chunk delivered them. The equivalent cap one level up,
in `HistoryMessageBuilder`, sorts newest-first before cutting. For an author who posts a lot, the quota
could fill with old posts and drop the recent ones.

### Reading on the phone left the pinned tile showing the old count

The same event — a conversation became read — is handled twice, once for a local read and once for the
phone telling us. The local one moved off `SchedulePersist` a while back, with a comment explaining why:
rewriting every chat preview plus three JSON maps is fine on an SSD and costs several seconds on Mobile
eMMC. It also tells `IShortcutService` so a pinned tile follows along. The app-state copy was left on the
old shape, so reading on the phone paid the expensive persist and left the tile stale.

It now tracks which rows actually changed and writes only those, the same way, and reports the resolved
count to the shortcut service. Not a straight call into the local path: this one also raises the count
back up when the phone marks a chat unread.

### One more group-name rule written three ways

"Do we already hold a usable name for this group?" existed in three copies and only one of them looked at
the label on the row. A name reaches the row before it reaches the cache, so through the other two a
synced subject the invite blacklist would normally hold back could overwrite a good name — and whether it
did depended on which path saw the group first.

### A group's name could come from a different group

`GroupMetadataReader` asked "is this node the group I asked about?" in two places and completed the id
differently. The server writes it bare, without the `@g.us`, and `FindGroupNode` adds the suffix before
comparing while `ExtractSubject` used a plain normalize, which does not. So `ExtractSubject` never
matched a node that had an id, fell through to the first `group` node in the response, and returned its
subject. On a single-group answer that is the right node by luck. On a community reply it is a different
group's name. Both now go through one `MatchesGroup`.

### Every direct chat was fetching a profile picture nothing would read

`IWhatsAppService` and `IGroupService` both declare `EnsureHighQualityGroupAvatarAsync` a no-op for 1:1
chats. The implementation had no such guard, and the avatar policy calls it for every visible row. So
each direct chat spent a second CDN round trip on the full-size file — and the cache hydration that
restores `AvatarHighUrl` between sessions is itself group-only, so the file was fetched and then
forgotten. On metered mobile data that is the cost the comment above the policy says it is avoiding.

### A captured name reached only one of a contact's two rows

The `notify` handler matched rows on the normalized address and stopped at the first hit. A contact is
listed under both PN and LID, so a name arriving by one address never reached the row filed under the
other, which went on showing the phone number with the name already in hand. It now walks the canonical
rows like the rest of the service.

### The shutdown branch in the message flush was dead, and that is correct

Left over from an earlier design: `FlushOfflineReplayMessagesAsync` skipped its scheduled persist for a
"shutdown" reason no caller passes. Worth chasing down rather than deleting on sight, because the
alternative explanation was that suspension had stopped calling the flush and messages were being lost.

It is not. Suspension deliberately does not come through here: `PrepareForSuspendAsync` flushes the
append-only journal, which is what actually keeps these messages, and skips the per-chat rewrite that
would blow the Windows Phone suspend deadline. Recovery is `incoming-journal-recovery` on the way back
in. The branch is gone and the reasoning is now in the code, where the next reader will find it.

### A message deleted on the phone came back

`ApplyAppStateDeleteMessageAsync` dropped the message from memory and from the id index, fixed up the
preview, and stopped there. It never told `MessageStore`. SQLite is what a conversation is rebuilt from,
so the message returned on the next load of that chat — next launch, or just switching away and back.

The sibling routine for deleting a whole chat documents this exact trap in its own XML comment and does
both halves; the revoke path already calls `DeleteMessageAsync`. Only this one was missing it.

Two more in the same pair of routines: both picked the row to update with `FirstOrDefault`, while every
other app-state mutation in the file uses `GetChatRowsForCanonicalJid`, because a conversation is listed
under both its PN and its LID. Deleting a chat left the second row behind as an empty entry, and
deleting a message corrected the preview on one row while the other went on showing the message that had
just been deleted.

### The avatar queue could spin forever on a group in two places

Three faults compounding. The sibling fallback — copy the picture from the other row for the same group
— was only reached after trying parent and community candidates, so for any group without a community
above it, which is most of them, it was unreachable. Failing stamped a reason meaning "sibling tried and
failed", and the retry policy reads that reason to decide the row deserves another pass. Meanwhile the
two copies of "does this sibling have a picture" disagreed: the policy accepted a row holding only the
high-resolution file, the fetch copies `AvatarUrl` and would have copied nothing.

So the policy had proof a retry was worthwhile, the fetch could never act on it, and the row went back
in the queue on every pass at two IQs a round. On a phone on mobile data that is a battery bug.

`SiblingGroupAvatar` is now the single answer, and it is the restrictive one, because the caller that
acts on it can only copy what it can read. The sibling attempt now also runs when there are no community
candidates, and it carries the high-resolution file across — same group, so it is this row's picture
too, and leaving it behind sent the row straight back out to fetch a file already on disk.

### A session wipe during connect could take the connection down

`ConnectAsync` read `_authState.Sessions.Count` for a diagnostics line, three lines below the code that
had already captured the same state into a local precisely because a wipe running alongside nulls the
field — and with an `await` in between. `ClearSessionAsync` does not take the connect lock. The
exception unwound into the outer handler and tore down the socket that had just been created.

### "Is this label still a stand-in?" was answered five different ways

Five copies across `WhatsAppService` and its partials, no two alike. Two omitted the self-marker check,
so a row reading "(You)" counted as named and was never sent for resolution — it stayed that way for the
life of the row. Two read the bare number by stripping known domains with chained `Replace` calls, which
covers `@s.whatsapp.net` and `@lid` and silently passes a group id through whole. Three called
`Contains("@")` on a label they had not tested for null first.

`PlaceholderChatLabel` is the version `ContactNameResolver` already had right, which is the only one
that tested for null first. It cuts the address at the separator instead of stripping a domain list, so
an address nobody anticipated still reduces. It takes the self-marker answer as a boolean for the same
reason `ChatNameReplacement` does — recognising "(You)" needs the localized resources, which are UWP.

### Two more in the preview, from the same sweep

**Every recovered video, sticker and voice note was labelled "[Message]".** The journal recovery path
decided the placeholder text from `IsImage` alone, even though it computes the message's actual kind on
the next line to pass along. `MediaPreviewTag.ForKind` answers from the kind, and returns null rather
than a marker for something that should have carried text, so the caller decides instead of being handed
a label for media that never arrived.

**`ResolvePreviewKind` was computed and then ignored on the live path.** It exists because render info
resolving to `Text` is not the last word — the message itself can still say otherwise, and it falls back
to inferring from the message. Both live call sites passed the raw `renderInfo?.PreviewKind` instead, so
a message appeared as text while the app was open and as media after a restart.

### Four defects in the outgoing path, found by mapping it before touching it

**A receipt could pull a double tick back to a single one.** `ApplyListPreviewSendState` worked out
which message the list strip was showing by comparing clocks with a two-second tolerance, ignoring the
`LastMessageId` sitting right there. Two messages sent to the same chat seconds apart — ordinary — are
indistinguishable that way, so the older one's receipt landed on the newer one's strip. The rule is now
`ChatPreviewTip.ShowsOutgoingMessage`: the id answers it whenever the row has one, and the clock stays
only as a fallback for rows written before ids were stored, where refusing outright would freeze the
tick forever.

**The offline replay fix from earlier was being undone one line away.** `OfflineReplayChatSummary`
gained a `Status` field so the list would show the ticks a replayed message actually has instead of
assuming "sent" — but the snapshot that hands those summaries to the UI never copied it, so the mapper
received `null` every time. The corrected copy in the `catch` block had been right all along, which is
what made it look done.

**A sent photo showed a clock in the list and a tick in the bubble, at the same time.** The strip for a
local send was hardcoded to `Pending`. That is true for text, which starts pending, but media is already
sent by the time it gets there, and in a chat with yourself it is read on arrival — with no receipt ever
coming to correct the wrong guess, so it stayed wrong.

**A pair could be sorted with the same address on both sides.** Three alias registrations and the merge
scan tested JID suffixes case-sensitively, sitting a few lines from a block that normalizes and compares
case-insensitively. When the server varied the case, `lid` and `pn` both fell through to the alias and
the merge scan compared a chat with itself. `JidHelper` gained `IsLidJid` and `IsPhoneJid` next to
`IsGroupJid`; they are asked together to sort a pair, so they have to agree on casing.

### The restore path is now stricter than the live one, and the reason is authority

Testing the gate turned up the mirrored direction: our own LID paired with a *contact's* number. It is
accepted, and once filed it spreads — that contact's number then reads as ours, so their own legitimate
pair is refused as poison and they are left with no alias at all.

The obvious move is to refuse it. That would have been a regression: live, this pair is how identity
healing works. The server reports which number our LID belongs to, `Me.Id` is corrected to match, and
refusing it would break that repair at the only moment it ever runs — when `Me.Id` is already wrong.

So the pair is not what distinguishes the two cases; where it came from is. A usync answer is the server
telling us something. A row read back from our own file is only telling us what we believed last time,
and a wrong belief there has no way to correct itself. `IsUnsafeToRestore` is the stricter check the
restore path uses, and it is order-agnostic because a pair is written both ways and either entry can be
the one the loop reaches first.

It deliberately ignores shape, unlike the live check. The restore path lets malformed rows through — the
file predates that validation, and dropping merely unusual entries would cost real identities — so a
pair is no less wrong for being malformed if it hangs our address off someone else's.

**One weakness left standing.** The mirrored half of a poisoned pair vouches for it. Pairs are filed both ways, so a poisoned pair comes
back from disk as two entries, and the mirrored one (`selfPn -> contactLid`) is not refused by this
policy — its phone side is not self-linked, so the guard exits immediately. Restored first, it is
indistinguishable from the reverse entry that proves the user's own identity, and it validates the
poisoned half. Which entry the restore loop reaches first is dictionary order over a file. The clean fix
is to anchor on `_authState.Me.Lid` instead of on whatever the table happens to hold, but that trades a
forgeable proof for one that is unavailable exactly when the LID is not yet known — where it would
discard legitimate self aliases instead. Characterized in `AliasPairPolicyTests` as
`The_other_half_of_a_poisoned_pair_vouches_for_it`.

---

## `TransientChatMerge` — what survives when one contact turns out to be two rows

A conversation can be opened against a LID before we know who is behind it. A usync or an app-state
alias later reveals it is a contact already in the list under their phone number, and the two rows have
to collapse into one. `MergeTransientDirectChatIntoCanonicalAsync` did the deciding inline, mixed with
the UI-thread work and the SQLite writes.

The rule is not "the canonical row wins", which is why it is worth stating on its own. The transient row
is usually the one the user has actually been reading, so its preview and unread count are the newer
facts; but it also has the weaker identity, so it must not overwrite what the canonical row already
knows. Each field resolves that differently: the preview moves when it is newer (all of its fields
together — half a preview renders a message that was never sent), the unread count takes the higher of
the two rather than the sum (both rows were counting the same conversation), the avatar only fills a gap
because a newer picture is not a better one, and the name moves only when the canonical row is not
really named.

The message lists merge through `AppendMissingMessages`. The overlap between the two conversations is
the normal case rather than the exception, since whatever arrived before the alias was known was written
to whichever address the sender happened to use.

### Four behaviour changes, all pre-existing gaps the extraction exposed

Writing the rule down separately, and then testing it against its own stated terms rather than against
its implementation, turned up four things the inline version got wrong. None is a regression from this
refactor; all four were in the old merge.

**A contact could spoof their way onto the canonical row.** The old code checked the canonical side for
a self-marker but checked the transient side only for blank and number-as-name. So a contact whose push
name is "(You)" could not take over a row by the normal path, but *could* be copied onto one during a
merge — the exact spoof `ContactLabelSanitizer` exists to stop, through a door it did not cover. Both
sides go through the same test now.

**The preview and its message id came apart.** Everywhere else in Core the id travels with the preview:
`ChatStateStore.CopyInto` copies it alongside, `ChatPreviewTip.Clear` clears it alongside, and
`HistoryChatPreviewApplier` compares it alongside when timestamps tie. The merge left it behind, so a
row that took the transient preview ended up showing one message and claiming the id of another, and
being persisted that way. Nothing downstream repairs it: `ShouldStampMissingMessageId` fills the id in
only when it is *missing*, never when it is wrong.

**A row could end up wearing two different faces.** The avatar gap was tested on `AvatarUrl` alone while
`AvatarHighUrl` was neither tested nor copied. A canonical row holding only the full-size picture would
therefore accept the transient row's preview, leaving one identity's face in the list and another's in
the info pane — `GetAvatarUrl` falls back between the two resolutions, so which one appears depends on
the surface asking. The two urls are one fact and now move together.

**A name that was really a number passed as a name.** Each row was measured for namelessness against its
own address, which is right on its own terms but not across a merge: a LID row is frequently labelled
with the very number the canonical row is addressed by, so that label read as a name on one side and as
no name at all once it landed on the other. Both rows are measured against both addresses now — the
first correction here was asymmetric, and the asymmetry had the same bug on the other side, where a
canonical row wearing the LID's digits counted as named and refused the real name being offered to it.

The echo test itself also borrows `ContactLabelSanitizer.IsPhoneEcho` rather than comparing the label to
the address verbatim, so a number is recognised as a number after being formatted. The plain comparison
stays alongside it: `IsPhoneEcho` ignores anything shorter than a phone number, on the grounds that
short numeric nicknames are legitimate, and a LID is frequently shorter than that.

A fifth finding — that a row with nothing to show accepts an older preview and moves down the list — was
left alone. Showing something old beats showing nothing, which is what the rule already says.

---

## Batch: five duplicated rules from the media and send paths

One pass over the remaining audit findings that were confirmed pure and testable. The large ones
(`ExtractMessageRenderInfo`, the two parallel usync pipelines) are deliberately not here — those change
control flow and belong with a device pass.

**`MediaCacheNaming`** — the name a cached media file gets on disk, previously written by hand in six
places in two variants that had drifted in their fallbacks. The name derives from the message's
encrypted-content hash rather than its id, so the same photo forwarded into two conversations is one
file instead of two, and base64 is rewritten into the URL-safe alphabet because `/` would otherwise read
as a directory separator. The tests cover what fails silently: stability (an unstable name re-downloads
on every open), uniqueness (a shared name serves the wrong file), and that a path traversal cannot
survive sanitizing, since these names partly derive from server-supplied values.

**`MediaFileExtensions.NeedsAudioTranscode`** — whether a voice note has to be converted before it can
play. The order of the two checks is load-bearing and now has a test saying why: a file already
converted keeps its original Ogg mime while sitting in a playable container, so testing the mime alone
would re-transcode it on every replay.

**`SelfChatStatusPolicy`** — in the conversation a user has with themselves there is no second party to
deliver to, so anything that reached the server shows as read rather than waiting forever on a receipt
that will never arrive. Pending and failed pass through, because whether it got out at all is still a
real question.

**`OutgoingFailureClassification.IsTransportFailure`** — whether a failed send condemns the connection
or just the message. Both directions are expensive: tearing down a healthy socket over one refused
message makes every other conversation pay for it, while calling a dead connection a message problem
leaves every later send failing the same way.

**`GetContextInfo`** — a fourteen-branch chain duplicated verbatim in `IncomingPump` and
`HistorySyncContentFilter`. Deleted the local copy; the live path now calls Core, as `UnwrapMessage`
directly above it already did.

---

## Media preview markers — one source, and a reported bug that was not one

The `[Image]` / `[Voice Message]` markers were being assembled by hand in six places across
`IncomingPump`, `WhatsAppService` and `ChatPreviewMessageFactory`. They are now `MediaPreviewTag` in
`Unison.Core/Helpers`.

These look like display strings but behave like a wire format: `BackgroundNotification` matches them —
including their legacy Portuguese spellings — to choose an icon and a localized label, and
`HistoryMessageMapper` strips them when reading old rows so they never resurface as a caption.
Rewording one does not change what the user sees; it silently stops the marker being recognised and
leaks the raw text into a notification. The tests assert the exact spellings for that reason.
`Unison.Background` keeps its own copy because it cannot reference `Unison.Core`; the two lists have to
move together, and that is now written down in both.

**The audio/voice bug that prompted this does not exist.** The audit reported that
`ChatPreviewMessageFactory` mapped both `Voice` and `Audio` to `[Voice Message]`, so a shared music file
would be announced as a voice note. Following it through: `ChatPreviewKind` has no `Audio` member at
all — both land on `Voice` deliberately, because that enum drives the list icon and template, where the
two are presented identically. The distinction is carried alongside in `IsVoiceNote`, which
`HistoryMessageMapper` reads to recover the real kind. The `Audio` branch is unreachable through that
factory, and the behaviour is by design. Recorded as a test so the next reader does not re-open it.

---

## Send status — the ticks rule, and two defects in the replay path

`ShouldApplyMessageStatus` and `GetMessageStatusRank` are now `MessageStatusProgression` in
`Unison.Core/Helpers`, with 20 tests. They decide the ticks on every message the user sends and were
consulted from four places without a single test.

The rule is a ratchet, not an assignment, and the tests are written as the sequences that actually
arrive: receipts are not ordered, so a delivery receipt can land after the read receipt it preceded,
and taking the latest would flicker the ticks backwards in front of the user. The asymmetry worth
knowing is failure — a late error cannot undo proof that the message arrived, so it is believed while
the message might still be in flight and ignored once delivered or read. A failed message can still
recover, because failure sits below the ladder rather than on it.

**Ticks were wrong after an offline replay.** `ApplyOfflineReplayUiSummariesAsync` hardcoded
`MessageSendState.Sent` for anything the user had sent, ignoring the status the message actually
carried — so after reconnecting, a message already read showed one tick, and a failed one showed as
sent. The replay summary did not carry the status at all; it does now, and the list asks
`HistoryLiveMessageMapper.FromStatus` like every other path does.

**The replay rollback merge moved half a preview.** When two summaries for the same conversation were
reconciled, the newer one's text and timestamp were copied over but its authorship and kind were left
behind — pairing one message's text with another message's ticks. All the fields describing the preview
now move together.

---

## Seven defects found by auditing the incoming path

An audit of `IncomingPump`, `Media`/send and `Groups`/usync turned up more defects than extraction
candidates, so the defects were fixed first.

**Recovered messages were timestamped in local time.** `UpsertRecoveredWebMessageInfoAsync` built its
timestamp with `DateTimeOffset.FromUnixTimeSeconds(...).LocalDateTime` while the entire pipeline treats
message times as UTC. The value is persisted and read back from SQLite as `DateTimeKind.Unspecified`,
which `ToComparableUtc` correctly interprets as UTC wall-clock — so a message recovered through
placeholder resend was permanently displaced by the machine's offset. At UTC-3 it sorted three hours
into the past, taking the conversation's ordering and preview with it. Now `.UtcDateTime`.

**Group messages could be routed as direct messages.** Four places decided "is this a group?" with
`EndsWith("@g.us")`, case-sensitive, including the live routing decision in `HandleDecryptedMessageAsync`.
`JidHelper.Normalize` deliberately does not lowercase group addresses — the asymmetry recorded when it
was covered with tests — so a mixed-case `@g.us` would take the direct-message path: wrong routing,
wrong participant handling, wrong preview. All four now call `JidHelper.IsGroupJid`, which compares
case-insensitively.

**Three more timestamp comparisons made without normalizing** — the offline replay rollback merge, the
placeholder resend priority ordering, and the pending journal recovery ordering. Same recurring defect
as before: a list holding both live (`Utc`) and stored (`Unspecified`) values compares off by the local
offset.

**A chat lookup that disagreed with its own live path.** `ReconcileChatListFromStoredMessagesAsync`
matched a canonical JID with `==` while the live path used `OrdinalIgnoreCase`, so reconciliation could
fail to find a row the live path had just created, and create a duplicate.

**`IsValidMessageTimestamp` is now `MessageTimestampValidity` in Core**, with 13 tests. It read
`DateTime.UtcNow` internally, which made its boundaries untestable, and compared a raw timestamp to that
clock — so near the future cut-off the verdict depended on the machine's time zone. The tests pin what
matters: a rejected timestamp becomes `DateTime.MinValue` and never `UtcNow`, because substituting the
current time would promote a replayed old event to the newest message in the conversation.

The remaining findings are extraction candidates rather than defects and are listed in
`WhatsAppService-Extraction.md`.

---

## Connection health — one rule, two named profiles

Deciding when to tear down a live socket was spread across `WhatsAppService.Connection.cs` in two
places — the background health monitor and `IsCurrentSocketHealthyAsync` — plus five thresholds, two of
which were bare literals sitting inline (`TimeSpan.FromSeconds(18)`, `ProbeConnectionAsync(9000)`). Now
in `ConnectionHealthPolicy` (`Unison.Core/Helpers`). Policy only: performing the reconnect stays in the
host, per the architecture rule.

The two call sites used **different numbers** for the same question — 55s vs 45s of staleness, 9000ms
vs 10000ms of probe. That looks like drift, and on the evidence it is not: the background monitor runs
unattended every 25s on a phone battery, while the on-demand check runs with the user waiting on the
answer, where acting on a dead socket costs more than a probe does. So they are now `ConnectionHealthProfile.Background`
and `.OnDemand`, with the trade written down, rather than two accidental constants.

The rule itself is worth stating because it is counterintuitive: **a stalled node queue is never
probed.** A socket whose ordered queue has stopped making progress still answers probes, so probing it
returns "healthy" and the stall survives the health check while user messages never reach the app. It
goes straight to reconnect, and freshness does not override it.

One latent crash fixed on the way: the backoff ladder was indexed with `ReconnectBackoff[Math.Min(attempt, Length - 1)]`,
which clamps the top but not the bottom — a negative attempt number would index out of bounds.
`ReconnectDelay` clamps both ends.

14 tests. The interesting half assert *relationships* rather than values, since restating a constant
proves nothing: the monitor must look more often than a connection takes to go stale (otherwise every
cycle probes, turning a health check into a keep-alive), a connection must be called stale well before
its queue is called stalled (so the cheap diagnosis gets its chance before the expensive one), the
message pump must be given up on sooner than the socket (the cheap recovery cannot fix the expensive
failure, so it has to be tried first), and a probe must fit inside one monitor cycle.

---

## Unread counts — one rule instead of two copies

`WhatsAppService.IncomingPump` incremented unread counts in two places: once for a single arriving
message, once for the batch replayed after a reconnect. Same logic, written twice, differing only in
whether it added one or a delta — the exact shape that had already produced a real behaviour drift
earlier in this refactor. Both now call `ChatUnreadTally.Bump` in `Unison.Core/Helpers`.

The rule is worth a name because it is not obvious. A conversation's unread count is not stored once:
the same conversation can be listed under both its PN and its LID form, so the number lives mirrored
across several rows and the badge the user sees is whichever row the list renders. So a count is never
read from a single row and never incremented in place. Every write takes the **highest** value among
the sibling rows and stamps that one result onto all of them. The highest wins rather than the newest
because rows are not updated in lockstep — a row that missed a mutation is stale, not authoritative,
and taking its lower value would silently drop unread messages.

`AppStateChatMutation.ResolveUnreadCount` was reading that same maximum with its own loop, so it now
calls `ChatUnreadTally.HighestAmong` instead. Its existing tests passed untouched, which is the proof
the two readings really were the same.

13 tests. The ones carrying the weight: every row of a conversation ends up showing the same number, a
stale sibling cannot drag the count backwards, and a count that had somehow gone negative is repaired
on the next message rather than needing to climb back through zero.

---

## Chat identity — JidHelper.Normalize covered, and a seam mismatch found

`JidHelper.Normalize` decides whether two addresses are the same conversation. Which row a message
lands in, whether the list shows one chat or two, which history keys are queried — all of it inherits
its answer, and it had no tests. Now 26, written as characterization: they record today's behaviour so
that a later change to chat identity has to be deliberate.

What they pin, beyond the obvious: a device suffix is dropped so one contact writing from phone and
desktop stays one chat; a LID loses its instance suffix; a `.0` alias on `@s.whatsapp.net` collapses
while a `.5` is kept whole, because that one is a different identity rather than an instance. And
normalizing is idempotent — load-bearing, since the same address is normalized repeatedly as it moves
between socket, store and list, and a second pass that altered it would make identity depend on how
many times a value had been handled.

**The finding, recorded and not acted on.** Group addresses take an early return, before the line that
lowercases the server part. So `@G.US` and `@g.us` normalize to two different strings, which is
harmless only while every consumer compares group addresses case-insensitively. `WhatsAppService` does.
`ChatStateStore` does not — it keys `_messagesByChat`, `_pushNames` and `_addressBookNames` with
`StringComparer.Ordinal` and `FindChat` compares with `StringComparison.Ordinal`. `FindChat` is what
`UpsertChats` asks before deciding whether a chat is new, so a mixed-case `@g.us` reaching the store
becomes two rows for one group, while the client still sees one.

The store is internally consistent, so this is a mismatch across the Core/UWP seam rather than a bug
inside either side, and whether it fires depends on whether the server ever varies that case — which
cannot be established from the code. Not changed: making chat identity case-insensitive is a one-line
edit with the blast radius of merging or splitting conversations, and it belongs with the device pass.
The test says so at the assertion.

---

## Chat preview staleness gate

- New `ChatPreviewStaleness.ShouldAccept` in `Unison.Core/Helpers`: the time check every preview write in `ApplyChatPreviewIfNewer` passes through, and it had no test
- It matters because previews arrive from four directions — live messages, history catch-up, offline replay, SQLite reconciliation — and not in order. Without it, a history chunk delivered after a live message rewrites the list with something older, which reads as the conversation going backwards
- Three behaviours were load-bearing and undeclared: an equal instant is *accepted*, because the same message often arrives again carrying a receipt or a caption it did not have the first time; a message with no usable instant is refused even on an empty row, since accepting it would also move the chat to the top on no evidence; and `force` skips everything, because a row whose stored instant is wrong would otherwise refuse the correction meant to fix it
- 14 tests, including the pair that differs only by the `force` flag, and the store-vs-live kind cases

**Found and deliberately not changed.** The no-op check below the gate compares the row's text against
`Normalize(preview)`, while the write a few lines later sets it from `Normalize(raw)` — where `raw` has
had an author prefix peeled off when no explicit `authorPrefix` was passed. When a group preview carries
its author inline, those two are different strings, so the check cannot match and the row is rewritten
with identical content. That is a wasted change notification and a repaint, not wrong output. Left alone:
it needs the device pass to confirm the repaint is real before restructuring a normalization path that
four callers depend on.

---

## Audit — raw DateTime comparisons in the chat list

Swept the client for the defect found while extracting `ChatPreviewTip.PickLatest`, on the rule now
in `WhatsAppService-Extraction.md`: comparing `ChatMessage.Timestamp` values directly is wrong
wherever the list can hold both live messages and rows read back from SQLite. Live rows are
`DateTimeKind.Utc`; stored rows are `Unspecified` and already UTC wall-clock. Compared raw, a stored
row looks a local offset newer than it is — three hours in Brazil.

Four more sites, all deciding which message a chat row displays:

- **Chat deduplication** (`DeduplicateChatsAsync`) took `Max(m => m.Timestamp)` raw while, three lines
  below in the same comparison, the `LastMessageTimestampUtc` branch went through `ToComparableUtc`. One
  expression normalising one side and not the other. This decides which of two duplicate rows' preview
  survives the merge
- **Replay preview refresh** and **two catch-up sweeps** in `.IncomingPump.cs` picked the newest message
  with `OrderBy`/`OrderByDescending` on the raw value

New `ChatMessageOrder.NewestComparableUtc` for the timestamp, `ChatPreviewTip.PickLatest` for the
message; the two are tested against each other so they cannot disagree about which message is newest.
10 tests, written around the defect rather than the method — including that the fix does not overshoot
into always preferring live messages.

Left alone deliberately: the offline-replay UI summaries in `.IncomingPump.cs` compare timestamps raw
too, but those are built on one path that already normalises upstream, and there was no evidence of
mixed kinds. Not changed without it.

---

## WhatsAppService extraction — 3.9b closed, 3.5b and 3.1c done

- **3.9b closed.** `ChatPreviewTip` gained `PickLatest` and `Clear`, the two rules inside the delete appliers. What stays in `.AppState.cs` is the tombstone write and the SQLite delete, whose rule and I/O are not separable
- **Fixed on the way through:** deleting the top message picked its replacement with `OrderByDescending` on the raw `DateTime`, while `ChatDisplayOrder` and `ChatPreviewTip.PickNewer` both normalise through `ToComparableUtc` first. Rows read back from SQLite arrive `Unspecified`, so a stored message could look a local offset newer than it was and be promoted over a genuinely newer one — the same three-hour drift this project has hit before
- **3.5b done.** `GroupReceiptTally` (`Unison.Core/State`) owns the per-message tally of who received and who read, completing 3.5 alongside the `ReceiptReader` from 3.5a. It takes its own lock rather than sharing `_messageStateLock`, which guarded several unrelated dictionaries; the state it replaces was touched nowhere else
- Check marks were previously only observable by sending to a real group from a second phone. Now pinned: that reading implies receiving, so a group where one member reads and another only receives still reaches delivered; that a repeated receipt from one participant does not count twice; that read is terminal and stops being tracked; and that the eviction sweep spares messages still being reported on
- **3.1c done.** `ChatAvatarOutcome` (`Unison.Core/Helpers`) records how an avatar lookup ended — four fields, three outcomes, written four different ways across `ApplyAvatarResultAsync`
- The distinction it exists to protect: *no photo* is an answer and is stamped as one, so the row stops being asked; *could not reach it* is not, and deliberately leaves the fetch time and any existing image alone. Collapsing them either way is a bug with no error attached — one direction is a blank circle forever, the other is re-asking the server on every sweep
- 45 tests across the three. Suite at 268

---

## WhatsAppService extraction — phase 3.9b (app-state chat mutations)

- New `AppStateChatMutation` in `Unison.Core/Helpers`: what a mutation arriving from the account does to a chat row — the unread count on mark read/unread, and the archive / pin / mute flags. First slice of the appliers folded in from 3.8
- These land on *every* row sharing a canonical identity, because one conversation can be listed under both its PN and its LID form. That is the whole reason the rules are non-obvious, and it is now stated rather than implied
- Two of them would be easy to get wrong in a way nobody reports. "Mark as unread" carries no number, so the highest count among the alias rows is kept and only falls back to 1 — otherwise a chat that really had seven waiting would come back showing one. And an unpin writes `0` rather than null, so an alias row that has not received the mutation yet cannot resurrect the pin through dedupe
- `ChatFlagChange` replaces five optional parameters, and `AppliesMute` earns its place: `null` is a *value* for `MutedUntil`, meaning unmuted, so absence had to be spelled separately. It also removed a duplicated `archived.HasValue || pinned.HasValue || applyMute` that decided whether the store write was worth doing
- 20 tests. The client keeps creating the placeholder row, the UI thread, the badge, the store write and the sort

---

## Startup crash — MediaDerivationService could not be constructed

- `MediaDerivationService` was registered with `AddSingleton<MediaDerivationService>()`, but its constructor is `internal`. Type activation only considers public constructors, and a declared constructor suppresses the implicit parameterless one, so the container had nothing to call
- Thrown as `InvalidOperationException` from the `IWhatsAppService` factory, because that is the first thing to ask for it. `BuildServiceProvider` is called without `validateOnBuild`, so a registration this broken survives until the first resolution — which is the launch after the one that introduced it
- Registered by hand instead, matching `AvatarFetcher` and `BridgeSessionProvider` in the same file. The type and its constructor stay `internal`: it is an implementation detail of `Unison.Uwp`, and widening them to satisfy the container would be the wrong direction
- Regression from 3.3b. Audited the other registrations added during phase 3: `MediaCacheService`, `AvatarCacheService` and `UsyncGate` declare no constructor at all, so the implicit public one activates fine, and `AvatarFetcher`, `GroupMetadataReader` and `ReceiptReader` are all built by hand. This was the only one

---

## WhatsAppService extraction — phase 3.9b (chat preview tip)

- New `ChatPreviewTip` in `Unison.Core/Helpers`: which message the one-line preview in the chat list should show, and whether the row already shows it
- Three decisions pulled out of `ReconcileChatPreviewsFromSqliteAsync`, which is otherwise SQLite reads, alias key expansion and diagnostics — none of which can be tested without a database and a UI thread
- `PickNewer` moved wholesale from `PickNewerPreviewSource`. `IsAlreadyShowing` and `ShouldStampMissingMessageId` were inline conditions in the apply loop; the second is a schema-v4 migration that stamps a missing `LastMessageId` from the store rather than forcing a full history resync to recover it
- Worth testing because wrong here is wrong where the user only glances: the last-message line. The tie-break that prefers our own message on the same wall-clock second exists for cross-device echoes, and must never beat a strictly newer one — that is now pinned, as is reading a kind-less timestamp as UTC rather than letting it drift three hours
- 20 tests. The client keeps the querying, the key expansion and the write

---

## WhatsAppService extraction — phase 3.9b (background display-name table)

- New `BackgroundDisplayNameTable.Build` in `Unison.Core/Helpers`: the address-to-name map handed to the background task, out of `PersistBackgroundDisplayNamesAsync` — 57 lines down to 6
- This is what a toast shows while the app is not running, so a mistake here reads as a notification from `+55 11 98888-8888` instead of from Ana. Nowhere near a log
- The precedence between its four sources used to be implied by the order of four loops and by which of them guarded with `ContainsKey`. Now stated: chat labels are the baseline, WhatsApp-learned names fill gaps only, the device address book overrides both, and PN/LID aliases mirror a name onto the identity form that lacks one without ever overwriting
- Named `Table`, not `Snapshot`: `Unison.Background.BackgroundDisplayNameSnapshot` already exists and is the serialized envelope. This builds the `Names` map inside it
- 18 tests, including that mirroring carries whichever name won the earlier rounds, and that an alias between two unknown addresses invents nothing
- The client keeps what is genuinely its own: copying the live UI-thread collections before handing them over, and the write

---

## WhatsAppService extraction — phase 3.9b (chat name replacement rule)

- New `ChatNameReplacement.ShouldReplace` in `Unison.Core/Helpers`: whether a freshly resolved name should be written over the label a chat row is showing
- **The rule was written out twice**, in `ApplyResolvedNamesToChatsAsync` and in `NormalizePersistedChatNamesAsync` — eleven near-identical lines in each
- **The two copies had already drifted**, and this changes behaviour on one of them. `ApplyResolvedNamesToChatsAsync` guarded with `IsNullOrWhiteSpace`; `NormalizePersistedChatNamesAsync` used `IsNullOrEmpty`, so on that path a whitespace-only resolved name could be written over a placeholder label — `IsMeaningfulChatLabel` rejects whitespace, which made `resolvedMeaningful` false, but the `!existingMeaningful` branch still let it through. Unified on the stricter check
- Takes the "is this label meaningful" answers as booleans rather than working them out, since that needs the contact directory and the session's own address. Same split `GroupNameSyncBlacklist` already uses, so `ResolveDisplayName` and `IsMeaningfulChatLabel` stay in the client for 3.6
- 14 tests, including that the invite-link blacklist applies to group subjects only — a contact is entitled to a name containing one of its tokens — and that group and contact behave identically for every other input

---

## WhatsAppService extraction — phase 3.9b (persist scheduler)

- New `PersistScheduler` in `Unison.Core/State`: whether the catalogue owes a save, whether startup is still warming up, and which of two elapsed timers actually runs the write
- Out of the client: `_persistPending`, `_suppressStartupScheduledPersist` and the check-then-act inside `SchedulePersist` / `EnableScheduledPersist`. The `Timer` stays, same seam as the message queue — `Request` answers with a `PersistScheduleAction` and the host arms the debounce
- **Fixes a real race, not just a move.** `_suppressStartupScheduledPersist` was read inside `_persistLock` in `SchedulePersist` but read *and written outside it* in `EnableScheduledPersist`, next to a `_persistPending` read that was inside. Two callers lifting suppression at once could both conclude they owed the deferred save. Lifting is now one atomic step
- `EnableAfterStartup` returns `PersistEnableResult` rather than a bool so both startup log lines survive: suppression lifted, and separately, a deferred save being settled
- 13 tests: startup deferral, collapsing many requests into one save, the second elapsed timer becoming a no-op, a request arriving mid-write not being swallowed, and shutdown not re-arming startup suppression

---

## WhatsAppService extraction — phase 3.9b, first slice (pending message queue)

- New `PendingMessageQueue` in `Unison.Core/State`: the messages accepted but not yet written to SQLite, plus the rule for when to write them. 143 lines out of the client against 42 back in
- Replaces a cluster of seven fields, three constants and one lock spread across `WhatsAppService.cs`, `.Persistence.cs` and `.IncomingPump.cs` — `_offlineReplayPendingMessagesByChat`, `_offlineReplayDirtyChats`, `_offlineReplayPendingMessageCount`, `_offlineReplayFlushRequested`, `_lastOfflineReplayFlushUtc` and `_offlineReplayPersistLock`
- **The dedupe rule was written twice**, once when queueing and once when putting a failed batch back. Now `MergeIntoChat_NoLock`, in one place
- The queue decides what is pending and whether someone should write; it does not own the timer or the database. `Add` returns a `PendingFlushAction` and the host acts on it, so the flush decision stays atomic under one lock while the platform work happens outside it
- Takes the clock as a parameter rather than reading `DateTime.UtcNow`, which is what makes the 750ms interval rule testable
- 30 tests. The threshold rule, the interval starting at the first message rather than at construction, single-claim on the flush, drain, requeue and the per-chat batch cap
- **Recorded, not endorsed:** a restored message overwrites a newer one queued while the write was in flight. Carried over from the original requeue; the loss is bounded, since the older copy stays queued and the next flush writes it
- `ScheduleOfflineReplayFlushTimer` gained its own lock. The queue lock no longer covers the timer swap, and two threads replacing that field could otherwise dispose a timer mid-callback
- This is the slice of 3.9b the new test project made safe to take: it is the message persistence path, where a mistake compiles cleanly and shows up as messages that silently never land

---

## JidAliasTable under test — and a self-aliasing finding

- 32 more tests (120 total): canonicalization, `IsSelfLinked`, `IsLidLike`, `GetCanonicalSelfPnJid`, and the `Changed` contract including the silence on a restated pair
- **Finding, pre-existing, not fixed here.** The guard in `GetCanonicalJid` is commented "never canonicalize a non-self contact to our own JID". It cannot fire when a contact's LID is aliased straight to our id: the guard's own `IsSelfLinked(normalized)` consults the alias being validated, reports true, and switches the guard off. Confirmed by probe — `GetCanonicalJid` returns our own address for that contact
- The effective protection is upstream, in `WhatsAppService.TryRecordAliasMapping`, which refuses to write such a pair. The guard in the table is narrower than it reads: it only catches an alias that is self-*linked* without being self-*jid*, e.g. a device-suffixed form of our own number. That case is tested and does hold
- **The gap that mattered, now closed:** the startup restore in `WhatsAppService.Connection.cs` wrote persisted aliases into the table directly, normalizing but skipping `TryRecordAliasMapping`. A poisoned pair that ever reached disk came back unvalidated on every launch and merged the contact's chat into the self chat. The restore now drops such an entry through a new `IsSelfPoisoningAliasPair` and logs the count
- Deliberately **not** reusing `RegisterAliasMappings` for the restore. On an empty table every pair counts as new, so it would raise `OnJidAliasResolved` per pair and queue every restored address for an avatar pass — the startup storm the remarks on `RegisterAliasMapping` describe having engineered away
- Deliberately **not** applying the full live validation either. The table is also written by paths that predate it, and rejecting their entries would drop identities that are unusual rather than wrong. Only the self-poisoning case is refused, and that case is already corrupt
- Why the fix works where the in-table guard cannot: it is asked before the entry is inserted, so the table cannot yet offer the alias as evidence for itself
- Both behaviours are pinned by tests that say they are recorded rather than endorsed, so a fix flips a red test instead of silently changing canonical keys
- Separately noted: `_inner` uses the default case-sensitive comparer while `Snapshot()` rebuilds with `OrdinalIgnoreCase`. Writers normalize, and normalization lowercases the server, so it does not bite today — but `Snapshot()` would throw rather than degrade if two keys ever differed only by case

---

## Test project for Unison.Core

- New `tests/Unison.Core.Tests` (net9.0, xUnit). 88 characterization tests, ~30ms. Run with `dotnet test tests/Unison.Core.Tests/Unison.Core.Tests.csproj`
- Covers what phases 3.x moved into Core and left unguarded: `ChatDisplayOrder` (pin/recency/name order, `Reposition`, `SortInPlace`), `GroupNameSyncBlacklist`, `MediaFileExtensions`, `WhatsAppMapper.ToUtc`
- **Characterization, not specification.** They record what the code does today so that 3.9b fails loudly instead of silently. Where current behaviour is arguably wrong it is pinned down and labelled as such — see the substring match in `GroupNameSyncBlacklistTests`
- Two regressions are pinned explicitly: `ToUtc` must not treat `Unspecified` as local (the Brazil UTC−3 +3h strip shift), and `SortInPlace` must raise no `CollectionChanged` when the list is already ordered
- **Deliberately not in `Unison.slnx`.** The solution carries ARM/ARM64/x86 platform configs for the UWP head; adding a net9.0 project to it risks the existing build and deploy flow for no gain. `dotnet test` finds it directly
- Reaches `Unison.Core` only. `ReceiptReader` and `GroupMetadataReader` live in the UWP head and stay uncovered until something moves or the head grows a test story

---

## WhatsAppService extraction — phase 3.3b (derived media renditions)

- New `MediaDerivationService`: WebP to PNG display sibling, platform PNG re-encode, Ogg/Opus to M4A transcode, first-frame video poster — 216 lines out of `.Media.cs`
- Grouped because they share one cause: Windows 10 Mobile ships fewer codecs than the desktop and WhatsApp sends for the desktop. The original payload always stays on disk; these sit next to it
- Takes the concrete `MediaCacheService`, closing the gap 3.3a left open — `MediaTranscoder` encodes into a `StorageFile` it is handed, which a Core interface cannot produce. Both sides are UWP, so nothing is being abstracted away
- DI now registers `MediaCacheService` concretely and maps `IMediaCache` onto the same instance
- Still in the client (3.3c): `Ensure*AvailableAsync`, `EnsureWebPDisplayUriAsync`, `EnsurePlayableAudioUriAsync` — they read, write and persist `ChatMessage`

---

## WhatsAppService extraction — phase 3.9a (chat list order)

- New `ChatDisplayOrder` in `Unison.Core/Helpers`: `Compare`, `Reposition`, `SortInPlace`. Sibling of `ChatMessageOrder`, which already owned the same job for messages
- Out of `WhatsAppService`: `CompareChatsForDisplay`, `RepositionChatForDisplay`, `SortChatsForDisplay` — 122 lines. The client keeps one-line forwards, so no caller changed
- Order rule unchanged: pinned first and by pin time, then last message, then name as tie-break
- `SortInPlace` keeps its first-out-of-place scan; the list is usually already ordered and every `Move` is a collection-changed notification the `ListView` acts on
- Picked as the first slice of 3.9 because it is the half the compiler can vouch for. Persistence, preview reconciliation and the 3.8 appliers all write chat state under the client's locks — that is 3.9b

---

## WhatsAppService extraction — phase 3.5a (receipt reading)

- New `ReceiptReader` + `ReceiptFacts`: parses a `<receipt>` node into status, message ids, chat, participant and group flag, and counts group recipients. Takes `IJidResolver`
- Out of `WhatsAppService.Receipts`: the receipt-type mapping, id collection, chat/participant extraction and the recipient count — `HandleMessageReceiptAsync` went from 74 lines to 33
- Unknown receipt types are still dropped rather than promoted to delivered, now in one named place
- Recipient counting is deliberately **not** `GroupMetadataReader.CountMembers`: that counts the roster including the account itself, which would set a target receipts can never meet and groups would never show as read
- `CountRecipients` returns `int?` so "no group in the response" stays distinct from "a group of nobody" — the client caches the second and retries the first, as before
- Still in the client: `RegisterGroupReceipt` and the recipient-count cache, both under `_messageStateLock`. Moving them is 3.9

---

## WhatsAppService extraction — phase 3.7a (JID alias table)

- New `JidAliasTable` in `Unison.Core/State`: the LID/PN map plus the rules that read it — `GetCanonicalJid`, `IsSelfLinked`, `IsSelfJid`, `IsLidLike`, `GetCanonicalSelfPnJid`, all moved verbatim
- Out of `WhatsAppService`: the nested `NotifyingJidAliasMap`, `IsSelfLinkedJid`, `IsSelfJid`, `IsLidLikeJid`, `GetBaseUserPart`, `GetCanonicalSelfPnJid`, `GetCanonicalForLidLikeSWhatsappJid` — 469 lines. The client keeps one-line forwards, so no call site changed
- `JidResolver` reads the table directly instead of wrapping the client; only `Self` still goes to the session, and through `IWhatsAppService` rather than the concrete class
- `IJidResolver` gained `IsSelfLinked`; `GroupMetadataReader` drops the two delegates from 3.2a and takes the resolver
- The table learns the account via `BindSelf(Func, Func)` — the own LID is written in place on the auth state after pairing, so a snapshot would miss it and the self-poisoning guards would stop firing
- **Not** folded into `LidMappingStore` as originally planned: that store is async while canonicalization is synchronous and sits behind XAML bindings, and the two disagree on key shape (user part vs. whole JID). Reconciling is 3.7b, now a storage swap behind this seam

---

## WhatsAppService extraction — phase 3.3a (media cache)

- New `IMediaCache` / `MediaCacheService` over `LocalFolder/MediaCache/{Images,Audio,Documents,Video,VideoPosters}`: `SaveAsync`, `TryGetUriAsync`, `TryReadAsync`, `SanitizeFileBase`
- New `MediaFileExtensions` (Core, pure) owns MIME to file extension for image / audio / video / document, plus the Ogg-Opus predicates
- Out of `WhatsAppService.Media`: `SaveImageBytesToCacheAsync`, `SaveAudioBytesToCacheAsync`, `SaveVideoBytesToCacheAsync`, `SaveDocumentBytesToCacheAsync`, `TryGetCachedImageUriAsync`, `TryReadCachedImageBytesAsync`, `SanitizeCacheFileBase` and the four `Get*FileExtension` helpers — 217 lines
- `SaveAsync` takes `reuseExisting`, preserving a difference the old call sites had on purpose: images and videos keyed by message id skip the rewrite, documents / posters / transcodes do not
- Still in the client: download orchestration (`Ensure*AvailableAsync`), WebP display conversion, the Ogg-Opus transcode and the video poster. The transcode keeps its own folder handling because `MediaTranscoder` encodes into a `StorageFile`, which a Core interface cannot express
- Known duplication left alone on purpose: `OggOpusToWavConverter`, `OggOpusHandlerService` and `HistoryThumbnailMaterializer` still build the same folders by hand

---

## WhatsAppService extraction — phase 3.2a (group protocol reading)

- New `GroupMetadataReader` turns a `w:g2` response into plain objects: subject, announce-only, member count, participant drafts, my participant role, and the picture-lookup candidate order
- It also took the roster list algebra (`RosterJidSetsEqual`, `MergeInPlace`) and the group-id placeholder test. `GroupListingEntry` and `GroupMemberDraft` moved out of the client with it
- Holds no socket, no chat state, no dispatcher — the first part of the group cluster that can be exercised without the UI thread
- Out of `WhatsAppService`: `ReadGroupMemberDrafts`, `CountGroupMembers`, `FindGroupNode`, `ExtractGroupSubject`, `ResolveMyGroupRole`, `ParseParticipantAdminRole`, `NormalizeGroupJidCandidate`, `GetGroupMemberPictureCandidates`, `IsGroupIdPlaceholder`, `RosterJidSetsEqual`, `MergeGroupMembersInPlace` — 383 lines net
- The reader takes canonical-JID and is-self as functions instead of `IJidResolver`, because self-recognition reads the PN/LID alias table the client still owns until 3.7. Reimplementing it would have silently changed the role the composer trusts in announce-only groups
- Applying metadata to chat rows stays in the client: it is blocked on 3.6 (`ContactNames`), 3.7 (alias table) and 3.9 (`Chats` + persist), not on itself

---

## WhatsAppService extraction — phase 3.1b (avatar fetch)

- New `IUsyncGate` / `UsyncGate` replaces the client's private `_usyncLock`. Contact name resolution and profile picture lookups share one rate-limited directory surface, so they keep sharing one gate now that they are moving to different owners
- The gate hands out a disposable lease, collapsing the `WaitAsync` / `lockTaken` / `Release` triple at all five call sites — two of which swallowed `ObjectDisposedException` and `SemaphoreFullException` by hand
- New `AvatarFetcher` owns wire-and-disk: candidate sweep, high-resolution fetch, download. It reaches the socket via `IWhatsAppSessionProvider` and touches no chat state
- Out of `WhatsAppService`: `FetchBestProfilePictureResultAsync`, `GetProfilePictureAsync`, the high-resolution group loop. `GetProfilePictureUrlAsync` is now a one-line forward
- Candidate resolution stays in the client: the PN/LID table is 3.7's, and injecting `IJidResolver` into the fetcher would close a DI cycle
- Still in the client: applying a result to a `ChatItem` (3.9), the group-metadata avatar fallback (3.2), and `ShouldDeferAvatarFetch`, which is history gating rather than avatar policy

---

## WhatsAppService extraction — phase 3.1a (avatar cache)

- New `IAvatarCache` / `AvatarCacheService`: `TryGet`, `SaveAsync`, `DeleteIfCached` over `LocalFolder/MediaCache/Avatars`
- Out of `WhatsAppService`: `BuildSafeAvatarFileName`, `TryGetCachedAvatarUri`, `DownloadAndCacheAvatarAsync`, the static `AvatarHttpClient`, and the file delete inlined in `MarkAvatarImageLoadFailed`
- Callers pass an `AvatarVariant` (`Preview` / `HighResolution`) instead of a `"_high"` filename suffix
- `ProfileFacade` takes the cache directly, so `CacheRemoteAvatarAsync` left `IWhatsAppService`
- Avatar **fetch** policy is unchanged and still in the client; only the storage moved

---

## WhatsAppService extraction — phase 2 (invert `Attach*`)

- `WhatsAppService` reports instead of calling up: new `OnStreamError`, `OnInvalidSessionSuspected`, `OnLiveStatusReceived`, `OnBackgroundHistorySyncBounced`, `OnAvatarCached` and `OnJidAliasResolved` on `IWhatsAppService`, each subscribed by the façade that owns the subject
- `AttachConnectionService`, `AttachStatusService` and `AttachHistoryService` removed; `AttachContactService` reduced from nine call sites to the two deferred-startup-maintenance ones
- Contracts lost the notify-in doors whose only caller was the client: `IConnectionService.NotifyStreamError` / `NotifySuspectedInvalidSession`, `IStatusService.TryIngestLiveAsync`, `IHistoryService.NoteBackgroundHistorySyncBounce`, `IContactService.ClearAvatarAttempted`
- `StatusFacade` now takes `IWhatsAppService` so it has something to subscribe to
- Dead branch removed: `ApplyGroupMetadataFromResponseAsync` had a `hydrateAvatars` parameter both call sites passed as `false`
- `AttachMessageService` and the three store attaches stay — those calls return values or interleave with client state, so they move with phases 3.2 / 3.4 / 3.8 / 3.9 / 3.10
- No behaviour change: the reports were already fire-and-forget with their failures swallowed

---

## WhatsAppService extraction — phase 1 (UI frontier)

- ViewModels and views no longer take `IWhatsAppService`: `ShellViewModel`, `ChatListViewModel`, `ChatDetailViewModel`, `ChatDetailInfoViewModel`, `DebugViewModel`, `ChatDetailInfoViewModelFactory`, both `ChatDetailView` code-behinds, `ChatAvatarControl`
- New `IGroupService` / `GroupFacade`: send permissions, roster hydrate, high-quality group avatar (forwards to the client until phase 3.2)
- `IChatService` gains active chat, unread total, list-row persist and SQLite preview reconcile; `IHistoryService` gains the initial-sync counters and `PreferFrugalSyncBudget`; `IConnectionService` gains the startup order and `IsConnected`; `IContactService` gains `PhoneContactNamesByJid` and `MarkAvatarImageLoadFailed`
- Verbose logging toggle moved to `IDiagnosticsConsole` → `IRuntimeDiagnostics` (not `IDebugSendService`, which is `#if DEBUG`-only)
- `WhatsAppService.Instance` fallback removed from `ChatAvatarControl`; `AttachUiDispatcher` moved out of `BootView` / `MainView` into `App.ConfigureServices`
- No behaviour change: every member still runs where it did, only the caller's address changed

---

## Frugal sync budget (universal)

- Removed `ForceDesktopSyncBehaviorOnMobile`; timings follow `PreferFrugalSyncBudget` (memory ≠ Low, or Mobile while catalog/sync is heavy)
- Chat list: longer refresh debounce + capped Moves per turn under frugal (continue on next tick)
- Automatic FULL_HISTORY catch-up: short ~1.5s quiet floor for both frugal and generous (not a 10s frugal-only delay); manual wipe unchanged
- Avatar batches: 4 + viewport-priority ordering when frugal; generous keeps larger batches
- **Disk avatar hydrate** runs right after catalog load / at the start of deferred maintenance (before quiet, memory gate, and history catch-up); network fetches stay behind history
- Background history toast: bounce counter in `HistoryFacade` (no mid-session reset); toast once when bounce &gt; 2 and the window is not visible
- FULL_HISTORY continue: idle **35s** (frugal **45s**), poll **8s**, reconnect delay **12s**, cooldown **15s**
- Catch-up pagination insists while receiving: after each quiet lot, adjacent FULL_HISTORY on the live socket (soft-reconnect only as fallback); offline-drain re-evaluates on every release while catch-up is active; empty tip-fresh drains still retry until stagnant

---

## Group author names on first open

- Bubbles: do not cache short-JID placeholders; drop bad cache/hints so roster/Person can win; re-run layout on `DisplayNamesUpdated` and on `MentionLookup` (in-place roster merge)
- Strip: push-name bare keys only for PN (`@s.whatsapp.net`); `ChatAuthorProjection` only replaces unusable strip labels (not a random other name)
- **Phone-as-name:** digit-only labels (≥7) are not usable display names — stops LID participants sticking on the phone number until leave/reenter, and lets the strip upgrade when a real name arrives

---

## Shell apply on toast / resume (Mobile Settings)

- Toast activation skips `OnLaunched`; now calls `ApplyFromSettings` after DI so strategy/Theme.xaml and the pending “Shell applied” toast run when reopening via notification
- Same apply on `OnResuming` (process often survives shell-change `Exit` on W10M)

---

## Phone call from chat info (tel:)

- `CallPhoneCommand` on `ChatDetailViewModel` / `ChatDetailInfoViewModel` (1:1 + member): confirm dialog, then `tel:+digits` via `IUriLauncher`
- Info phone row: phone icon + accent hyperlink number (same command)
- Strings: `ChatDetail_CallPhoneTitle` / `Body` / `Confirm` in all locales

## Chat-list pins on cold load

- `RefreshVisibleChats` applies ChatStore pin/mute before sort (was missing → pins appeared only after opening a chat)
- Attach awaits `ChatStore.WarmAsync`, then re-applies + re-sorts; canonical-JID fallback when the pin row key differs from the list JID

---

## Unison ChatDetail / Chats pages (W10M-safe split)

- Separate Unison shell pages under `Shell/Unison/Views/` (`ChatsView`, `ArchivedChatsView`, `ChatDetailView`) — own visual trees, shared `ChatDetailViewModel`
- `UnisonThemeStrategy` navigates to Unison Chats/Archived; WhatsApp keeps `UI/Views`
- Unison detail: header `ShadowOpacity=0`; full-height left `DropShadowPanel` cast (same blur/opacity as former top shadow)
- `IChatDetailSurface` / `IConversationShellPage` so templates and MainView resolve either shell without wrapping views

---

## Status checkmarks via UnisonIcons font

- Added `EA03` (single check) and `EA04` (double check) to `UnisonIcons.ttf`; regen: `scripts/icons/generate-unison-icons.ps1`
- Message footer + chat-list preview use `FontIcon` with theme brushes (`ChatDetailStatusCheck*`, `ChatListStatusCheck*`) so Unison read ticks stay white on green (no blue PNG washout)

---

## Unison chat list: edge-to-edge rows

- `WhatsAppChatListItemStyle` in Unison theme: `CornerRadius 0`, horizontal margin `0` (keeps `0,2` vertical) for future swipe

---

## Unison light sent bubble `#79DB8B`

- Light theme sent bubble (and matching received media-circle fill / sent reaction chip) use `#79DB8B`

---

## Unison sent media button glyph `#00B090`

- Sent-bubble media circle glyph `#00B090`; background is a lighter gray (`#4A4A4A` dark / `#E8E8E8` light) for contrast

---

## Unison media circle buttons (audio + image/video/doc)

- Renamed `UnisonAudioCircleButtonStyle` / audio brushes → `UnisonMediaCircleButtonStyle` / `ChatDetail*MediaButton*Brush`
- `ChatMediaCircleButtonHost` + `UnisonMediaCircleButton` shared by audio, image, video, and document download
- WhatsApp image/video keep dark ellipse via `UseOverlayChrome`; Unison always uses the brand 30×30 circle

---

## Unison sent bubble `#007862`

- Sent bubble (and matching audio/reaction tokens) use `#007862` across Unison light/dark
- Audio buttons keep swap pattern: sent glyph / received circle fill track the sent bubble color

---

## Unison dark audio / sent bubble colors

- Unison round audio buttons **30×30**
- Dark normal: sent circle = received bubble + **glyph = sent bubble** (`#005C4B`); received circle = sent bubble + white glyph
- Keeps Unison round button size **30×30**
- PointerOver/Pressed: Unison green fill + white glyph

---

## Unison sent reactions: left-aligned

- Sent reaction chips use `ChatDetailSentReactionChipButtonStyle`: Unison **Left** (clears bottom-right tip); WhatsApp stays **Right**. Received chips unchanged (right of gutter / under bubble).

---

## Shell split: Unison vs WhatsApp audio chrome

- Dedicated Unison controls (`UnisonChatAudioBubbleBar`, `UnisonChatAudioActionButton`, `UnisonAudioCircleButtonStyle` + audio brushes only in Unison theme)
- WhatsApp keeps icon-only `ChatAudioBubbleBar` / `WhatsAppIconButtonStyle`
- Shared templates use hosts that **create** the active shell control (no Transparent brush stubs, no shared morph style)
- `ShellUi` + `ChatAudioTransportPresenter` shared helpers; bubble chrome host uses `ShellUi` too

---

## Unison audio buttons: round brand colors

- Play / download audio buttons are round (`UnisonAudioCircleButtonStyle`)
- Light + dark (for now): received = Unison green fill + white glyph; sent = white fill + green glyph

---

## Unison tips: first received, last sent

- Received (1:1 + group): tip on **first** of run — 1:1 up, group left toward avatar
- Sent (1:1 + group): tip on **last** of run, **below** (group no longer uses top-right side tip)

---

## Unison group tips: right triangle

- Group side tips were isosceles (`L0,6` mid-point); now right-triangle Paths like 1:1 (received left / sent right)

---

## Message runs: no mid-sequence Unison tips

- 1:1 sequences no longer split when `ParticipantJid` is empty vs LID vs PN — only the **last** bubble keeps the tip
- Group runs still match on normalized participant / sender name; default `IsRunEnd` is false until layout runs

---

## Chat list pins stay on top during sync

- Batch release applies ChatStore pin/mute **before** SortForDisplay (was sorting unpinned, then ApplyTo too late)
- While the list is already full mid-sync, VisibleChats is re-ordered in place so tip updates cannot bury pinned chats until you open one
- PN/LID dedupe carries `IsChatPinned` / `PinnedTimestamp` onto the surviving row

---

## Unison bubbles: tip on last of run, flush edges

- Unison tip only on **last** bubble of a consecutive run (`IsRunEnd`); WhatsApp still uses first (`IsRunStart`)
- Path tips flush to the bubble edge (no left/right/top inset)

---

## Unison bubbles: group vs 1:1 tails + preview reconcile crash

- Unison tails: **1:1** received tip up (top-left), sent tip **below** pointing down (right); **group** received tip left toward avatar, sent tip right at top
- `FindNewestInMemoryForChat` snapshots `MessagesByChat` / message lists so concurrent unload no longer throws mid-enumerate during SQLite preview reconcile

---

## Unison shell: square message bubbles

- New `UnisonMessageBubbleChrome`: **CornerRadius 0**, Path tails (received **top-left up**, sent **bottom-right down**); WhatsApp keeps rounded `MessageBubbleChrome`
- `MessageBubbleChromeHost` picks chrome from `SelectedShell` so templates stay shared

---

## Settings: Notifications section + toast sounds

- New **Notifications** settings section (glyph `E91C`) in Unison and WhatsApp shells; system notifications toggle moved out of General
- **Message notifications** / **Group notifications** ComboBoxes persist `NotificationSound` (`SystemDefault` for now); toast audio reads those keys (`Notification.Default`)
- Catch-up continue idle shortened to **15s** (poll 5s, reconnect delay 6s) for faster next-lot soft-reconnects

---

## FULL_HISTORY catch-up: faster continue cycles

- Idle quiet before continue: **35s** (was 90); poll **8s**; soft-reconnect delay **12s** (was 45); cooldown **15s**
- Max continue rounds raised to **60** so deeper backlogs can finish under the shorter cycle (~47s wait vs ~135s)
- Same path helps reconnect catch-up and long initial/history catch-up while the tip is still advancing

---

## FULL_HISTORY catch-up: continue soft-reconnect

- After a quiet FULL_HISTORY lot, compare the newest tip watermark: if still stale and the tip is advancing, **soft-reconnect** for the next offline batch while keeping **Synchronizing history…**
- Stop automatically after **2** stagnant cycles (tip did not move) or max continue rounds; enrichment then runs normally
- Replaces idle-abort → failure reconnect (20 min cooldown) as the primary deep-catch-up path

---

## FULL_HISTORY catch-up: progress-aware idle abort

- Replaced the fixed **2-minute** post-ack `full-history-no-payload` abort with an idle watchdog: abort only after **5 minutes without progress** (tips / SQLite chunks / offline apply / ack), polled every 20s; absolute hard timeout raised to **15 minutes**
- Progress stamps reset the idle clock; diagnostics journal adds `catch-up-watchdog-*`, `catch-up-progress`, `catch-up-idle-abort`, `catch-up-hard-timeout`
- Snapshot now shows FULL_HISTORY ack + last progress reason/time/signal count

---

## FULL_HISTORY catch-up banner + mobile diagnostics dialog

- After a freshness `FULL_HISTORY_SYNC_ON_DEMAND` request, the chat-list banner sticks on **Synchronizing history…** (`phase:historycatchup`) and ignores enrichment clears until SQLite quiet-finalize / idle abort / hard timeout / reject
- Hold or right-click the **Chats** header (same gesture on the login QR) opens a runtime diagnostics ContentDialog: snapshot + recent journal, **Save to disk** writes under `LocalState/Diagnostics` (picker fallback still available via Debug page export)
- Snapshot fields now include FULL_HISTORY pending id/trigger, catch-up banner latch, last history sync type/time, and SQLite chunk accumulation

---

## History SQLite delta apply

- History sync chunks no longer blindly rewrite every chat/message already in SQLite
- **Chats (`history_chat_preview`):** preload light lifecycle/tip columns; insert new JIDs fully; existing rows only update archived/deleted flags and tip when the incoming last message is newer
- **Messages (`history_message`):** `PreferDeltaSkipExistingBodies` preloads MessageIds for touched chats and skips existing bodies; pins / reactions / revokes still apply
- UI reload (`MessageChatJids`) only covers chats that actually got a new body or a side effect

---

## Reconnect: messages before names/groups

- After offline drain, post-replay work is **message-only** (SQLite Last Message reconcile); USync / groups / avatars no longer run on that path
- If freshness is still stale, reconnect requests `FULL_HISTORY_SYNC_ON_DEMAND` and **holds** name/group enrichment until that heavy sync (or until idle/hard abort)
- When already fresh, enrichment runs once after messages settle; freshness also reads `ChatItem.LastMessageTimestampUtc` so cold start is not stuck on empty `MessagesByChat`

---

## IJidResolver (WhatsAppService extraction, phase A)

- New `IJidResolver` in Core (`GetCanonicalJid`, `TryGetAlias`, `Self`); UWP `JidResolver` wraps the live client table
- Core helpers (`SelfIdentity`, `GroupParticipantLookup` / `GroupParticipantResolver` + `ParticipantResolutionContext`), ViewModels, UI (`ChatDetailView`, `ChatsView`, `ArchivedChatsView`, `CommentRichService`), and façades/helpers under `Contacts/` / `ChatFacade` / `MessageFacade` ask the resolver instead of `IWhatsAppService`
- `IContactService.ResolveDisplayName` added so mention/participant name lookup does not need the client
- `GetCanonicalJid` and `JidAlias` removed from `IWhatsAppService` (table stays on the concrete client until a later phase)

---

## Archived chats + persisted lifecycle

- `history_chat_preview` schema v6 persists `ChatStatus` (`Active`, `Deleted`, `Archived`) while retaining `DeletedAtUtc` for delete/revival ordering
- History bootstrap maps `Conversation.Archived`; inbound `ArchiveChatAction` persists archive/unarchive immediately
- New `archived` shell route uses standalone `ArchivedChatsView` (own XAML + master-detail layout, `ChatListScope.Archived`); shares `ChatListView` / `ChatDetailView`; active and archived lists update when status changes

---

## Shell strategy navigation (`feature/shell-strategy`)

- `NavigationDestination` enum in Core; `INavigator` takes destinations (not raw strings) for root/shell
- **Settings:** `OpenSettings()` — Unison page; WhatsApp `SettingsDialog`. Phone handset (`IsMobile && !Continuum`): `FullSizeDesired` + no `BackgroundElement` sizing (Imgur); desktop keeps 600/800 chrome + Overlay/Inline
- Other shell sections still use the strategy page map (`Chats` / `Status` / `Debug` stay under shared `UI/Views`)
- `IShellNavigationStrategy` + `ShellThemeService` own the Unison vs WhatsApp maps alongside chrome

---

## Delete chat (app state + local tombstone)

- Outgoing **`DeleteChatAsync`** on `IWhatsAppSocket` / `SocketBridge` → `AppStatePatchFactory.DeleteChat` (RC14 `chatModify({ delete })`)
- **`IChatService.DeleteChatAsync`** / `ChatFacade`: patch first (with message range), then local wipe; offline or empty timeline stays local-only
- Incoming / local wipe: `ApplyAppStateDeleteChatAsync` writes `ChatStatus.Deleted` plus `history_chat_preview.DeletedAtUtc` (schema v6) and deletes `history_message` for PN/LID keys so a restart does not resurrect the row
- Upsert of previews **keeps** the tombstone unless the incoming tip is newer than the deletion (new message brings the chat back)
- UI: list context menu, chat overflow (1:1 + group), and chat-info green bar (**Apagar conversa** after the two pin actions, 1:1 + group) — all confirm via `ChatDeletionPrompt` (strings in every shipped locale)

---

## Static UI copy: View → `x:Uid` (not ViewModel Loc)

- Pin/unpin and mute menus: two `MenuFlyoutItem`s with `x:Uid`; Opening only toggles Visibility (UWP MenuFlyout bindings are unreliable)
- Chat info pane: section labels, pivot headers, empties, ToggleSwitch On/Off, app-bar pin labels via `x:Uid` + Visibility on bools (`IsWidgetPinned` / `IsChatPinned`)
- Login Show/Hide log and Enable/Disable log: paired buttons + Visibility
- About description/branch and image/video download/share tooltips: `x:Uid`
- Left in ViewModels: presence, sync progress, dialogs, formatted counts, bubble copy

---

## German (`de-DE`) locale

- New UI pack `Strings/de-DE/Resources.resw` (parity with `en-US`; download draft + 92 missing keys filled)
- Registered in `AppLanguage.German = 9`, `AppLanguageInfo`, `Package.appxmanifest`, and `AppxDefaultResourceQualifiers` / `PRIResource`

---

## ViewModel slim-down (helpers)

- **`GroupParticipantLookup`** owns roster/name/avatar/1:1 indexes previously inlined on `ChatDetailViewModel`; VM keeps thin wrappers (`RebuildParticipantLookup`, `ResolveParticipantContactUri`) and timeline Mentions refresh
- **`MessageRunLayout`** owns run chrome, date chips, sender/quote labels, and contact slots; midnight refresh only rewrites separators
- **`SelfIdentity`** centralizes “is this JID me?” for You/Você labels (used by the participant lookup)
- **`ChatListDisplayOrder`** owns PN/LID dedupe + pin/timestamp/name sort; `ChatListViewModel` delegates instead of duplicating the merge logic
- Collaborators stay VM-owned (no new DI interfaces for these helpers)

---

## Bubble pin menu + clock format (i18n / Settings)

- Message long-press **pin / unpin** labels come from `.resw` (`ChatDetail_UnpinMessage`, `ChatDetail_PinFor24Hours`, `ChatDetail_PinFor7Days`, `ChatDetail_PinFor30Days`) in every shipped locale — no hardcoded PT-BR in `ChatDetailView`
- Settings **Customization:** clock after device time-zone conversion is **24-hour** or **12-hour (AM/PM)** (`TimeFormat` / `LocalSettingsConstants.TimeFormat`). Storage and tip compare stay **UTC**; only UI clocks (`WhatsAppMapper.FormatClock` / `LocalTimeConverter`) read `WhatsAppMapper.CurrentTimeFormat`
- **Fix (message pin persistence):** live pin/unpin now writes `history_message` immediately via `UpsertPinsAsync` (PN+LID keys + MessageId fallback). Body upserts preserve an existing pin unless the row already carries `IsPinned=true` or `WritePins` clears it — history sync `InsertOrReplace` no longer wipes the banner after a successful pin
- **Fix (chat-list pin persistence):** `ChatStore.UpsertAsync` already saved `IsChatPinned`, but `ApplyTo` ignored it (comment said “history is source of truth” while `history_chat_preview` has no pin columns). `ApplyLocalFields` now restores chat-list pin from SQLite on list hydrate so a restart does not wait for the next `pin_v1` app-state sync

---

## Chat list: Last Message stays in sync with SQLite

- **Rule:** load newest `history_message` for the chat by **TimestampUtc** (PN+LID keys); if that tip’s **MessageId** differs from `ChatItem.LastMessageId` (or body/fromMe), swap the strip — never on a strictly older timestamp
- **`LastMessageId`** on `history_chat_preview` (schema v4). No WhatsApp history resync: first reconcile after upgrade stamps Ids from `history_message` (ALTER COLUMN + backfill)
- **Startup:** reconcile Last Message **before** name/photo resolution
- Reconcile also considers in-memory tips; opening a chat picks newest tip by timestamp then reconciles
- **Fix (UTC kind):** `ChatMessageOrder.ToComparableUtc` now matches `WhatsAppMapper.ToUtc` — SQLite `Unspecified` is UTC wall-clock, not local. The old `ToUniversalTime()` path shifted Brazil UTC−3 by **+3h** into `LastMessageTimestampUtc`, so the strip looked “newer” than the real tip and reconcile kept the stale Last Message. Reconcile force-applies the chosen tip so already-poisoned strips heal
- **Sent checkmarks** on the Last Message strip follow `LastMessageSendState` (same send-state vocabulary as bubbles)

---

## Chat detail: bubble cost (layout, tree, decode)

- **Participant resolve no longer walks every chat per bubble.** `RebuildParticipantLookup` indexes 1:1 avatars/names once; `GroupParticipantResolver` uses that map instead of scanning `Chats`. Roster `AvatarUrl` PropertyChanged patches visible `ContactUri` as hydrates land
- **`MergeTimelineFromService` is O(n)** via an id→row dictionary (was FirstOrDefault per service message). Midnight date chips only rewrite separator fields — no full run/avatar pass
- **Message templates use `x:Load`** for quote, media grids, sticker, caption, audio, document, body, and read-more — text bubbles no longer build 300×300 download trees. Dead `MessageBubbleChrome` ContactUri/ShowContact bindings removed
- **`StringToImageSourceConverter` caches** BitmapImage by URL+decode width (cap 96). Quote/document/`CanExpand` getters on `ChatMessageViewModel` are memoized. Unused `ChatMessage.ReactionChips` getter removed; no-op `FillTimelineThumbnailsAsync` gone

---

## Reactions: back to one path for Mobile and desktop (revert of the summary mode)

- **Timeline open loads the reactor rows again.** `AttachReactionsAsync` fills `HistoryMessage.Reactions` with one batched `ChatJid IN (…) AND MessageId IN (…)` query per 80 ids, explicit columns, oldest reactor first. Gone: `ReactionSummarySelectSql`, `COUNT(*)` / `GROUP_CONCAT` aggregation, `ReactionSummaryRow` / `ReactionEmojiRow` and the in-process fallback
- **One shape in the models.** `HistoryMessage.ReactionTotal` / `ReactionSummaryText` removed; `ChatMessage.Reactions` is the single source for `HasReactions` / `TotalReactions` / `ReactionsDisplayText` / `ReactionChips`, so `ApplyReactionSummary` and `ReactionsBuilder.BuildEmojiLineFromSummary` are gone. `ReactionMapper` always edits the list (no `SoftApplyToSummary`)
- **`AreReactionDetailsLoaded` kept, with a narrower meaning:** true only when the list came from the store. It is what scopes the reaction `DELETE` in a live upsert, so a live-only object still cannot wipe stored reactors
- Cost is back where it was before the optimization: opening a big group reads every reactor of the page (still one query per batch, not N+1)
- Unchanged from the previous fix: on-demand threshold follows `_sqlOpenPageSize`, and live reaction envelopes persist additively via `UpsertReactionsAsync`

---

## Reactions chips missing on Mobile: live upsert was deleting the rows (fix)

- **Live upsert no longer clears reactions of chip-summary rows.** `HistoryMessageWriteBatch.ReactionOwnerMessageIds` lists only the ids whose reactor rows the batch actually carries (`AreReactionDetailsLoaded`); `ClearReactionsForMessages` deletes just those. Before, any receipt/pin/state flush of a summary-only row ran `DELETE FROM history_message_reaction` and rewrote nothing, so `COUNT(*)` was 0 on the next open
- **Timeline merge carries the summary.** `ApplyLiveFieldsTo` assigned only the reactor list, never `ReactionsDisplayText` / `TotalReactions`, so a row already on screen could never receive a chip from a later SQL page — `HistoryMessageMapper.CopyReactionState` now applies details or summary (and never blanks a chip a partial read cannot confirm)
- **On-demand threshold follows the page size** (`_sqlOpenPageSize - 5`): the fixed `40` meant the 30-row Mobile page was always "thin", so every chat open asked the phone for history and ran an extra sync/merge/persist cycle — the cycle that tripped both bugs above on Mobile and never on desktop (50-row page)
- **Live reaction envelopes are durable again.** `IHistoryMessageStore.UpsertReactionsAsync` writes the single reactor row additively (empty emoji still removes it), so a reaction landing on a summary-only bubble survives a restart and can even arrive before its parent message
- Reaction rows already deleted on device do not come back on their own; they return with the next reaction, on-demand chunk, or resync (history-sync persist is additive)

---

## Reactions chips missing on Mobile (fix)

- Live/client rows winning the open merge dropped SQLite reaction summaries — `CopyReactionsIfMissing` keeps the chip when the winner has none
- Reaction attach falls back to an explicit `MessageId, Emoji` query + in-process tally when `GROUP BY` / `GROUP_CONCAT` fails or returns empty (common on older Mobile SQLite)
- `COUNT(*)` mapped as `long`; `GROUP_CONCAT` no longer uses `DISTINCT` (optional; fallback still dedupes)

---

## Sync StatusBar: no sticky settling / “0 of N” over open chats

- Opening a chat (`SetActiveChatJid`) clears the global sync banner so Mobile StatusBar does not keep “Finishing startup…” / list enrichment over chat detail
- List enrichment phases (`settling` / `names` / `avatars` / `groups` / `lowmemory`) are suppressed while a conversation is active; work still runs in the background
- Early exits from post-replay / background resolution / cancelled quiet-wait always `RaiseSyncStatus(null)` so settling cannot stick forever
- Avatar batch no longer reports `0 of N`; progress starts after the first completed fetch. UI also strips zero-current counts to bare phase text

---

## Reactions: summary on open, details on dialog (no SELECT *)

- Timeline attach uses `GROUP BY MessageId` with `COUNT(*)` + `GROUP_CONCAT(DISTINCT Emoji)` instead of loading every reactor row
- Bubble chip binds cached `ReactionsDisplayText` / `ReactionTotal`; full rows load in `MessageReactionsViewModel` via `GetReactionsForMessageAsync` (explicit columns)
- Live reaction updates on summary-only bubbles soft-adjust the chip without wiping other reactors; dialog always reloads from SQLite
- Pinned / media history queries use `TimelineSelectSql` (no `SELECT *`)
- **Fix:** dropped `GROUP_CONCAT(DISTINCT …, ' ')` (needs SQLite 3.44+); that SQL failed on UWP/Mobile and aborted the whole history page, leaving only the list-preview placeholder. Summaries now use comma separator; attach is try/caught so reactions never block the timeline

---

## Mobile performance: timeline windows, selective avatars, roster merge

- **Timeline windows by device** (`ISystemInfoProvider`): chat open UI window is **30/80** on Mobile vs **50/150** on desktop; SQLite open/load-more pages are **30/20** vs **50/30** (`ChatDetailViewModel`, `MessageFacade`)
- **Group member avatars not on open**: `RefreshGroupSendPermissionsAsync` uses `hydrateAvatars: false`. Visible bubble authors hydrate via `HydrateGroupMemberAvatarsForJidsAsync` / `GroupRosterPolicy.HydrateVisibleAsync` (no full-roster next-batch). Full roster hydrate waits for the Members pivot (`EnsureMembersAvatarsHydratedAsync` + `IsMembersAvatarsLoading` progress)
- **Roster diff**: when metadata returns the same JID set, `ApplyGroupMembersToChat` merges name/avatar fields in place instead of replacing `GroupMembers` (avoids PropertyChanged relayout)
- **Mobile `CacheLength`**: message `ItemsStackPanel.CacheLength = 0.5` after list load / chat open (default is much higher)
- **Selective `RefreshMentions`**: run layout only refreshes bubbles with `HasMentions` (skips the common no-@ case)

---

## Lighter “Loading photos…” batch (Mobile)

- Avatar batch no longer calls `SchedulePersist` after every download — one debounced write at the end of the batch
- Sync-status banner updates are throttled (every few items + start/end) instead of rewriting the StatusBar on each photo
- `HydrateCachedAvatarUris` probes disk off the UI thread and applies URIs in one dispatcher pass
- Startup batch fetches preview only (`fetchHighQuality: false`); high-res group art waits for visible-row refresh
- Mobile uses a smaller batch (8) and a shorter inter-request delay (400 ms) than desktop

---

## Protocol thumbs behind the download placeholder

- History `_thumb` URIs map to `ThumbnailUri` (images) / `VideoPosterUri` (video), not full `ImageUri`/`VideoUri`, so `NeedsImageDownload` stays true and the bubble can still offer CDN download
- Sent/received download overlays show the protocol thumb (or video poster) under the download button; the generic placeholder glyph only appears when no thumb is on disk

---

## Transparent message ListViewItem (no white recycle flash)

- Timeline `MessageListView` used the default opaque `ListViewItem` chrome, so virtualization recycle flashed white blocks over the tiled wallpaper while scrolling up. Containers are now a transparent `ContentPresenter`-only template (same idea as Unigram’s wallpaper-friendly history items); the list itself is `Background="Transparent"`

---

## Timeline load progress + scroll lock

- `IsLoadingMore` already gated load-more (`CanLoadMore`); first open now sets `IsLoadingMessages` via `BeginLoadingMessages` / `EndLoadingMessages`. `IsTimelineBusy` drives a 2px indeterminate `ProgressBar` on the bottom edge of the chat header
- While busy, vertical scroll on the message list is disabled (and `ViewChanged` ignores load-more) so Mobile does not stack flings on top of materialization; scroll is restored when the load finishes

---

## Dropped MediaThumbnailBase64 from SQLite

- Removed the fat `MediaThumbnailBase64` column/property from `history_message`, `history_status`, `ChatMessage`, mappers, and UI. Protocol thumbs live only as `MediaCache/Images/*_thumb` URIs (`MediaLocalUri` / `MediaPosterUri` / `ThumbnailUri`)
- On init, stores attempt `ALTER TABLE … DROP COLUMN MediaThumbnailBase64` (schema message **7**, status **2**). Chat-info preview is URI-only; `Base64ToImageSourceConverter` deleted

---

## History thumbs on disk + light group participant lookup

- History sync no longer stores protocol `jpegThumbnail` as base64 in `history_message`. Bytes are written to `MediaCache/Images/*_thumb` and the URI goes on `MediaLocalUri` / `MediaPosterUri` (full media is never overwritten by a thumb). Timeline open skips the fat second thumbnail query
- Opening a large group no longer runs `ResolveDisplayName`/`ResolveAvatar` for every roster member. `RebuildParticipantLookup` only indexes names/avatars already on the roster; the full resolver runs on demand for JIDs that appear on visible bubbles. `PersonGroup` roster persist uses one transaction instead of N async inserts

---

## Chat open: fewer SQLite round-trips + participant lookup cache

- Opening a conversation used to call `GetForChatAsync` once per PN/LID/canonical key (timeline + thumbs + reactions each), then resolve every group author name by walking the roster. `GetForChatKeysAsync` loads with `ChatJid IN (...)` in one query; open page size is **50** (matches the UI window). Live `ChatMessagesChanged` uses `LoadRecentMessagesForSyncAsync` (RAM + 30-row SQL tail, no pinned/pending extras)
- `ChatDetailViewModel` builds `participant name/avatar` dictionaries once from the roster (`RebuildParticipantLookup`) and injects them into run layout / sender labels / avatars so bubbles hit `TryGetValue` instead of resolving per message

---

## Composer grows upward while typing

- Chat detail `MessageInput` mirrors Unigram: `TextWrapping="Wrap"`, `MinHeight="40"`, `MaxHeight="192"`, `VerticalAlignment`/`VerticalContentAlignment` Bottom so the box expands upward; attach / mic / send stay Bottom on the single-line baseline. Enter sends; Shift+Enter inserts a newline. Starting a voice note clears `MessageText` so the box collapses before the recording overlay

---

## Mark-read no longer rewrites the chat catalogue

- Opening a chat called `ClearUnreadForChatAsync` → `SchedulePersist` → `PersistDataAsync`, which upserted **every** `history_chat_preview` row and rewrote three contact JSON maps, then published `OnSyncStatus("Saving chats...")`. On Mobile (Unison theme) that landed on the StatusBar and contended with SQLite message load on eMMC — Unigram stays fast because TDLib patches one chat and the UI only updates that row
- `ClearUnreadForChatAsync` now no-ops when no alias row had unread, otherwise upserts **only the dirty rows** via `PersistChatCatalogSliceAsync` / `PersistChatListRowsPublic`. Preview refresh after open uses the same slice path instead of a full `SchedulePersistPublic`
- Routine `PersistDataAsync` no longer raises `"Saving chats..."`; sync phases already report through `SyncPhaseStatus`

---

## Chat list filter flyout

- Filter menu items bind `FilterChatsCommand` with integer `CommandParameter` values that map to `ChatListFilter` (`All = 0` … `Drafts = 6`). UWP does not pass enums reliably from XAML, so the command is `RelayCommand<int>` and the view model casts after `Enum.IsDefined`
- `RefreshVisibleChats` applies the active filter with LINQ before the search box filter, so both compose with AND. Incremental list patches fall back to a full rebuild while a non-All filter is active
- `ChatItem.IsFavorite` and `HasDraft` currently return false (stubs) so Favorites / Drafts ship empty until those features exist. Contacts / Non-contacts use the address-book overlay (`PhoneContactNamesByJid`); Groups uses `IsGroup`; Unread uses `HasUnread`

---

## Startup phases are visible and localized

- The service published finished English sentences through `OnSyncStatus` ("Fetching contact names…"), which the chat list showed verbatim — the only part of the UI that never translated. A phase now travels as a `SyncPhaseStatus` token (`phase:names:12/40`) and `ChatListViewModel.TranslateSyncPhase` is the single place that turns it into words. Anything that is not a token still passes through untouched
- Five phases that ran silently now report: settling after replay (`ChatList_Settling`), name resolution, avatar fetch and group metadata (`ChatList_ResolvingNames` / `_FetchingAvatars` / `_FetchingGroups`, each with a running count), and the low-memory pause (`ChatList_PausedLowMemory`). All nine packs carry the keys
- Post-replay maintenance no longer sleeps a flat 25 s on Windows Mobile. `WaitForStartupQuietAsync` polls every 500 ms and stops as soon as safe mode and the replay drain are both clear, past a 3 s floor (1 s on desktop); the ceilings are 8 s / 10 s / 6 s
- Memory pressure no longer abandons enrichment on the spot. `WaitForMemoryHeadroomAsync` retries at 10 s / 20 s / 40 s and only gives up after the last one, so a device that was briefly above the low watermark still gets its names. `TriggerBackgroundResolution` also runs on Mobile now — it was desktop-only, which is why Mobile never showed those phases at all
- `RuntimeDiagnosticsService` gains a `startup-phase` category: begin/end per phase with elapsed ms, `AppMemoryUsageLevel` at both ends, plus `quiet-wait`, `memory-retry` and `memory-abandoned` records

---

## Avatars decode at the size they are drawn

- `BitmapImage(Uri)` starts decoding in the constructor, so a `DecodePixelWidth` assigned in the object initializer that follows arrives too late and the full-resolution frame is decoded **on the UI thread**. `ChatAvatarControl`, `StringToImageSourceConverter` and `TiledBackground` all had that shape — a 640-square group photo cost more than the info panel around it, and the tiled background ignored its 256 px Mobile cap. All three now set the decode properties first and assign `UriSource` last
- `ChatAvatarControl` asked for `size * 2` under `DecodePixelType.Logical`, which is already display-scaled — four times the drawn area. It now decodes at `size`, and remembers the applied URL so `ApplyVisual` (which runs for any visual property change) stops re-decoding an unchanged picture

---

## Live media rows no longer store preview tags

- `HistoryLiveMessageMapper` ran `ChatMessage.Content` into `history_message.Body` verbatim, so a live sticker / captionless image / video landed as `[Sticker]` / `[Image]` / `[Video]`. On read-back `HistoryMessageMapper.ApplyMediaEnvelope` promotes a non-empty body to `ChatMessage.Caption`, which is what the media bubble renders — history-sync rows were clean because they already went through `ChatPreviewNormalizer.NormalizeBody`
- Live writes now normalize body and quoted body the same way; reads normalize again so rows written by older builds stop showing the tag without a schema bump or resync

---

## Chat-info Media / Files load on tab

- Opening profile / group / member info no longer queries `history_message` for media. `EnsureMediaIndex` runs when the **Media** or **Files** pivot is selected (one shared SQLite index for both)
- Those panes show a centered `ProgressRing` while `IsMediaIndexLoading` is true; empty copy stays hidden until the first page lands. Members still binds the existing `ChatItem.GroupMembers` list

---

## Timeline SQLite open 100 + indexes

- Opening a chat reads the newest **100** `history_message` rows (`MessageFacade.SqlOpenPageSize`; was 200). UI still paints `InitialUiMessageWindow` **50** bubble VMs; load-more stays 30; cap stays `MaxUiMessageWindow` 150. Pinned + pending outgoing still merge from SQLite if they sit outside that page
- Schema **5**: composite indexes `ix_hm_chat_ts` `(ChatJid, TimestampUtc, MessageId)`, `ix_hm_chat_pin` `(ChatJid, IsPinned, PinnedAtUtc)`, `ix_hm_chat_kind` `(ChatJid, Kind, TimestampUtc)`. Open and load-more use the same `ORDER BY TimestampUtc DESC, MessageId DESC`
- Timeline SELECT omits `MediaThumbnailBase64`; image / video / sticker / document rows of that page fetch it in a second query. Chat-info Media / Files still `SELECT *`

---

## Group @number mentions in bubbles

- SQLite timeline dropped proto `ContextInfo.MentionedJid`, so group bubbles kept `@5511…` / `@…@lid` as plain text. `history_message` stores those JIDs (schema **4**); `history_chat_preview` stores them for the list strip (schema **2**)
- `ChatItem.GroupMembers` stays the list for chat-info Members. `ChatItem.MentionLookup` is a digit→name dictionary rebuilt when that roster is replaced (`MentionLookupBuilder`). Bubbles and the list strip bind that map — they do not walk every group on each parse
- `CommentRichService` consumes `@digits` **and** `@digits@lid` / `@s.whatsapp.net`. A JID user-part is not treated as a display name

---

## Show Unison contacts in Windows

- Settings toggle **Show Unison contacts in Windows** (`PublishContactsToWindowsEnabled`, default **off**). When on, **all 1:1 chats** (LID included; phone filled from canonical/PN/`Person` when known) go to a Unigram-shaped People account: `UserDataAccount` (`AppAccountsReadWrite`) + `CreateContactListAsync(name, account.Id)` + `ContactAnnotationList` on that account — not a bare app `ContactList` (People ignores those) and not the user agenda
- Name is `FirstName` / `LastName` (not `Contact.Name`). Photo is `SourceDisplayPicture` from a local `StorageFile` (not `Thumbnail`, not a raw `ms-appdata` URI). `RemoteId` is the JID with `@` replaced (`w5511….s.whatsapp.net`) so `SaveContactAsync` does not reject it. Annotations use `ContactProfile | Message | AudioCall` (+ Share) and `ContactPanelAppID` / `ContactShareAppID`
- Publish **upserts** and does not delete the rest of the list on a partial snapshot (chats still loading). Name/avatar refresh retries while the first pass was empty; otherwise at most every 30s. Logout or turning the toggle off **deletes the `UserDataAccount`**. One failed `SaveContactAsync` does not abort the batch. Account / list / annotation ids are `PublishWindowsUserDataAccountId` / `ContactListId` / `AnnotationListId`

---

## Chat info full screen under 800 epx

- Opening user, group, or member info uses `ChatDetailInfoStates`: `InfoFullScreen` when `ChatDetailView` is narrower than 800 epx (400 chat + 400 info), otherwise `InfoDocked` (400-wide column). Threshold is the detail pane, not the window. Closed state is `InfoClosed`

---

## Add contact (People card)

- **Adicionar contato** also appears on the green chat-info command bar (`ChatDetailInfoControl`) when `CanAddToAddressBook` — 1:1 with a phone, not groups. Same command as the link under the number
- Phone on the info pane uses `IContactService.TryResolvePhone` (not only `TryPhoneFromJid` on a LID). Overlay name maps still prefer the user agenda; they skip Unison-owned lists so publishing to Windows does not hide Add
- ViewModels call `IContactService.CanAddToAddressBook` / `ShowAddToAddressBookAsync`. WinRT stays on `ILocalContactsService`. Desktop / Continuum opens `ShowFullContactCard` (a People window, not the light-dismiss flyout that Windows 11 closes with the menu). Mobile still uses `ShowContactCard` after UI idle, anchored to the focused control. Agenda overlay refreshes when the app is foreground again — not in the same turn as the click
- `ms-people:` only if showing the card throws. LID-only people (no phone) do not get the action. The group overflow menu does not

---

## Group member pictures in idle batches

- `GroupRosterPolicy` (on `IContactService`) fetches member photos **16 at a time**, then schedules the next 16 — not a one-shot on open and not a migration table
- `GroupMember.AvatarFetchedAtUtc` is stamped on a hit **and** on a confirmed miss (`no-picture`), so people without a photo are not asked again for 7 days. Timeouts use a 30-minute backoff
- Picture IQs prefer the phone JID. Opening a group also harvests LID↔PN pairs from interactive metadata (the participating listing already did)

---

## Timeline date chips (Hoje / Ontem / date)

- First bubble of each **local** calendar day shows a centered pill (`ChatDateSeparator`) above the row — not a synthetic list item. Flags `IsFirstOfDay` / `DateSeparatorText` are layout-only on `ChatMessage`, set in `ApplyMessageRunLayout`
- Labels: `Common_Today` / `Common_Yesterday` / culture short date (`d`). Midnight relabel is a `DispatcherTimer` on `ChatDetailView` (Core stays free of WinRT clocks)
- Fill is `ChatDetailDateSeparatorBackgroundBrush` (same as wallpaper underfill). Bold text uses `ChatDetailDateSeparatorTextStyle` → `ChatDetailDateSeparatorForegroundBrush`

---

## Bubble time uses the device time zone

- Chat stamps (send and receive) are stored as GMT 0 / UTC. `LocalTimeConverter` maps them to `TimeZoneInfo.Local` on the bubble. SQLite Unspecified Kind is treated as UTC

---

## Chat list "Updating..." stuck after connect

- Header "Updating..." is `IConnectionService` status `open`; it only clears on `synced` (offline drain). The safety timeout now emits pending-notifications so the banner cannot stay forever when `ib/offline` never arrives
- Debug log `[ChatList/Sync]` names the façade that last changed the banner (`IConnectionService`, `IHistoryService`, …)

---

## Live messages + chat list in SQLite

- Send/receive/outbox and media/pin/revoke/reaction updates upsert `history_message` (no per-chat JSON rewrite). Outgoing pending/failed rows stay in that table until they complete
- Chat catalog persist is `history_chat_preview` (`SyncType=live`, no `ChunkPersisted` storm). Startup loads that table instead of `chats.json`. `SaveChatsAsync` is unused
- Open chat still overlays RAM; pinned + pending outgoing are merged from SQLite if they sit outside the newest SQL page

---

## Timeline open window 50

- Opening a chat materializes `InitialUiMessageWindow` **50** bubble VMs (was 80). SQLite open page was 200 (now 100; see above); load-more stays 30; cap stays `MaxUiMessageWindow` 150

---

## History SQLite = timeline (quote, pin, revoke, reactions)

- `history_message` schema 3: quote snapshot, pin timestamps, `IsRevoked`, local media URI / poster. New 1:N table `history_message_reaction` (one emoji per reactor; empty emoji deletes the row)
- History chunks persist those side-effect envelopes into SQLite; JIDs are stored normalized (envelope LID or PN, not canonical)
- Open chat and load-more read SQLite (`GetForChatAsync` with a timestamp cursor, page of 30). JSON message files are no longer the history source — do a conversation resync after updating
- Live RAM overlay on open still covers the current session (send/receive before the next chunk)

---

## WhatsAppService phase 0 — dead code out, partials in

- Deleted the unreachable legacy JSON history apply (`ProcessHistorySyncBodyAsync` + `StoreConversationTcTokenAsync` + `ApplyHistoryConversationPin` + `UseHistorySqliteApplyPath`). `ProcessHistorySyncCoreAsync` now only forwards progress to the SQLite path
- Deleted leftover duplicates that were never in the container: `MessageService` / `ContactService` / `ConnectionService` / `ProfileService` and the extra `DebugSendService` next to the façades (`Diagnostics/DebugSendService` stays)
- `SettingsViewModel` no longer takes unused `IWhatsAppService` (logout already goes through `IConnectionService`)
- The compatibility client is now `partial`: `WhatsAppService.cs` keeps fields/send/history notify, with Connection / Media / Groups / Avatars / Identity / AppState / Persistence / Receipts / IncomingPump files beside it — same type, no behaviour change, so the next extractions are diffs against one cluster
- Full leftover leaks + phases 1–4: [WhatsAppService extraction](WhatsAppService-Extraction)

---

## Open chat paints first, then messages

- `ChatsView` used to `await SetActiveChatAsync` (SQLite + bubble VMs) **before** `ApplyChatPaneState`, so a tap stayed on the list until history returned — especially noticeable on Mobile
- Open is now two steps: `PrepareActiveChatAsync` shows header/composer and empty wallpaper immediately; the host switches to NarrowDetail; `CompleteActiveChatLoadAsync` yields one frame (48 ms extra on Mobile) then loads the window
- Mark-read no longer blocks first paint (`MarkChatOpenedAsync` is fire-and-forget after chrome)

---

## History sync status banner (SQLite path)

- After the SQLite history path landed, each chunk called `PublishInitialSyncProgress(active)` and immediately `completed` in the same method, then raised `HistorySyncReceived(null)`, which cleared the chat-list banner — so the UI looked frozen with no “Syncing conversations…” feedback during import
- `NotifyHistorySqliteChunkApplied` now accumulates conversation counts across chunks, keeps safe-mode/progress active, and only finalizes after a quiet period (~2.8 s, or ~0.9 s after a Full chunk)
- `NotifyHistorySqliteChunkStarted` fires before SQLite writes so the banner appears while Mobile is still persisting
- `ChatListViewModel` no longer treats null `HistorySyncReceived` as “sync over” while safe-mode or preview hydrate is still running; when the preview queue drains after finalize, the batch banner completes and clears

---

## Open chat lands at bottom

- Switching conversations reused the same `ListView`/`ScrollViewer`, so the previous `VerticalOffset` survived `Clear` + `ReplaceTimelineWindow` — opening chat B mid-timeline looked like the position from chat A had stuck
- On open: zero the offset after clear, arm stick-to-bottom / load-more suppress for ~2.5 s, then `ScrollToBottom` plus a few deferred retries until `IsNearBottom` (or the load is cancelled by another switch)

---

## Media tab Creators Update crash

- Opening the Media pivot on Windows 10 Mobile / Creators Update (`10.0.15063`, the package `MinVersion`) closed the app: `ChatInfoMediaPane` set `CornerRadius` on `ChatInfoMediaTile` (`Control.CornerRadius`, UniversalApiContract **7.0** / 1809) and the tile root was a `Grid` with `CornerRadius` (same late API). The XAML parser throws as soon as the pivot materializes the template — the build already warned `WMC0151` for that line
- Fix: drop `Control.CornerRadius` from the DataTemplate; round the tile with `Border.CornerRadius` (contract 1, safe on 15063). `Border` on chrome buttons elsewhere was already fine

---

## Reactions viewer

- Tapping the reaction chip under a bubble opens `ShowReactionsDialogAsync(ChatMessageViewModel)` on `IDialogService` — the command was a stub until now. `ChatMessageViewModel.ShowReactionsAsync` swallows dialog failures so a bubble tap can never crash the timeline
- `MessageReactionsViewModel` (Core, transient) builds the dialog: title (`Reactions_TitleOne` / `Reactions_TitleMany`), the per-emoji tally through the existing `ReactionsBuilder.BuildChips`, and one row per reactor
- Reactor identity comes from `IPersonStore`, not from the reaction envelope: each `ReactorJid` is looked up under its canonical and normalized form (history files the LID it saw, a later usync files the phone JID), falling back to the envelope's `ReactorName`, then to the number. Lookups are memoized per dialog, so reacting twice does not query twice
- The phone line is suppressed when it would only repeat the name — an unnamed reactor already shows their number as the name
- `ReactionsDialog` renders chips side by side on `ReactionsDialogChipBrush`, a fill added to both themes' Default/Dark/Light dictionaries. Light cannot be lightened past a white dialog, so there the chip reads as a raised grey panel instead

---

## Group author strip on history sync

- History chunks put push names in `HistorySync.Pushnames`, not on each envelope, so `WebMessageInfo.PushName` is usually empty. The old code read only that field (plus a hardcoded `"You: "`), which is why a synced group row showed the kind chip with no name while the kind — read from the message body — always worked
- `HistorySyncContentFilter` no longer decides authorship: `TryGetListableContent` just extracts body/kind/timestamp, and the new `BuildPushNameMap` / `ResolveSenderName` / `ResolveParticipant` (moved from `HistoryMessageBuilder`) answer who wrote it. Preview and message builders now share one implementation
- `HistoryChatPreviewBuilder` composes the strip through `ChatPreviewNormalizer.FormatListAuthorPrefix`, so it inherits the existing fallback to a short participant label instead of dropping the strip
- `HistoryMessageBuilder` fills `SenderName` from the same map — SQLite timeline rows were also landing nameless
- `history_chat_preview` gained `LastMessageIsFromMe` / `LastMessageSenderName` / `LastMessageParticipantJid` (sqlite-net adds missing columns on `CreateTable`, so existing DBs migrate silently). `HistoryChatPreviewApplier` recomposes the strip in the current UI language instead of replaying a label frozen at sync time, and never blanks an author the live path already resolved
- `ChatItem` carries the same three parts so the strip can be recomposed later from the raw sender identity

---

## Group author strip resolves independently of the chat list

- `IChatAuthorProjection` (`ChatAuthorProjection`, Core singleton started at app init) owns strip recomposition. It listens to `IPersonStore.PersonChanged`, `IChatStateStore.DisplayNamesChanged` and `Chats.CollectionChanged`, and rewrites `ChatItem.LastMessageAuthor` when a name arrives — the strip catches up even when the chat list is not the active screen (the old `ChatListViewModel.RefreshGroupAuthorStrips` only ran while the list VM was attached)
- New `IPersonStore.PersonChanged` (payload: normalized JID) is raised after a committed `UpsertIfChangedAsync` write, so a roster/usync/address-book resolution pushes the exact JID instead of only the coarse `DisplayNamesUpdated`
- Name-arrival bursts during sync are coalesced into one sweep per UI turn; resolution still tries the resolved-name map, the Person cache and the 1:1 chat, across the JID's own and canonical (LID → PN) forms, and refuses a label equal to the JID's own digits
- `ChatListViewModel` no longer resolves author strips or depends on `IPersonStore`; it just re-renders visible rows on `DisplayNamesUpdated`
- Event-driven alone was not enough at launch: both maps the sweep reads start cold. `IPersonStore` only caches a JID once someone asks for it, and the contact-name sidecar is loaded by deferred maintenance (25 s on Mobile, skipped entirely when the window is hidden or memory is not `Low`) with a direct dictionary write that raises no event. A strip synced in an earlier session would sit on a bare LID until an unrelated write happened to warm the cache
- So the sweep now feeds itself: participants it cannot name in memory are read from the `Person` table off the UI thread, and a re-sweep is scheduled if any row came back. Attempts are tracked per JID, so a participant with no row is queried once per launch rather than on every sweep

---

## Chat timeline keeps position on load-more

- `MessageListView` still declares `ItemsStackPanel ItemsUpdatingScrollMode="KeepItemsInView"`, but that alone failed near the top: variable bubble heights, many one-by-one `Insert`s, and a `VerticalOffset` already close to 0 left the viewport on the newly prepended rows
- `ChatDetailView.LoadMoreMessagesAsync` now captures the first realized bubble intersecting the viewport (and its Y in the ScrollViewer) **before** the fetch, then after prepend + run layout restores with `ScrollIntoView` and a fine-tune via `TransformToVisual` (extent-delta fallback when the container is not ready yet)
- Suppress starts before the await (1.5 s) so a settled `ViewChanged` cannot start a second load-more while the first is still in flight; after a successful prepend it is re-armed for 800 ms
- `ScrollViewer_ViewChanged` still ignores intermediate events so load-more does not fire mid-fling

---

## Chat detail — leak fix, one timeline path, legacy pruning

- `ChatDetailView` subscribed five ViewModel events in its constructor and only unhooked `PropertyChanged` on `Unloaded`, so the ViewModel (kept alive by `ChatItem.PropertyChanged`) held the whole visual tree after Back. Subscriptions now pair `Loaded` → `AttachViewModelEvents` / `Unloaded` → `DetachViewModelEvents`, and the handlers are named methods so they can actually be removed
- Timeline mutation lives in `ChatDetailViewModel` only: `MergeTimelineFromService` (strip preview bubbles → refresh visible rows → ordered insert → trim to `MaxUiMessageWindow`), `ApplyPreviewFallback`, `StampGroupRemoteJid`, `InsertTimelineMessage`, `TakeLastWindow`. Code-behind keeps what XAML cannot do: scroll, run layout, pinned banner
- The dead `ChatDetailViewModel.SetActiveChatAsync` / `AppendLiveMessages` / `TryApplyPreviewFallback` / `IsHistoryOnDemandPending()` are gone — opening a chat has one owner (the view), so there is no second copy to drift
- `_messageService != null ? façade : _whatsAppService` fallbacks removed; `IMessageService` is a required constructor dependency
- Removed with no call sites: `ChatItemViewModel` + `IChatItemVmFactory` (chat rows bind `ChatItem` and get local state from `IChatStore.ApplyTo`), `IMessageStore.LoadChatsBackupAsync`, the four granular `IChatStore` setters (everything writes through `UpsertAsync`), `IHistoryMigrationStore.IsSucceededAsync` (`GetAsync().IsSucceeded` already says it)
- Still there on purpose: `IHistoryChatPreviewStore.GetAllAsync` (the read a cold start from SQLite needs)

---

## History bodies no longer truncated to 50 chars

- `HistoryMessageBuilder` / `HistoryStatusBuilder` were running the chat-list preview normalizer on the message body, so every SQLite history row was stored capped at 50 chars + `...` with line breaks flattened
- New `ChatPreviewNormalizer.NormalizeBody`: same placeholder stripping, no cap, keeps line breaks. `Normalize` stays the one-line preview for chat list / quotes
- Rows written before this keep the cut text (the rest was never stored) — full text returns when the phone re-delivers those messages
- The bubble `...` at 12 lines is a separate, intentional collapse (`ContentMaxLines` + Read more)

---

## Chat info Media / Files — right source, paged tiles

- Rows now come from `IMessageService.LoadChatMediaIndexAsync` (SQLite `history_message` media rows + live/JSON cache); the pane no longer reads only the legacy per-chat JSON, so history photos appear again
- New `IHistoryMessageStore.GetMediaForChatAsync` queries media/document kinds directly (text rows no longer crowd out photos)
- `ChatDetailInfoViewModel` keeps a model index (cap 400) and materializes 30 tile VMs per page (`CanLoadMoreMedia` / `LoadMoreMedia`, same for Files)
- Refresh diffs the bound collection instead of clear-and-refill (large groups crashed the app)
- Subscribes `IMessageService.ChatMessagesChanged` (SQLite chunks only raise the façade event) with a 400 ms debounce
- Stickers stay out of Media (`ChatMediaFilter`)

---

## Chat timeline memory — detach bubble VMs

- `ChatMessageViewModel.Detach()` drops `Model.PropertyChanged`
- Timeline clear/trim/remove (and chat-info media lists) call Detach so switching chats does not keep orphan VMs alive via the model event
- Open materializes only `InitialUiMessageWindow` (80) bubble VMs via `IChatMessageVmFactory`; hard cap `MaxUiMessageWindow` (150)
- Scroll near top → code-behind checks `ChatDetailViewModel.CanLoadMore` → `LoadMoreMessagesAsync` prepends factory VMs; stickers hydrate on tap (not bulk on open)

---

## Live stickers classified as image

- `StickerMessage` is classified **before** `ImageMessage` (live `MergeFrom` can leave both; the image field is often a thumbnail)
- `ResolveKind` / media filler / toast preview follow the same order
- Unwrap peels `DeviceSentMessage` and the same future-proof wrappers as Socket `MessageContent.Normalize`

---

## Status — façade, list, and viewer

- Shell **Status** item enabled (`NavigationRoutes.Status` → `StatusView`)
- `IStatusService` / `StatusFacade`: authors from `history_status`, oldest→newest items, `EnsureMediaAsync` via `IMessageService`, live `status@broadcast` ingest (no `ChatItem`)
- List: one row per author (avatar, name, relative `TimestampUtc`)
- Viewer: black photo chrome, white segment bars, 5s photo/sticker, video uses proto duration (else `NaturalDuration`); auto-advance; close after last item
- ViewModels talk to `IStatusService` only (not `IWhatsAppService`)

---

## Status vs chat (SQLite)

- Wire chat for Status is always `status@broadcast`; the person is `participant`
- Lista: `HistoryChatPreviewBuilder` / `IsListable` skip that JID
- Table `history_status`: AuthorJid + MessageId, media envelope, `ExpiresAtUtc` = timestamp + 24h
- `HistoryFacade` persists via `HistoryStatusBuilder`; wipe clears the table

---

## History SQLite — media envelope for on-demand download

- `history_message` stores Url / DirectPath / MediaKey / FileEncSha256 / mime / duration / fileName / jpegThumbnail
- `HistoryMessageMapper` maps those onto `ChatMessage` image/video/audio/document fields
- Merge keeps SQLite keys when a live/JSON row wins but has no key
- Existing DBs pick up columns via sqlite-net `CreateTable` migrate (schema 2)

---

## History SQLite phase 4 — façade ownership + detail hydrate

- `HistoryFacade.PersistHistorySqliteChunkAsync`: LID mappings → previews → messages → gate → progress notify
- `MessageFacade.SyncMessageHistoryAsync`: Person upsert + persist + `ChatMessagesChanged` per touched chat
- Open detail reloads via `IMessageService.LoadMessagesForChatAsync` (view sync + VM); load-more / on-demand also on the message façade
- `IWhatsAppService.ApplyHistoryLidMappings` restores PN/LID bookkeeping skipped when the legacy body was turned off
- Thin timeline (&lt; 40 msgs): opening a chat seeds RAM then requests `HISTORY_SYNC_ON_DEMAND`; on-demand latch cleared on SQLite apply (`CompleteHistoryOnDemandForChats`)

---

## History SQLite path (phase 3+) — legacy body off

- `MessageFacade` persists previews + messages to SQLite; the old JSON apply body is gone
- `WhatsAppService.NotifyHistorySqliteChunkApplied` completes resync wait / initial-sync progress
- `ProcessHistorySyncCoreAsync` only notifies SQLite-path progress (no legacy UI apply)
- Chat detail loads via `IMessageService.LoadMessagesForChatAsync` (SQLite + live/JSON merge)
- **Listable filter** (`HistorySyncContentFilter`): skip protocol/revoke/pin/reaction and empty bodies — same spirit as legacy JSON apply (no ghost empty chats)

---

## History messages SQLite (phase 3)

- Table `history_message` with `SendState` INTEGER (NotApplicable…Failed); capped at 250 msgs/chat/chunk
- Persist owned by `HistoryFacade` (`HistoryMessageBuilder`); cleared on wipe
- `HistoryMessageChunkPersisted` carries `ChatJids` for open-detail hydrate

---

## History chat preview SQLite (phase 1–2) + façade ownership

- Table `history_chat_preview`: list rows from history chunks; built off-thread in `HistoryFacade`
- `history_migration` gate + preview clear owned by **`HistoryFacade`** (`Track*` / `ResetHistorySqliteAsync`); no `WhatsAppService.AttachHistory*`
- Phase 2: `ChatPreviewChunkPersisted` → `ChatListViewModel` hydrates list
- Wire root remains `ConnectionHandler`; `WhatsAppService` stays the compatibility in-memory client until richer history/SQLite replaces remaining JSON uses

---

## History migration gate (current tree)

- SQLite `history_migration` owned by `HistoryFacade` (`Track*` / `ResetHistorySqliteAsync`)
- `MessageFacade` marks InProgress/Succeeded around sync; wipe via `OnSessionCleared` / resync wipe
- Gate only — chats/messages remain on JSON until a later migration step

---

## Group members, Person source, bubble avatars (current tree)

- `ChatItem.GroupMembers` persists the group roster (capped); Members pivot lists them with optional avatars
- `PersonSource` on SQLite `Person` (INTEGER): address-book names stick; push names cannot overwrite them; `Phone` is indexed
- After deferred startup, address-book overlay runs with `force: true` and reapplies distinct names to chats / roster
- Group timeline author photos: `ChatDetailViewModel.ApplyMessageRunLayout` resolves once (roster → canonical 1:1 → Person) and sets `ContactUri`; the bubble only binds
- SQLite `PersonGroup` (Person↔Group) updated when a roster applies; rows keyed by Jid **and** LID/PN/phone aliases so lookup survives LID↔PN mismatch
- Member info “groups in common” includes the open group; factory is `CreateGroupMember`
- Tap author name/avatar (or Members list) opens member info (Profile / Media / Files; no Calls/pins)
- Chat-info UI: `ChatDetailInfoControl` hosts user + group only; `ChatDetailGroupMemberInfoPane` is a **separate** shell (avoids shared-host conflicts). Opening any info resets Pivot `SelectedIndex` to 0
- Group header status loop: hint → alphabetical member names → fade; repeats ~every 90s
- Image download chrome: themed wallpaper placeholders (`Assets/Media/wallpaper-placeholder-*.png`) behind a rounded download button in bubbles and the info media tile (`#00210F` tile background)

---

## Unison.Socket (current foundation)

Baileys **7.0.0-rc14** session and protocol, separated from the UWP app.

### Architecture

- Added `Unison.Socket` (netstandard2.0), mirroring the rc14 structure
- `ConnectionHandler` owns socket-level work only (Noise, framing, IQ correlation, keep-alive)
- `WhatsAppSession` is the session composition root
- ~38 use cases: messaging, receipts, retries, media, groups, profiles, USync, app-state, history, authentication
- UWP façades for connection, messages, contacts, chats, profiles, history
- ViewModels consume the new façades
- `SocketBridge` keeps the existing UI working during the migration (`IWhatsAppSocket` over `WhatsAppSession`)

### Protocol

- Ported Baileys 7.0.0-rc14 session and protocol flows
- Buffered event processing during history synchronization
- Sequential offline node processing
- `MessageRetryManager` with LRU tracking and retry reasons
- Replaced the JidAlias protocol flow with `LidMappingStore`
- Pre-key generation at rc14 volume (812 initial, refill below 5)
- USync for contacts, devices, and LID
- App-state patches (mute / archive / pin / read)
- Media encryption and downloading (CDN HTTPS)
- Group metadata synchronization
- Phone-number pairing (link code) in addition to QR
- Server-side logout notification
- Outgoing audio converted to OGG
- Synchronized chat pin/unpin via app-state

### Runtime and reliability

- Fixed 1:1 messaging after application restart with persisted authentication/session state
- Fixed disconnect and unpair session cleanup
- Fixed QR state getting stuck after disconnect
- QR refresh after timeout
- On-demand history resynchronization through `HistoryFacade`
- Fixed history synchronization during the initial handshake
- Fixed self-chat read state
- Profile, contact, and group avatar sync updated for the rc14 LID flow

### Migration status (this release)

- The new socket stack is the foundation for WhatsApp communication
- `SocketBridge` keeps the UWP layer functional
- `WhatsAppService` is still present for compatibility and will be removed as remaining legacy flows migrate
- The background broker still hosts the existing raw-socket infrastructure; transferring that socket onto the new stack is a subsequent step

---

## Product surface (shell, chat UI, media)

Shipped around the same era as the façade work; still current.

- Language selector on Boot and Settings
- Language packs shipped **inside the main package** (not only OS + pt-BR on sideload)
- QR code pop-up for lower-resolution devices
- White / light theme for the Unison shell; WhatsApp shell still available
- Shell reload after theme/language changes
- Image viewer: pinch, pan, wheel zoom, double-tap
- Navbar no longer opens accidentally on Minimal (W10M)
- Settings shell with account info and disconnect
- Boot shell (extended splash) with animations
- Settings collaborators with GitHub links
- Chat message balloons as an entity + ViewModel (images, videos, reactions, quotes, interaction events)
- Code-behind interactions moved to ViewModels via Microsoft.Xaml.Behaviors
- WinUI 2.7
- Chat info window for groups and users
- Notifications closer to WhatsApp UWP (circular avatar, group vs direct layout)
- Pin Tile — pin chats to Start
- Audio/video in bubbles: video opens fullscreen; audio on speaker vs earpiece (screen off when held to the ear); statement updates
- Hardcoded Baileys-core strings replaced with resources
- Audio recorder UI (overlay + elapsed)

---

## Socket Broker (out-of-process)

Predecessor of the current background task; journal format still in use.

- Reliable Socket Broker foundation with an out-of-process `SocketActivity` task
- Noise handoff, frame journal (UBJ2 / UBD3), cold restore on the **legacy** `SocketClient` path
- Real contact toasts when minimized or screen-off
- Filter non-message frames
- Single disconnect toast

Cold restore and transfer are **not** wired through `SocketBridge` yet. See [Background broker](Background-Broker).

---

## v6.9

Theme: `WhatsAppService` becomes a **connection client**; policy moves to WhatsApp contracts/services; ChatDetail composer becomes MVVM.

- Move `ContactService` ownership (names + avatars: cooldown, batches, dedup)
- ChatDetail composer with MVVM (attach, microphone, overlay)
- Attachments and microphone recording
- On-demand image loading and fullscreen viewer
- Domain WhatsApp façades (`Contracts/WhatsApp`, `Services/WhatsApp`)
- `IDebugSendService` extracted from the client (`#if DEBUG`)
- `ChatKind` (Direct / Group / Personal) distinct from `ChatMessageKind` / `ChatPreviewKind`
- Message kind from protocol flags, not `[Image]` text
- Toast circular avatar
- Refresh README, `.gitignore`, and `Unison.slnx`
- Build and deployment scripts for `src/Unison.Uwp`
- Missing translations with English fallback

Explicitly **not** rewritten in 6.9: Noise, Signal, Socket Broker, journal, cold restore.

---

## v6.8

- Split Core, Uwp, Baileys, and Background projects
- MVVM + DI architecture
- Add `en-US`, `pt-BR`, and `id-ID` localization (later expanded; see UI page)
- Add Boot, Start, Login, and AppShell navigation
- Add shell themes and settings
- `Unison.Core` has no XAML; `Unison.Background` has no Core
- Auth-boundary navigation uses `NavigateAndClear`
- Master-detail remains on `ChatsView`
