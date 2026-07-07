# Competitor comparisons — internal research

**Date:** 2026-07-04
**Status:** Internal research doc, NOT marketing copy. Feeds later docs/roadmap/marketing decisions — it is
not itself a public-facing artifact and should not be quoted verbatim externally without re-checking dates
(software version numbers, prices and feature sets move fast).

**Ground rule for this doc ("ehrlich alles"):** every comparison below carries a genuine "Where they win"
section. If a section reads like it flatters JUST STREAM or JUST PLAY, that's a bug in the doc, not a
compliment to us. Where a claim could not be independently verified from an official source, it is marked
**UNVERIFIED** rather than guessed at — an admitted gap in this research beats an invented fact.

Our own claims below are sourced to specific files in this repo (`docs/*.html`, `src/**/*.cs`) rather than
URLs — those files ARE the source of truth for what we ship.

---

## 1. BUTT ("broadcast using this tool") vs JUST STREAM

BUTT is a free, donation-supported, single-purpose broadcast/record tool — the closest thing to a direct
competitor JUST STREAM has, and it has had a decade-plus head start.

**Sources:** official site & manual (v1.46.0, released 2025-12-07) —
[danielnoethen.de/butt](https://danielnoethen.de/butt/),
[manual](https://danielnoethen.de/butt/release/1.46.0/butt-1.46.0_manual.html),
[changelog](https://danielnoethen.de/butt/Changelog.html),
[multi-server howto](https://danielnoethen.de/butt/howtos/multiple_servers.html).

### Feature table

| Feature | BUTT 1.46.0 | JUST STREAM | Source |
|---|---|---|---|
| Streaming codecs | MP3, AAC+, Ogg Vorbis, Ogg Opus, Ogg FLAC (5) | MP3 (CBR 128–320) + Opus (2) | BUTT manual; `src/JustPlay.Core/Models/StreamServerProfile.cs` |
| Recording codecs | MP3, AAC+, Ogg Vorbis, Ogg Opus, FLAC (16/24/32-bit), WAV | SameAsStream, MP3 320, FLAC, AIFF, WAV | BUTT manual; `src/JustPlay.Core/Models/RecordingSettings.cs` |
| Record ≠ stream codec/bitrate | Yes, explicit: "BUTT is able to record and stream simultaneously in different bit rates" | Yes — recording format is chosen independently of stream codec (`RecordingFormat` incl. `SameAsStream` which *mirrors* the live codec on purpose) | BUTT manual; `src/JustPlay.Core/Models/RecordingSettings.cs` |
| Split-recording (time-based) | Yes — split every N minutes | **None** — one continuous file per set | BUTT manual |
| Song-based file splitting | Not found in manual/changelog — treat as absent | **None** | BUTT manual (silent on this) |
| Silence-based auto start/stop | Yes — separately configurable dB threshold + timer for stream AND recording (default −50 dBFS) | Recording only: `AutoRecord` triggers on stream CONNECT/DISCONNECT event (deterministic, not level-based) | BUTT manual; `src/JustPlay.Stream/Settings/StreamSettings.cs` |
| Smart silence *trimming* (look-behind, keep short gaps, cut long ones) | **Not found** — only the blunt level-threshold gate above, no asymmetric gap handling | Yes (unreleased, targeting 0.5): starts on first beat, ends on last, gaps ≤30s kept 1:1, longer gaps cut, all-silence recordings discarded | BUTT manual (absent); `src/JustPlay.Core/Models/RecordingSettings.cs` (`TrimSilence` docs) |
| Server protocols | Icecast, Shoutcast, WebRTC/WHIP (added v1.42.0) | Icecast only (PUT, auto-fallback to legacy SOURCE) | BUTT manual/changelog; `src/JustPlay.Audio.Bass/BassBroadcastService.cs` |
| Multiple servers simultaneously (one instance) | **No** — official howto says run multiple BUTT instances instead | No — multiple *saved* profiles, one active at a time | BUTT howto; `src/JustPlay.Stream/Settings/StreamSettings.cs` |
| TLS | UNVERIFIED (not confirmed either way in the manual sections fetched) | Yes, per-profile toggle | `src/JustPlay.Core/Models/StreamServerProfile.cs` |
| Sound processing on broadcast/record path | 10-band EQ (±15 dB) + dynamic range compressor (has an "aggressive" mode), independent settings per stream vs. recording path | 3-band EQ + adaptive TILT + PUNCH (transient shaping) + true-peak Limiter (Off/Soft/Club/Loud) with genre presets — one chain feeds both stream and any local monitor | BUTT manual/changelog; `docs/just-stream.html` |
| True-peak / broadcast-standard limiter | **None found** — no limiter, no true-peak concept anywhere in manual/changelog | Yes, dedicated stage, four drive levels | BUTT manual (absent); `docs/just-play.html` ("Limiter / Maximizer") |
| MIDI control | Yes, fairly deep: CC control of start/stop broadcast & record, gains, crossfader, mute, MIDI-Learn (since v1.45.0) | **None** | BUTT changelog |
| VU / level meters | dBFS meters, customizable color/threshold | True-peak meter with limiter gain-reduction lamps + DRY/WET spectrum view | BUTT changelog; `docs/just-stream.html` |
| Per-application audio capture (grab one app, not the whole device) | **None** — device/soundcard capture only | Yes — WASAPI per-process loopback (Windows 10 2004+), the headline feature | BUTT manual (absent); `docs/just-stream.html` |
| Multi-channel input selection | Yes — pick L/R from any channel pair on a multi-channel device (generic) | Yes — broadcast-channel picker framed for DJ gear (Channels 1-2 default = Master, 3-4, or full mix), so headphone cue isn't isolated by raw index but by DJ-workflow meaning | BUTT manual; `src/JustPlay.Stream/Settings/StreamSettings.cs` (`AppMasterChannels`) |
| ICY now-playing metadata | UNVERIFIED (not confirmed in the fetched manual sections) | Yes, MP3 only (Opus can't carry it on Icecast), with a privacy off-switch | `docs/just-stream.html` |
| Platforms | Windows, macOS, Linux | Windows only | BUTT download page |
| Price / license | Free; donation-supported (PayPal/Patreon/crypto). Exact license **UNVERIFIED** — commonly cited as GPL elsewhere, but the license text itself was not confirmed on danielnoethen.de in this pass | Free, MIT-licensed, open source | BUTT site; `docs/just-stream.html` footer |
| Track record | In active development, versioned since well before 2020 (**UNVERIFIED** exact founding year — not confirmed in this pass) | v0.4 released, set-recording feature still unreleased/working-tree | BUTT changelog |

### Where they win

- **Codec breadth, both directions.** Five streaming codecs (incl. AAC+ and Ogg Vorbis, which we don't
  touch at all) vs. our two (MP3/Opus). Same gap on the recording side.
- **Split-recording.** A basic, long-standing feature (split every N minutes) that we simply don't have —
  a long club set becomes one giant file in JUST STREAM today.
- **Protocol breadth.** Icecast *and* Shoutcast *and* WebRTC/WHIP vs. our Icecast-only. Some venues still
  run Shoutcast; we currently can't talk to them at all.
- **MIDI control.** BUTT — a much smaller, single-purpose tool — has deeper MIDI mapping (with a Learn
  mode) than we do. We have none.
- **10-band EQ.** More surgical manual tone-shaping than our 3-band + TILT/PUNCH approach; ours leans on
  automation (TILT adapts per track) rather than manual bands, which is a design choice, not obviously
  better.
- **Signal-presence auto start/stop.** BUTT's silence-threshold gate can start *and stop* the actual
  broadcast (not just the recording) automatically based on audio presence, with no "connect" event
  required. Our `AutoRecord` is connect/disconnect-triggered, which needs an explicit stream session; BUTT's
  is more "walk away, mic gets loud, it goes."
- **Cross-platform.** Windows, macOS, Linux vs. our Windows-only.
- **Free and has been for years** with a broader feature set — there is no price argument to make here.

### Where we win

- **Per-app capture.** BUTT cannot isolate a single application's audio — it captures a whole device. This
  is the one structural capability BUTT cannot replicate without an OS-level rearchitecture.
- **Purpose-built true-peak limiter with genre presets.** BUTT has zero limiting/maximizing stage at all —
  a BUTT user can clip or under-loud a stream with no safety net beyond the basic compressor.
- **Smart auto-trim silence** (unreleased) is a materially more thoughtful design than BUTT's blunt
  threshold gate — asymmetric short-gap-preserved/long-gap-cut behavior with a look-behind buffer.
- **AIFF recording** — a real gap in BUTT's format list, though it mostly matters once we have a Mac
  build to pair it with.
- **DJ-workflow-aware channel picker** (Master vs Cue framing) vs. BUTT's raw channel-index picker.
- **`SameAsStream` self-check recording mode** — deliberately mirrors what the *audience* actually heard
  (same codec/bitrate), which BUTT's format-agnostic recording doesn't frame as a diagnostic tool.

### Roadmap gaps this suggests (ranked)

1. **Split-recording (time-based).** Cheap, long-proven, and a real absence — should not stay a gap for long.
2. **Shoutcast protocol support.** Some infra we might need to reach still runs Shoutcast, not just Icecast.
3. **MIDI control**, at least for start/stop broadcast & record and basic gain — BUTT proves this doesn't
   need to be complex to be useful.
4. **Streaming codec breadth** (AAC+ at minimum) — weigh against BASS/licensing cost before committing.
5. **Cross-platform reach** — already tracked as the Mac port roadmap item; BUTT is proof the bar for a
   broadcast tool is "runs everywhere," not just Windows.

---

## 2. Mixed In Key vs JUST PLAY

Mixed In Key (MIK) is the long-established commercial reference for DJ key/energy analysis — the origin of
the Camelot Wheel and the "8A · Energy 7" comment-tag convention this whole industry uses.

**Sources:** [mixedinkey.com](https://mixedinkey.com), [features](https://mixedinkey.com/features/),
[FAQ](https://mixedinkey.com/faq/mixed-in-key/), [Camelot Wheel history](https://mixedinkey.com/camelot-wheel/),
[Mark Davis interview](https://mixedinkey.com/book/how-to-use-harmonic-mixing/interview-with-mark-davis/),
independent accuracy test: [Dubspot Lab Report — Mixed In Key vs Beatport](https://blog.dubspot.com/dubspot-lab-report-mixed-in-key-vs-beatport)
(200-track, 6-genre, ear-verified panel, updated 2026-06-12),
[Digital DJ Tips on MIK 11 Pro](https://www.digitaldjtips.com/mixed-in-key-11-pro/).

### Feature table

| Feature | Mixed In Key 11 | JUST PLAY | Source |
|---|---|---|---|
| Price | **UNVERIFIED exact figure** — official shop pricing was paywall/geo-gated during this research; secondary sources conflict (~$58 Standard / ~$99–129 Pro cited in different places). Perpetual license, 3-computer/same-OS limit | Free, open source (MIT) | mixedinkey.com/shop (blocked), mixedinkey.com/faq/mixed-in-key/; `docs/just-play.html` footer |
| Key detection — own accuracy claim | "At least 10% more accurate than the next best key detection software" (no % published) | No public accuracy marketing claim beyond internal benchmark below | mixedinkey.com/features/ |
| Key detection — independent accuracy | **89%** (178 full + 12 half credit / 200 tracks) in the Dubspot Lab Report panel test, vs. KeyFinder 76%, Rekordbox 7 69%, Beatport Key Data 60% in the *same* test | Internal benchmark only: MIREX-weighted **~0.754 / ~69% exact** (ML detector, GiantSteps dataset, 5-fold CV + 12× transposition) / ~0.712 (DSP fallback detector) — **not an independent third-party test**, and a different dataset/methodology than Dubspot's panel | Dubspot Lab Report; `src/JustPlay.ML/BestKeyDetector.cs`, `ml/INTEGRATION_PLAN.md` |
| Energy scale | 1–10, framed as "how danceable" | 1–10, framed as track intensity | mixedinkey.com/features/ (exact 1–10 wording UNVERIFIED on official page, but industry-standard); `docs/just-play.html` |
| Additional vibe dimensions (brightness, hypnotic/repetitive, groove, danceability, etc.) | **None found** — MIK appears limited to Key + BPM + single Energy axis | Yes — groove, brightness, hypnotic/repetitive and more, surfaced via `stats`/`analyze` | mixedinkey.com/features/, FAQ (absence checked); `docs/cli.html` |
| Cue point suggestions | Yes — up to 8 automatic cues, exported directly into Serato/Traktor/Rekordbox | **None** — no cue-point feature exists in JUST PLAY today | mixedinkey.com/features/ |
| Camelot Wheel | Invented by MIK (Mark Davis, 2007); MIK also claims "world's first key detection software" (2009) | Uses the same notation (industry standard now, not our invention) | mixedinkey.com/camelot-wheel/; `docs/just-play.html` |
| Integrations | Explicit, marketed integrations: Serato, Traktor, Rekordbox, VirtualDJ, Ableton Live, iTunes | Writes standard tags read by Traktor and rekordbox (documented); broader per-app-validated support (Serato GEOB, etc.) is researched (`dj-metadata-interop` skill) but not a shipped/marketed parity claim | mixedinkey.com/features/; `docs/cli.html`, `docs/just-play.html` |
| Platforms | Windows + Mac | Windows only | mixedinkey.com homepage/FAQ |
| Batch processing | Implied standard workflow (drag a folder in); no documented limits found — **UNVERIFIED specifics** | Yes, explicit: whole-library `scan`/`analyze`/`stats`/`dedup`/`tag`/`promote`/`squeeze` via CLI, scriptable and AI-agent-drivable | mixedinkey.com (implied); `docs/cli.html` |
| Automatic set sequencing / ordering | **None found** — MIK analyzes tracks but does not order a set for you | Yes — Harmonic Sort + Set Squeeze | mixedinkey.com/features/ (absent); `docs/just-play.html` |
| Tag-write model | Writes tags as a normal part of analysis; does **not** tag `.wav` files (documented FAQ gap); some fields flagged as unsupported by Serato in some formats | Explicit opt-in: nothing written until asked, per-field (BPM/Key/Energy) or restore-original, every write undoable | mixedinkey.com/faq/mixed-in-key/; `docs/just-play.html` ("Your files are the memory") |
| BPM detection | Yes | Yes | mixedinkey.com homepage; `docs/just-play.html` |
| Reputation / track record | "Industry standard" self-description, established since 2009, celebrity-DJ testimonial (David Guetta) on homepage, huge install base (no exact number found — **UNVERIFIED**) | New, no independent track record yet | mixedinkey.com homepage |

### Where they win

- **Independently verified accuracy is real and currently ahead of ours by the only third-party
  measurement either product has: 89% vs. our internal-only ~69–75%.** These numbers come from different
  tests (Dubspot's 200-track ear panel vs. our GiantSteps MIREX-weighted CV score) so they are not strictly
  apples-to-apples — but that caveat cuts both ways: we also have no independent test proving we're
  competitive, only an internal one. **This is the single most important honest admission in this whole
  doc.**
- **Cue point suggestions.** A genuinely useful, concrete per-track feature we don't have at all.
- **Marketed, validated multi-app integrations** (Serato/Traktor/Rekordbox/VirtualDJ/Ableton) vs. our
  documented-but-narrower Traktor/rekordbox claim.
- **Mac support** — half the DJ market we currently can't reach.
- **A decade-plus of trust, reputation, and a paying install base.** This is real and not something a
  roadmap item fixes directly — it's earned via track record, benchmarks, and word of mouth.

### Where we win

- **Free**, full stop, vs. MIK's ~$58–129 one-time cost (exact price UNVERIFIED but it is definitely not free).
- **Richer feature taxonomy** — brightness, hypnotic/repetitive, groove, danceability beyond a single
  Energy score, none of which MIK appears to expose.
- **Automatic set sequencing** (Harmonic Sort, Set Squeeze) — MIK gives you the raw numbers, we also build
  the set.
- **A real CLI/agent surface** for whole-library batch work — MIK has no documented equivalent scripting
  interface.
- **Explicit, reversible, opt-in tag-writing model** — arguably safer for a user's existing library than
  MIK's "writes on analyze" default, though this is a philosophy difference as much as a strict "win."

### Roadmap gaps this suggests (ranked)

1. **Cue point suggestions.** The most concrete, missing, high-value per-track feature.
2. **Independent, third-party-comparable accuracy validation.** Either commission/run a Dubspot-style
   ear-panel test on our own detector, or at minimum publish our GiantSteps methodology transparently
   (dataset, exact-match vs. MIREX-weighted, sample size) so "ehrlich alles" holds up outside this repo too.
3. **Per-DJ-app validated tag integration** (Serato GEOB, Traktor NML quirks, rekordbox ANLZ behavior) —
   the research already exists (`dj-metadata-interop` skill); it isn't yet a shipped, marketed parity claim.
4. **Mac platform** — already tracked via the Mac port roadmap; MIK is proof this is where a large chunk of
   the addressable market already lives.
5. **Reputation/proof-of-accuracy as a trust asset** — not a code gap, but worth flagging: MIK's biggest
   asset isn't a feature, it's fifteen-plus years of DJs trusting the numbers. That only closes with time,
   transparency, and (see #2) verifiable proof.

---

## 3. VirtualDJ (broadcast + record axis) vs JUST STREAM

VirtualDJ (VDJ) is full DJ software; broadcasting and recording are side features bundled into it. This
section compares **only** that slice, but the framing matters: a VDJ user who's already paying for the Pro
tier needs **zero extra software** to go live — that's the real competitive threat, not any single feature.

**Sources:** [broadcast settings manual](https://virtualdj.com/manuals/virtualdj/settings/broadcast.html),
[record settings manual](https://virtualdj.com/manuals/virtualdj/settings/record.html),
[record loopback manual](https://virtualdj.com/manuals/virtualdj/settings/audiosetup/recordloopback.html),
[pricing](https://virtualdj.com/products/virtualdj/price.html),
[system requirements](https://www.virtualdj.com/wiki/Minimum-system-requirements.html),
[broadcast-to-a-radio wiki](https://virtualdj.com/wiki/broadcast-to-a-radio.html),
[forum: Ultimate Guide to Broadcasting](https://virtualdj.com/forums/59741/Music_discussion/The_Ultimate_Guide_to_Broadcasting_With_Virtual_Dj.html),
[forum: broadcasting listener limits](https://www.virtualdj.com/forums/122965/Old_versions/broadcasting_listeners.html).

### Feature table

| Feature | VirtualDJ | JUST STREAM | Source |
|---|---|---|---|
| Zero-server P2P broadcast ("broadcast to friends") | Yes — VDJ hosts the stream itself, no Icecast account needed, but capped at ~10 listeners (upload-bandwidth-limited) and a broadcast license is legally required beyond that | **None** — Icecast server (self-hosted or rented) is always required, no built-in P2P mode | VDJ forum guide/listener-limit thread |
| Icecast/Shoutcast source client | Yes — Icecast (Ogg Vorbis or MP3) and Shoutcast (MP3). No AAC/Opus found (**UNVERIFIED as a hard absence**, but no source confirms it exists) | Icecast only, MP3 + Opus | VDJ broadcast manual; `src/JustPlay.Core/Models/StreamServerProfile.cs` |
| Broadcast feature tier-gating | **Gated behind Pro Infinity ($299 one-time) or Pro Monthly ($19/mo)** — Free/Home tiers cannot broadcast to a server at all, only the capped P2P mode | Always free — no tier at all | VDJ pricing page + broadcast manual |
| Public discovery layer | Yes — VDJ's own podcast hosting, plus one-click broadcast to Facebook/YouTube/Twitch/Periscope, and a public radio directory | **None** — private Icecast mount only, by design (privacy is a stated feature, not a gap — see below) | VDJ broadcast manual, broadcast-to-a-radio wiki |
| Recording formats | MP3 (128/192/320), OGG (112/160/192), FLAC, WAV, plus video WEBM/MP4 — **no AIFF** | SameAsStream, MP3 320, FLAC, AIFF, WAV — **no OGG** | VDJ record manual; `src/JustPlay.Core/Models/RecordingSettings.cs` |
| What gets recorded | The Master output as-is — whatever the DJ's own mixer/FX chain already produced; a separate "Record Loopback" input needed for external mixer setups. No dedicated broadcast-only DSP layer found | Runs through the same purpose-built broadcast DSP (EQ/TILT/PUNCH/true-peak Limiter) before recording/streaming | VDJ record + loopback manuals; `docs/just-stream.html` |
| Dedicated broadcast-path DSP (EQ/compressor/limiter specific to going live) | **Not found** — no mention anywhere in the manual or forums of a broadcast-specific processing stage (**UNVERIFIED as an absolute negative**, but absence-of-evidence across every source checked) | Yes — 3-band EQ + adaptive TILT + PUNCH + true-peak Limiter with genre presets, leaned a notch louder than the player's default because "a live stream has to stay competitively loud" | VDJ manuals (silent); `docs/just-stream.html` |
| Per-app / other-software audio capture | **None** — VDJ only handles its own Master output or an external mixer line-in; no way to grab another application's audio | Yes — WASAPI per-process loopback; can even capture VDJ's own output, or any other software, as a source | VDJ record-loopback manual, ASIO/WASAPI forum thread; `docs/just-stream.html` |
| Auto-record-on-play / scheduled recording | Not found — **UNVERIFIED / likely absent** | Yes (unreleased 0.5) — `AutoRecord` starts on stream connect, saves on disconnect | VDJ manuals (silent); `src/JustPlay.Stream/Settings/StreamSettings.cs` |
| Silence trimming | Not found — **UNVERIFIED / likely absent** | Yes (unreleased 0.5), look-behind gate, asymmetric gap handling | VDJ manuals (silent); `src/JustPlay.Core/Models/RecordingSettings.cs` |
| Platforms | Windows, Mac; no Linux found (**UNVERIFIED as a hard "no"**, but absent from every source checked); limited iOS/Android remote-control companion apps only | Windows only | VDJ system requirements |
| "No extra tool needed" claim | **True, but conditional** — only for a DJ already on Pro Infinity/Pro Monthly; a Free/Home VDJ user gets only the capped P2P mode, not real server broadcasting | N/A — JUST STREAM *is* the extra tool, but it's free regardless of tier | VDJ pricing page |

### Where they win

- **Bundling is the whole story.** If you already own VDJ Pro for the deck/looping/FX features, broadcast
  and record cost nothing extra and need no separate app, no extra learning curve, no extra window. That is
  a real, structural advantage JUST STREAM cannot out-feature its way past — it's a product-shape argument,
  not a feature-count one.
- **Zero-setup P2P mode** for casual/small-scale listening (≤10 friends) with no Icecast account at all —
  genuinely lower friction for a total beginner who just wants a couple of people to hear a set tonight.
- **Public discovery layer** (podcast hosting, one-click social platform broadcast, radio directory) — real
  reach we don't offer and, by current design, don't want to (see honest caveat below).
- **Broader recording codec palette** on paper (adds OGG; loses AIFF) and it also does video recording,
  which we don't touch at all (by design — audio-only).
- **Massive existing install base** — many bedroom DJs already run VDJ for mixing, so broadcast is "already
  there" the moment they think to look for the button.

### Where we win

- **Free and unlimited broadcasting, full stop.** VDJ gates real server broadcasting behind a $19/mo or
  $299 one-time tier; ours has no paywall at any point.
- **Per-app capture**, including the ironic case of being able to capture VDJ's own output — VDJ cannot
  grab any external application's audio at all, only its own Master or a physical line-in.
- **Purpose-built broadcast DSP with a true-peak limiter.** VDJ streams/records whatever the mixer already
  produced; there's no evidence of any dedicated loudness/safety stage on the broadcast path itself.
- **Auto-record-on-connect + smart silence trimming** (unreleased 0.5) — not found anywhere in VDJ's
  documented feature set.
- **AIFF recording** for the Mac-native lossless workflow — a format gap on VDJ's side.
- **Broadcast-channel Master/Cue isolation** — genuinely not a fair comparison point against VDJ, since VDJ
  *is* the DJ software generating one single Master with no external-gear channel-collision risk to solve;
  flagging this rather than claiming a false win.

### Roadmap gaps this suggests (ranked)

1. **A zero-Icecast-account, low-friction broadcast mode** for tiny/casual audiences — VDJ's P2P mode shows
   there's real demand for "I just want two friends to hear this tonight" with no server setup at all.
   Would need real thought on whether it fits JUST STREAM's privacy-first positioning (see docs/just-stream.html
   "your IP is never exposed") before committing — a P2P mode inherently exposes the broadcaster's IP, so
   this is a genuine tension, not a free win.
2. **Recording codec: OGG Vorbis** — cheap breadth gain, closes one of the two format asymmetries with VDJ.
3. **Shoutcast protocol support** (echoes the BUTT gap above) — VDJ supports it too; two-for-two competitors
   we're missing this against.
4. **Consider whether a discovery/distribution layer matters at all** — this is a "should we" question, not
   a "we're behind" one; JUST STREAM's whole pitch is private-Icecast-without-hassle, and a public directory
   may actively conflict with that positioning. Flag for a product conversation, not a silent roadmap add.
5. Everything else in this comparison already favors us — the honest gap here is structural (bundling), not
   feature-shaped, and there's no code fix for "VDJ users already have VDJ open."

---

## Cross-cutting summary — top 5 roadmap gaps overall

Ranked by combined severity + fixability across all three comparisons:

1. **No cue-point suggestion feature in JUST PLAY.** The single most concrete, missing, everyday-useful
   per-track feature versus Mixed In Key — and unlike accuracy or reputation, this is a clean, buildable gap.
2. **No independently-verified accuracy benchmark for our key detection.** MIK has a real third-party
   89%-on-200-tracks data point; we only have an internal GiantSteps CV score (~69–75% depending on
   detector). Different methodologies, but we're the only one of the two without outside validation —
   fix by running/publishing an equivalent independent test, not by re-quoting our own number louder.
3. **No split-recording in JUST STREAM.** BUTT has had time-based split-recording for years; it's basic,
   proven, and currently absent from our recorder entirely.
4. **No MIDI control anywhere in JUST STREAM.** Even a much smaller single-purpose tool like BUTT beats us
   here with a real Learn mode; zero MIDI support is a real gap for hands-off/hardware-controlled setups.
5. **Windows-only across the whole JUST suite**, vs. Mixed In Key (Win+Mac), BUTT (Win+Mac+Linux) and
   VirtualDJ (Win+Mac). Already tracked as the Mac port roadmap item — this research just reconfirms it's
   the single biggest structural reach gap versus every competitor examined, not a nice-to-have.

**Honorable mentions that didn't make the top 5 but recur across comparisons:** no Shoutcast protocol
support (shows up against both BUTT and VirtualDJ); narrower streaming-codec palette generally (AAC+, Ogg
Vorbis) vs. BUTT; no independently-verified marketing-grade reputation/trust asset yet for either JUST PLAY
or JUST STREAM, which no roadmap item fixes directly — only time, transparency, and shipped proof does.
