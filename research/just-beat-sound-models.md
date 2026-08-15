# JUST BEAT — small models for drum/bass one-shot generation (research report)

Deep-research run 2026-07-04 (103 agents, 21 sources fetched, 104 claims extracted, 25 verified
adversarially: 23 confirmed / 2 refuted). Question: which small/local AI models can generate
808/909/606-family drum & bass one-shots with genuine techno-grade punch, embeddable in a shipped
.NET desktop app — and when does classic DSP synthesis simply win?

## Verdict: DSP-first hybrid

**The punch itself is a solved deterministic problem — build it, don't model it.**
The research literature *itself* supplies the evidence: the DOSE paper (ICASSP 2025) had to invent a
dedicated **onset loss** because attack transients — the essence of punch — are precisely what
neural audio models get wrong by default ([arXiv 2504.18157]: onset loss "essential for capturing
timbral characteristics"; kick MSS 4.435→3.700 with it). Meanwhile the classic recipe is two
ingredients: a kick is "nothing but a sine wave with pitch modulation" (dsokolovskiy.com), and the
real TR-808 generates it as a filter biased near self-oscillation (Perfect Circuit). Producers on
Gearspace judge hand-synthesized FM kicks superior to sample packs. **JUST BEAT phase 1 = pure DSP
X0X engine** (sine + pitch-envelope kick / filter-ring 808 model, noise+bandpass snares & hats,
303-style resonant bass with accent/slide) **run through the existing Transient → MasteringLimiter
bus rack** — that chain is where "gleicher Wumms wie der Track" comes from, and nobody else has it.

**A neural layer is optional, later, and only for timbre variety** (sample-pack-free character —
the one thing deterministic synthesis doesn't give you). Verified options below.

## The candidates (all claims verified 2026-07-04)

| Model | What | Punch fit | CPU/RT | License / weights | .NET path | Verdict |
|---|---|---|---|---|---|---|
| **DrumGAN VST** (Sony CSL) | GAN, 0.5 s kick/snare/cymbal one-shots @ 44.1 kHz, knob + 128-dim latent control | Best-in-class of the AI tools ("Backbone tends to do a much better job at creating kicks from scratch" — Gearspace) | n/a | ⛔ weights + 300k-sample training set proprietary; only ships inside Steinberg Backbone 1.5 | none | The existence proof, not a component. Architecture code is public → self-train possible |
| **tiny-audio-diffusion** | Waveform diffusion, 4 pretrained models (kicks/snares/hats/perc) on HF | Author concedes quality/speed/length tradeoff; ~0.75 s cap | ⛔ CUDA-GPU-bound, not realtime | Repo MIT, **weights license unstated** (gray zone); 509 MB ckpts | no ONNX path documented | Experimentation + the self-training recipe; not shippable as-is |
| **RAVE** (IRCAM) | VAE, 48 kHz at ~20× realtime on laptop CPU, streaming export, runs on a Pi 4 | Built for continuous audio/timbre transfer — one-shot fit unproven | ✅ the proven CPU-realtime architecture (200–566 ms latency in streaming practice) | ⛔ **CC BY-NC 4.0** — hard commercial blocker without an IRCAM deal | ⛔ ONNX export = degraded "noiseless v1" config only (no noise, capacity 32, no streaming) | Doubly blocked for us |
| **Stable Audio Open Small** | Text-to-audio, up to 11 s stereo @ 44.1 kHz | Vendor-positioned for "drum loops, foley, riffs, ambient textures" — loops, not punchy one-shots; quality claims are vendor-authored | ✅ runs on Arm CPUs (<8 s gen on a phone — Armv9/KleidiAI int8; x86 transfer plausible, unproven) | ✅ Stability Community License: free commercial <$1M revenue, **registration + attribution required, license TERMINATES above $1M** (planning cliff) | plausible via ONNX Runtime (param count unverified — the "341M" figure was REFUTED) | The only both-CPU-feasible-and-licensable option; wrong conditioning (text) for a knob instrument |
| **anira** (Apache-2.0) | Not a model — RT-safe C++ inference host (static thread pool, rtsan-checked, IEEE 2024 paper, v2.2.0 06/2026) | — | ✅ | ✅ Apache-2.0 | P/Invoke wrapper; for offline one-shot rendering plain ONNX Runtime for C# suffices | The hosting layer IF a neural model ever runs live. ⚠ its exact backend list must be re-verified (multi-backend claim refuted 1-2) |

**2024–2026 academic sweep:** nothing new for one-shot *generation*. Soiledis et al. 2026
(arXiv 2605.10281) renders drum *performances* from MIDI grids; DOSE (arXiv 2504.18157) *extracts*
one-shots from existing mixes.

## Three strategic nuggets

1. **DOSE-style extraction as a feature:** "pull the kick out of THIS track" — sample-pack-free
   character sourced from her own library. Perfectly on-brand (library-first, reference-based,
   same philosophy as GROOVE learning patterns from real tracks). Distinct from generation.
2. **The self-training path** is the only clean route to an own neural layer: MIT
   tiny-audio-diffusion recipe (or public DrumGAN architecture) trained on a *licensed* one-shot
   library, with a DOSE-style onset-weighted loss for punch. Open question: achievable ceiling.
3. **Producer reality check:** Emergent Drums judged "mediocre sounding" on Gearspace; Backbone's
   DrumGAN is the only AI drum tool with a decent kick reputation — and it sits behind exactly the
   kind of curated training + productization we'd have to replicate. The "generic AI mush" complaint
   is the verified norm, not the exception.

## Known gaps (from the adversarial pass)

- No published perceptual A/B of neural one-shots vs deterministic DSP + transient/limiter chain.
- Producer-reputation evidence is thin (one review + forum threads).
- Model size figures largely unverified (SAOS "341M" refuted; DrumGAN/RAVE param counts unknown).
- License terms verified live 2026-07-04 — recheck before any ship decision.

## Sources (primary)

arXiv 2206.14723 (DrumGAN VST) · arXiv 2008.12073 (DrumGAN) · github.com/SonyCSLParis/DrumGAN ·
github.com/crlandsc/tiny-audio-diffusion + 4 HF weight repos · arXiv 2111.05011 + github.com/acids-ircam/RAVE
(+ LICENSE, onnx.gin) · stability.ai/license + community-license-agreement + HF stable-audio-open-small ·
arXiv 2505.08175 (ARC) · arXiv 2504.18157 (DOSE) · arXiv 2605.10281 (Soiledis 2026) ·
github.com/anira-project/anira + arXiv 2506.12665 · cslmusicteam.sony.fr · steinberg.net ·
musictech.com (Backbone 1.5 review) · gearspace.com (2 producer threads) · dsokolovskiy.com/kick-synthesis ·
perfectcircuit.com/signal/kick-drum-synthesis · musicradar.com (analogue drum recreation) · audialab.com
