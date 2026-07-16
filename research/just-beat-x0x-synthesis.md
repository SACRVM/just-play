# JUST BEAT — X0X drum & bass synthesis knowledge base (no samples, no AI)

Companion to `just-beat-sound-models.md` (whose verdict was: DSP-first). This is the knowledge map
for writing the synthesis engine ourselves — how the classic machines actually made each sound,
the unifying DSP primitive, and how to get "infinite kicks/snares" via principled randomization.
Status: drafted from established synthesis lore 2026-07-04; circuit-level details marked ⚠ should
be re-verified against service manuals / cited papers when implementing.

## 1. How the classics actually work (circuit → DSP translation)

### TR-808 — fully analog, the "resonator" family
- **Kick:** a bridged-T resonator biased near self-oscillation, kicked by the trigger pulse. The
  ringing IS the sound: a damped ~50–60 Hz sine whose pitch sags briefly after the hit; the trigger
  edge provides the click. DSP: damped sine `A·e^(−t/τ)·sin(2π f(t) t)` with an exponential pitch
  envelope (f from ~3–4× f0 falling to f0 in 10–40 ms), plus a short click transient, plus gentle
  saturation. Decay knob = τ. That's the whole legend.
- **Snare:** two resonators (⚠ ~180 Hz + ~330 Hz) for the shell tone + highpassed white noise with
  its own fast envelope ("snappy" = noise mix/decay).
- **Hats (closed/open):** a bank of ~6 square oscillators at inharmonic frequencies (⚠ the classic
  cluster ≈ 205/304/369/522/540/800 Hz) → bandpass ~8–10 kHz → fast envelope. Closed vs open = decay
  length. Metallic character comes from the inharmonic cluster, not noise.
- **Cymbal:** same oscillator bank through different band splits with multi-segment envelopes.
- **Clap:** bandpassed noise (~1 kHz) through a retrigger envelope — 3–4 rapid re-fires ~10 ms apart
  (the "many hands" illusion) + a longer noise tail.
- **Toms/congas:** tuned bridged-T resonators + pitch envelope + a breath of noise.
- **Cowbell:** two square oscillators (⚠ ~540 + 800 Hz) → bandpass → fast decay.
- **Rimshot/claves/maracas:** high-tuned fast-decay resonator / filtered-noise micro-envelopes.

### TR-909 — the techno one (hybrid)
- **Kick:** ✅ VERIFIED against the actual service manual (N29, 2026-07-14) — see
  `references/kick-circuit-909.md` for the full stage-by-stage circuit breakdown. Confirmed: a
  real VCO (CV Gen IC12 → VCO IC13, triangle shaped toward sine via diode wave-rounding) with a
  dedicated, explicit pitch-envelope generator (ENV3/TUNE — NOT a bridged-T resonator, and NOT a
  circuit-nonlinearity side effect the way the 808's pitch "sigh" is) + a fully parallel click
  voice (Pulse Gen + shared digital shift-register noise, own fixed ~15 ms envelope, own ATTACK
  *level* pot — ATTACK is a level control, not a time control). **Correction:** no dedicated
  saturation/distortion stage was found in the bass-drum circuit itself — "the dirt is part of
  the instrument" is NOT schematic-supported for the BD voice; any grit is more likely downstream
  gain-staging. Keep a drive/waveshaper stage in our engine if it sounds good, but document it as
  our own creative addition, not a circuit reproduction.
- **Snare:** two slightly detuned oscillators + noise with separate tone/snappy envelopes.
- ⚠ **Hats & cymbals are 6-bit SAMPLES on the 909** — the one exception in the family. For a
  no-samples engine we use the 808-style metallic cluster (or FM pairs) instead.

### TR-606 — same DNA, thinner tunings (fully analog; the resonator family again).

### TB-303 — the bass
Single oscillator (saw/square) → resonant diode-ladder lowpass (⚠ ~18 dB/oct, distinctive) → VCA.
The acid magic is the *interaction*: filter-envelope mod depth, ACCENT (boosts amp + env with a
characteristic filter "wow"), SLIDE (portamento between steps), then external distortion. Sequencer
integration matters as much as the voice: accent/slide are per-step properties (GROOVE hook!).

## 2. The unifying primitive (engine design)

Every voice above is one architecture:

```
EXCITER (pulse | noise burst) → RESONATOR BANK (N damped sines / biquads, optional pitch env)
  + NOISE PATH (colored noise + envelope)
  → per-voice mix → NONLINEARITY (drive/waveshape) → voice out → JUST bus rack
```

One DSP core, per-instrument parameter sets. Kick = 1 resonator + pitch env + click. Snare = 2
resonators + noise. Hat = 6 metallic squares + bandpass. Tom = 1–2 resonators tuned. Clap = noise +
retrigger env. Modal synthesis (a sum of damped resonators) is the same primitive generalized —
the engine scales from "808 clone" to "any percussive object" without new architecture. All
deterministic, trivially cheap on CPU, trim/AOT-safe, zero dependencies.

## 3. Infinite palette — principled randomization (the "unendlich viele Kicks" part)

1. **Sweet-spot distributions, not uniform chaos:** per parameter per instrument, define musical
   ranges (kick f0 40–70 Hz, sweep ratio 2–6×, click 0–30 %, τ 60 ms–1.2 s …) and sample within
   them. Uniform randomness over raw DSP ranges produces mush — the priors ARE the product.
2. **Perceptual macros over raw knobs:** map 3–4 macros (PUNCH, BOOM, DIRT, BRIGHT) onto correlated
   parameter movements (punch ↑ = faster sweep + more click + shorter τ + more drive). Randomize in
   macro space for coherent variation; expose raw knobs only in an "expert" layer.
3. **Seeded determinism:** patch = (instrument, seed, macro vector) — a few bytes. Every random kick
   is reproducible, shareable, and can be a preset string. "Kick #4711" is forever recallable.
4. **⭐ Analyzer as judge (the JustPlay-original move):** generate candidates, then SCORE them with
   our own analysis stack — bassPunch/transient metrics for punch, harshness detector as the
   anti-fatigue gate (Chloe's ears!), LUFS via the rack for loudness match. Auto-cull duds; only
   hits above the punch threshold reach the user. Nobody else can do generate-and-test with a
   gig-validated feature stack as the fitness function.
5. **Variation around a favorite:** small Gaussian jitter in macro space around a chosen seed =
   "more like this one" — the DrumGAN latent-neighborhood UX without the GAN.

## 4. Prior art to study (all DSP, no AI — proof the approach carries a product)

- **Sonic Charge Microtonic** — THE reference: pure-synthesis drum machine, beloved for decades,
  famous randomize/morph. Study its voice model and randomize UX.
- **Sonic Academy Kick 2** — kick designer: editable pitch/amp envelopes + click layer; the
  workflow producers already accept for bespoke kicks.
- **Roland ACB (AIRA)** — Roland's own circuit-behavior modeling of 808/909/303; the fidelity bar.
- Waldorf Attack, FXpansion Tremor, Ableton DS suite, hardware: Jomox, Vermona DRM1, Erica Synths.

## 5. Literature / sources to pull when implementing

- **Gordon Reid, "Synth Secrets" (Sound on Sound, ~60 parts)** — the canonical practical series;
  has dedicated chapters on synthesizing kick, snare, hats, cymbals, toms analytically.
- **Kurt Werner et al. (Stanford CCRMA / DAFx)** — virtual-analog / wave-digital-filter models of
  the TR-808 voices (kick, cymbal); the academic gold standard if we want circuit-accurate. ✅ Read
  in full for the 909 kick research pass (N29) — see `references/kick-circuit-909.md` for the
  equations and what they say about the 808's bridged-T + pitch-sigh mechanism.
- 808/909/606/303 **service manuals + published circuit analyses** (widely available; the hat
  oscillator frequencies and resonator tunings above should be confirmed there). ✅ The TR-909
  service manual (block diagram + bass-drum schematic) was read for N29 — see
  `references/kick-circuit-909.md`. 808/606/303 manuals still unread; the hat/cymbal/snare/303
  tunings above remain lore-level.
- The `research/just-beat-sound-models.md` sources for the neural side (DOSE onset loss = why
  transients deserve explicit care even in evaluation).

## 6. Open to verify before coding

- Exact resonator/oscillator tunings per voice (service manuals) — table above is lore-level.
- 303 filter topology details (diode ladder pole count/behavior) if we chase authentic acid.
- Whether GROOVE's per-step accent/slide contract should live in the BEAT voice API from day 1
  (recommendation: yes — 303 without accent/slide is not a 303).
