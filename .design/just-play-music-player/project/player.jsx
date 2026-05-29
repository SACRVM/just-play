// player.jsx — Just Play desktop music player
// Reusable React components: album sleeve + vinyl, waveform, progress bar, queue panel

const { useState, useEffect, useRef, useMemo } = React;

// ─── Theme palettes ──────────────────────────────────────────────────────────
const THEMES = {
  aurora: {
    name: "Aurora",
    bgFrom: "#3a1d6b",
    bgVia: "#251347",
    bgTo: "#120a26",
    accentA: "#6ce0ff",  // cyan
    accentB: "#ff5fae",  // pink
    accentC: "#8a5fff",  // purple
    glow: "rgba(140, 105, 255, 0.55)",
    bottomBarFrom: "#8a4fe0",
    bottomBarTo: "#5fa0ff",
  },
  sunset: {
    name: "Sunset",
    bgFrom: "#5b1a52",
    bgVia: "#3a1235",
    bgTo: "#180a1c",
    accentA: "#ffd166",
    accentB: "#ff6f91",
    accentC: "#ff9966",
    glow: "rgba(255, 120, 120, 0.55)",
    bottomBarFrom: "#ff7a59",
    bottomBarTo: "#d94a8a",
  },
  midnight: {
    name: "Midnight",
    bgFrom: "#16224a",
    bgVia: "#0b1230",
    bgTo: "#050817",
    accentA: "#7ad7ff",
    accentB: "#7c5cff",
    accentC: "#2a7bff",
    glow: "rgba(60, 120, 255, 0.5)",
    bottomBarFrom: "#3957d9",
    bottomBarTo: "#6dc3ff",
  },
  neon: {
    name: "Neon",
    bgFrom: "#0e2a26",
    bgVia: "#08161e",
    bgTo: "#040a10",
    accentA: "#5cffd4",
    accentB: "#a8ff5c",
    accentC: "#5cd0ff",
    glow: "rgba(92, 255, 200, 0.45)",
    bottomBarFrom: "#2bd4a8",
    bottomBarTo: "#83e85a",
  },
};

// ─── Helpers ─────────────────────────────────────────────────────────────────
const fmt = (s) => {
  const m = Math.floor(s / 60);
  const ss = Math.floor(s % 60).toString().padStart(2, "0");
  return `${m}:${ss}`;
};

// ─── Window chrome: traffic lights (frameless macOS-style) ───────────────────
function TrafficLights() {
  return (
    <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
      {["#ff5f57", "#febc2e", "#28c840"].map((c, i) => (
        <div
          key={i}
          style={{
            width: 12, height: 12, borderRadius: "50%",
            background: c,
            boxShadow: `inset 0 0 0 0.5px rgba(0,0,0,.18), 0 0 0 0.5px rgba(255,255,255,.06)`,
          }}
        />
      ))}
    </div>
  );
}

// ─── Waveform visualizer ─────────────────────────────────────────────────────
// Beat-driven animation algorithm:
//   - Per frame, compute beat phase from BPM:  phase = (t * bpm/60) % 1   (0..1)
//   - Beat envelope = exp(-phase*k)            (sharp attack, exponential decay)
//   - Four bands (bass, low-mid, mid, treble) each get their own modulator:
//       bass:    pulses on every beat       — scales with track energy
//       low-mid: pulses on every other beat — slower modulator
//       mid:     slow sine (musical breath)
//       treble:  fast pseudo-noise shimmer
//   - Each SVG path's vertical scale is updated imperatively via refs (no React state).
function Waveform({ theme, playing, height = 90, bpm = 120, energy = 6 }) {
  const W = 1280;
  const H = height;
  const pathRefs = useRef([null, null, null, null]);

  // Static path shapes — amplitudes scaled at runtime via transform: scaleY
  const paths = useMemo(() => {
    const make = (amp, freq, phase, yOff) => {
      const pts = [];
      const steps = 70;
      for (let i = 0; i <= steps; i++) {
        const x = (i / steps) * W;
        const y = yOff + Math.sin((i / steps) * Math.PI * 2 * freq + phase) * amp;
        pts.push(`${x.toFixed(1)},${y.toFixed(1)}`);
      }
      return "M" + pts.join(" L");
    };
    return [
      { d: make(28, 2.0, 0.0, H * 0.55), color: theme.accentB, opacity: 0.85, width: 2.4, band: "bass" },
      { d: make(22, 3.1, 1.2, H * 0.50), color: theme.accentA, opacity: 0.75, width: 1.9, band: "mid"  },
      { d: make(34, 1.6, 2.4, H * 0.58), color: theme.accentC, opacity: 0.6,  width: 2.6, band: "lowMid"},
      { d: make(14, 4.6, 0.8, H * 0.45), color: theme.accentB, opacity: 0.5,  width: 1.4, band: "treble"},
    ];
  }, [theme, H]);

  useEffect(() => {
    const els = pathRefs.current;
    if (!playing) {
      // settle gently to an idle state
      els.forEach((el, i) => {
        if (!el) return;
        el.style.transform = `translateX(0) scaleY(0.55)`;
        el.style.opacity = 0.5;
      });
      return;
    }
    let raf;
    const startedAt = performance.now();
    const beatSec = 60 / Math.max(40, bpm);
    const energyF = Math.max(0.3, Math.min(1.1, energy / 8)); // 5..9 -> .625..1.125

    const tick = (now) => {
      const t = (now - startedAt) / 1000;
      // Beat phase 0..1
      const beatPhase = (t / beatSec) % 1;
      const halfBeat  = (t / (beatSec * 2)) % 1;
      // Sharp-attack, exponential-decay envelope per beat
      const beatEnv = Math.exp(-beatPhase * 4.5);          // bass kick
      const halfEnv = Math.exp(-halfBeat * 3.0);           // slower
      // Continuous modulators
      const slow    = (Math.sin(t * 1.6) + 1) / 2;          // 0..1
      const shimmer = (Math.sin(t * 9.3) * Math.sin(t * 3.7) + 1) / 2;

      // Per-band amplitudes (mapped to scaleY: 0.4..1.6)
      const bassAmp   = 0.55 + 0.95 * beatEnv * energyF;
      const lowMidAmp = 0.55 + 0.55 * halfEnv * energyF + 0.18 * slow;
      const midAmp    = 0.60 + 0.35 * slow + 0.18 * beatEnv * energyF;
      const trebAmp   = 0.50 + 0.55 * shimmer * energyF + 0.20 * beatEnv * energyF;

      // Horizontal drift speeds vary per band
      const driftPx = (speed) => -((t * speed) % (W / 5));

      // Subtle opacity pulsing on beat for the glow feel
      const opGlow = (base) => Math.min(1, base + 0.18 * beatEnv * energyF);

      const apply = (i, amp, speed, baseOp) => {
        const el = els[i];
        if (!el) return;
        el.style.transform = `translateX(${driftPx(speed).toFixed(1)}px) scaleY(${amp.toFixed(2)})`;
        el.style.opacity = opGlow(baseOp).toFixed(2);
      };
      apply(0, bassAmp,   18, 0.85);
      apply(1, midAmp,    34, 0.75);
      apply(2, lowMidAmp, 12, 0.60);
      apply(3, trebAmp,   52, 0.50);

      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, [playing, bpm, energy]);

  return (
    <div style={{ position: "relative", width: "100%", height, overflow: "hidden", pointerEvents: "none" }}>
      <svg viewBox={`0 0 ${W} ${H}`} width="100%" height={H} preserveAspectRatio="none"
           style={{ display: "block" }}>
        <defs>
          <filter id="wfblur"><feGaussianBlur stdDeviation="0.5" /></filter>
        </defs>
        {paths.map((p, i) => (
          <path
            key={i}
            ref={(el) => { pathRefs.current[i] = el; }}
            d={p.d}
            fill="none"
            stroke={p.color}
            strokeWidth={p.width}
            strokeLinecap="round"
            filter="url(#wfblur)"
            style={{
              transformBox: "fill-box",
              transformOrigin: "center",
              opacity: p.opacity,
              willChange: "transform, opacity",
            }}
          />
        ))}
      </svg>
    </div>
  );
}

// ─── Vinyl disc (used standalone, behind the sleeve) ────────────────────────
function VinylDisc({ theme, size }) {
  const labelR = 0.34;
  const grooves = [];
  for (let i = 0; i < 28; i++) {
    grooves.push({
      r: 96 - i * 1.9,
      op: 0.04 + (i % 3 === 0 ? 0.05 : 0),
      w: i % 5 === 0 ? 0.5 : 0.35,
    });
  }
  return (
    <svg viewBox="-100 -100 200 200" width={size} height={size}
         style={{ display: "block", borderRadius: "50%" }}>
      <defs>
        <radialGradient id="vBody" cx="0.5" cy="0.5" r="0.5">
          <stop offset="0%"  stopColor="#1a1216" />
          <stop offset="78%" stopColor="#080406" />
          <stop offset="95%" stopColor="#000" />
        </radialGradient>
        <radialGradient id="vRim" cx="0.5" cy="0.5" r="0.5">
          <stop offset="92%" stopColor="rgba(255,255,255,0)" />
          <stop offset="96%" stopColor="rgba(255,255,255,.18)" />
          <stop offset="98%" stopColor="rgba(0,0,0,.5)" />
          <stop offset="100%" stopColor="rgba(0,0,0,.8)" />
        </radialGradient>
        <linearGradient id="vSheen" x1="0.2" y1="0.1" x2="0.8" y2="0.9">
          <stop offset="0%"  stopColor="rgba(255,255,255,0)" />
          <stop offset="40%" stopColor="rgba(255,255,255,.10)" />
          <stop offset="50%" stopColor="rgba(255,255,255,.22)" />
          <stop offset="60%" stopColor="rgba(255,255,255,.10)" />
          <stop offset="100%" stopColor="rgba(255,255,255,0)" />
        </linearGradient>
        <radialGradient id="vLabel" cx="0.5" cy="0.5" r="0.55">
          <stop offset="0%"   stopColor={theme.accentA} />
          <stop offset="45%"  stopColor={theme.accentC} />
          <stop offset="100%" stopColor={theme.accentB} />
        </radialGradient>
        <radialGradient id="vLabelShade" cx="0.4" cy="0.4" r="0.6">
          <stop offset="0%"   stopColor="rgba(255,255,255,.35)" />
          <stop offset="60%"  stopColor="rgba(255,255,255,0)" />
          <stop offset="100%" stopColor="rgba(0,0,0,.35)" />
        </radialGradient>
        <clipPath id="vLabelClip">
          <circle cx="0" cy="0" r={100 * labelR} />
        </clipPath>
        <filter id="vNoise">
          <feTurbulence baseFrequency="2.8" numOctaves="2" stitchTiles="stitch"/>
          <feColorMatrix values="0 0 0 0 0  0 0 0 0 0  0 0 0 0 0  0 0 0 .35 0"/>
        </filter>
      </defs>
      <circle cx="0" cy="0" r="100" fill="url(#vBody)" />
      <circle cx="0" cy="0" r="98" filter="url(#vNoise)" opacity="0.45" />
      {grooves.map((g, i) => (
        <circle key={i} cx="0" cy="0" r={g.r} fill="none"
                stroke={`rgba(255,255,255,${g.op})`} strokeWidth={g.w} />
      ))}
      <circle cx="0" cy="0" r="98" fill="url(#vSheen)" />
      <circle cx="0" cy="0" r="100" fill="url(#vRim)" />

      {/* Label */}
      <circle cx="0" cy="0" r={100 * labelR + 1.5} fill="rgba(0,0,0,.6)" />
      <circle cx="0" cy="0" r={100 * labelR} fill="url(#vLabel)" />
      <g clipPath="url(#vLabelClip)">
        <circle cx="0" cy="0" r={100 * labelR} fill="url(#vLabelShade)" />
        <circle cx="0" cy="0" r={100 * labelR * 0.85} fill="none"
                stroke="rgba(255,255,255,.18)" strokeWidth="0.3" />
        <circle cx="0" cy="0" r={100 * labelR * 0.55} fill="none"
                stroke="rgba(255,255,255,.2)" strokeWidth="0.4" />
        <path id="labelCurve" d={`M ${-100 * labelR * 0.72} 0 a ${100 * labelR * 0.72} ${100 * labelR * 0.72} 0 1 1 ${100 * labelR * 1.44} 0`}
              fill="none" />
        <text fontSize={100 * labelR * 0.18}
              fill="rgba(255,255,255,.85)"
              fontFamily="'Inter', sans-serif"
              fontWeight="700"
              letterSpacing="0.18em">
          <textPath href="#labelCurve" startOffset="50%" textAnchor="middle">
            LOUD · RIHANNA · 2010
          </textPath>
        </text>
        <text x="0" y={100 * labelR * 0.78} textAnchor="middle"
              fontSize={100 * labelR * 0.1}
              fill="rgba(255,255,255,.45)"
              fontFamily="'Inter', sans-serif"
              letterSpacing="0.2em">
          A-SIDE · 33⅓
        </text>
      </g>
      <circle cx="0" cy="0" r={100 * labelR} fill="none"
              stroke="rgba(0,0,0,.5)" strokeWidth="0.6" />

      {/* Spindle hole */}
      <circle cx="0" cy="0" r="2.2" fill="#000" />
      <circle cx="-0.6" cy="-0.6" r="0.6" fill="rgba(255,255,255,.4)" />
    </svg>
  );
}

// ─── Album-cover artwork (placeholder; later: real cover from MP3 tags) ─────
// Pass `imageUrl` to render a real image. Without it, renders a themed
// placeholder with the album title + artist.
function AlbumCover({ theme, title = "LOUD", artist = "RIHANNA", year = "2010", imageUrl }) {
  if (imageUrl) {
    return (
      <img src={imageUrl} alt={`${title} cover`}
           style={{ width: "100%", height: "100%", objectFit: "cover", display: "block" }} />
    );
  }
  return (
    <svg viewBox="0 0 100 100" width="100%" height="100%" preserveAspectRatio="xMidYMid slice"
         style={{ display: "block" }}>
      <defs>
        <linearGradient id="covBg" x1="0.1" y1="0" x2="0.9" y2="1">
          <stop offset="0%"   stopColor={theme.accentB} />
          <stop offset="55%"  stopColor={theme.accentC} />
          <stop offset="100%" stopColor={theme.accentA} />
        </linearGradient>
        <radialGradient id="covGlow" cx="0.78" cy="0.22" r="0.55">
          <stop offset="0%"   stopColor="rgba(255,255,255,.18)" />
          <stop offset="70%"  stopColor="rgba(255,255,255,0)" />
        </radialGradient>
        <radialGradient id="covSpot" cx="0.5" cy="0.5" r="0.5">
          <stop offset="0%"   stopColor="rgba(255,255,255,.06)" />
          <stop offset="80%"  stopColor="rgba(255,255,255,0)" />
        </radialGradient>
      </defs>
      <rect width="100" height="100" fill="url(#covBg)" />
      {/* Abstract concentric circles hinting at sound waves */}
      <g opacity="0.32" stroke="rgba(255,255,255,.85)" fill="none">
        <circle cx="78" cy="78" r="38" strokeWidth="0.6" />
        <circle cx="78" cy="78" r="28" strokeWidth="0.5" />
        <circle cx="78" cy="78" r="20" strokeWidth="0.4" />
        <circle cx="78" cy="78" r="14" strokeWidth="0.35" />
        <circle cx="78" cy="78" r="9" strokeWidth="0.3" />
      </g>
      <rect width="100" height="100" fill="url(#covSpot)" />
      <rect width="100" height="100" fill="url(#covGlow)" />

      {/* Title */}
      <text x="8" y="40"
            style={{ fontFamily: "'Inter', sans-serif" }}
            fontSize="22" fontWeight="900"
            fill="rgba(255,255,255,.97)"
            letterSpacing="0.02em">
        {title}
      </text>
      {/* Thin divider */}
      <rect x="8" y="44.5" width="20" height="0.55" fill="rgba(255,255,255,.85)" />
      {/* Artist */}
      <text x="8" y="51"
            style={{ fontFamily: "'Inter', sans-serif" }}
            fontSize="4.5" fontWeight="600"
            fill="rgba(255,255,255,.78)"
            letterSpacing="0.32em">
        {artist}
      </text>
      {/* Bottom right year mark */}
      <text x="92" y="94" textAnchor="end"
            style={{ fontFamily: "'Inter', sans-serif" }}
            fontSize="3.2" fontWeight="500"
            fill="rgba(255,255,255,.55)"
            letterSpacing="0.25em">
        {year}
      </text>
      {/* Bottom left side mark */}
      <text x="8" y="94"
            style={{ fontFamily: "'Inter', sans-serif" }}
            fontSize="3" fontWeight="600"
            fill="rgba(255,255,255,.45)"
            letterSpacing="0.32em">
        SIDE · A
      </text>
    </svg>
  );
}

// ─── Album Sleeve with vinyl peeking out the right side ─────────────────────
// Replaces the old turntable. Sleeve on the left, vinyl behind sliding out
// to the right by ~30% of its diameter. The vinyl spins when playing.
function Vinyl({ theme, playing, size = 360, coverUrl, title, artist, year }) {
  const sleeve = size;
  const vinylSize = size;                 // 12" LP in 12" sleeve
  const vinylCenterX = size * 0.8;        // center near right edge → ~30% pokes out
  const containerW = vinylCenterX + vinylSize / 2 + 6;

  return (
    <div style={{
      position: "relative",
      width: containerW,
      height: sleeve,
    }}>
      {/* Soft ground shadow */}
      <div aria-hidden style={{
        position: "absolute",
        left: 20, right: 20, bottom: -14,
        height: 26, borderRadius: "50%",
        background: "radial-gradient(ellipse, rgba(0,0,0,.55) 0%, transparent 70%)",
        filter: "blur(12px)",
        pointerEvents: "none",
      }} />

      {/* Vinyl behind sleeve */}
      <div style={{
        position: "absolute",
        left: vinylCenterX - vinylSize / 2,
        top: (sleeve - vinylSize) / 2,
        width: vinylSize,
        height: vinylSize,
        borderRadius: "50%",
        animation: playing ? "vinylSpin 12s linear infinite" : "none",
        transformOrigin: "center",
        filter: `drop-shadow(0 18px 30px rgba(0,0,0,.5))`,
      }}>
        <VinylDisc theme={theme} size={vinylSize} />
      </div>

      {/* Sleeve in front */}
      <div style={{
        position: "absolute",
        left: 0, top: 0,
        width: sleeve, height: sleeve,
        borderRadius: 6,
        overflow: "hidden",
        boxShadow: `
          0 25px 50px -12px rgba(0,0,0,.65),
          0 0 0 1px rgba(255,255,255,.06),
          0 1px 0 rgba(255,255,255,.18) inset
        `,
      }}>
        <AlbumCover theme={theme} title={title} artist={artist} year={year} imageUrl={coverUrl} />

        {/* Right edge — soft shadow where the vinyl exits (kept subtle) */}
        <div aria-hidden style={{
          position: "absolute", top: 0, bottom: 0, right: 0, width: 14,
          background: "linear-gradient(to left, rgba(0,0,0,.22), transparent)",
          pointerEvents: "none",
        }} />

        {/* Paper-texture grain (very subtle) */}
        <div aria-hidden style={{
          position: "absolute", inset: 0,
          opacity: 0.05,
          mixBlendMode: "overlay",
          backgroundImage: "url(\"data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='220' height='220'><filter id='n'><feTurbulence baseFrequency='0.85' numOctaves='2' stitchTiles='stitch'/></filter><rect width='100%25' height='100%25' filter='url(%23n)'/></svg>\")",
          pointerEvents: "none",
        }} />
      </div>
    </div>
  );
}


// ─── Progress bar (skeuomorphic inset rail) ─────────────────────────────────
function ProgressBar({ theme, current, total, onSeek }) {
  const ref = useRef(null);
  const [drag, setDrag] = useState(false);
  const pct = Math.min(100, Math.max(0, (current / total) * 100));

  const seek = (e) => {
    const rect = ref.current.getBoundingClientRect();
    const x = (e.clientX - rect.left) / rect.width;
    onSeek(Math.min(1, Math.max(0, x)) * total);
  };

  useEffect(() => {
    if (!drag) return;
    const m = (e) => seek(e);
    const u = () => setDrag(false);
    window.addEventListener("mousemove", m);
    window.addEventListener("mouseup", u);
    return () => {
      window.removeEventListener("mousemove", m);
      window.removeEventListener("mouseup", u);
    };
  }, [drag]);

  return (
    <div style={{ display: "flex", alignItems: "center", gap: 14, width: "100%" }}>
      <span style={{ fontSize: 12, color: "rgba(255,255,255,.65)", fontVariantNumeric: "tabular-nums", minWidth: 38,
                     textShadow: "0 1px 0 rgba(0,0,0,.5)" }}>
        {fmt(current)}
      </span>
      <div
        ref={ref}
        onMouseDown={(e) => { setDrag(true); seek(e); }}
        style={{
          flex: 1, height: 22, position: "relative", cursor: "pointer",
          display: "flex", alignItems: "center",
        }}
      >
        {/* Inset rail */}
        <div style={{
          position: "absolute", left: 0, right: 0, height: 7, borderRadius: 999,
          background: "linear-gradient(180deg, rgba(0,0,0,.55), rgba(0,0,0,.25))",
          boxShadow: `
            inset 0 1px 2px rgba(0,0,0,.7),
            inset 0 -1px 0 rgba(255,255,255,.07),
            0 1px 0 rgba(255,255,255,.05)
          `,
        }} />
        {/* Filled portion */}
        <div style={{
          position: "absolute", left: 1, width: `calc(${pct}% - 2px)`, height: 5, top: "calc(50% - 2.5px)", borderRadius: 999,
          background: `linear-gradient(180deg, ${theme.accentA}, ${theme.accentB})`,
          boxShadow: `
            inset 0 1px 0 rgba(255,255,255,.5),
            inset 0 -1px 0 rgba(0,0,0,.2),
            0 0 10px ${theme.accentB}88,
            0 0 16px ${theme.accentA}55
          `,
        }} />
        {/* Handle — glossy 3D ball */}
        <div style={{
          position: "absolute", left: `calc(${pct}% - 9px)`,
          width: 18, height: 18, borderRadius: "50%",
          background: `
            radial-gradient(circle at 30% 25%, rgba(255,255,255,.95) 0%, rgba(255,255,255,.3) 35%, rgba(255,255,255,0) 60%),
            linear-gradient(160deg, ${theme.accentA} 0%, ${theme.accentB} 100%)
          `,
          boxShadow: `
            inset 0 1px 1px rgba(255,255,255,.6),
            inset 0 -1px 2px rgba(0,0,0,.4),
            0 2px 5px rgba(0,0,0,.5),
            0 0 0 1px rgba(0,0,0,.3),
            0 0 12px ${theme.accentB}aa
          `,
          transition: drag ? "none" : "left .15s linear",
        }} />
      </div>
      <span style={{ fontSize: 12, color: "rgba(255,255,255,.65)", fontVariantNumeric: "tabular-nums", minWidth: 42, textAlign: "right",
                     textShadow: "0 1px 0 rgba(0,0,0,.5)" }}>
        -{fmt(total - current)}
      </span>
    </div>
  );
}

// ─── Volume slider ───────────────────────────────────────────────────────────
function VolumeSlider({ theme, value, onChange }) {
  const ref = useRef(null);
  const [drag, setDrag] = useState(false);
  const set = (e) => {
    const rect = ref.current.getBoundingClientRect();
    const x = (e.clientX - rect.left) / rect.width;
    onChange(Math.min(1, Math.max(0, x)));
  };
  useEffect(() => {
    if (!drag) return;
    const m = (e) => set(e);
    const u = () => setDrag(false);
    window.addEventListener("mousemove", m);
    window.addEventListener("mouseup", u);
    return () => {
      window.removeEventListener("mousemove", m);
      window.removeEventListener("mouseup", u);
    };
  }, [drag]);
  return (
    <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
      <Icon name={value === 0 ? "volume-x" : value < 0.5 ? "volume-1" : "volume-2"} size={14}
            color="rgba(255,255,255,.65)" />
      <div
        ref={ref}
        onMouseDown={(e) => { setDrag(true); set(e); }}
        style={{ width: 100, height: 18, position: "relative", display: "flex", alignItems: "center", cursor: "pointer" }}
      >
        <div style={{
          position: "absolute", left: 0, right: 0, height: 5, borderRadius: 999,
          background: "linear-gradient(180deg, rgba(0,0,0,.5), rgba(0,0,0,.2))",
          boxShadow: "inset 0 1px 2px rgba(0,0,0,.7), inset 0 -1px 0 rgba(255,255,255,.06)",
        }} />
        <div style={{
          position: "absolute", left: 1, width: `calc(${value * 100}% - 2px)`, height: 3, top: "calc(50% - 1.5px)", borderRadius: 999,
          background: `linear-gradient(180deg, ${theme.accentA}, ${theme.accentB})`,
          boxShadow: `0 0 6px ${theme.accentB}88, inset 0 1px 0 rgba(255,255,255,.4)`,
        }} />
        <div style={{
          position: "absolute", left: `calc(${value * 100}% - 7px)`,
          width: 14, height: 14, borderRadius: "50%",
          background: `
            radial-gradient(circle at 30% 25%, rgba(255,255,255,.95), rgba(255,255,255,.3) 40%, transparent 65%),
            linear-gradient(160deg, #f4f6fa, #aab2c2)
          `,
          boxShadow: `
            inset 0 1px 1px rgba(255,255,255,.7),
            inset 0 -1px 1px rgba(0,0,0,.25),
            0 2px 3px rgba(0,0,0,.5),
            0 0 0 1px rgba(0,0,0,.35),
            0 0 6px ${theme.accentB}66
          `,
        }} />
      </div>
    </div>
  );
}

// ─── Icon library (stroke) ───────────────────────────────────────────────────
function Icon({ name, size = 20, color = "currentColor", strokeWidth = 1.8, fill = "none" }) {
  const props = {
    width: size, height: size, viewBox: "0 0 24 24",
    fill, stroke: color, strokeWidth, strokeLinecap: "round", strokeLinejoin: "round",
    style: { display: "block", flexShrink: 0 },
  };
  switch (name) {
    case "play": return (
      <svg {...props} fill={color} stroke="none"><path d="M7 5.5v13a.5.5 0 0 0 .77.42l10.5-6.5a.5.5 0 0 0 0-.84L7.77 5.08A.5.5 0 0 0 7 5.5z" /></svg>
    );
    case "pause": return (
      <svg {...props} fill={color} stroke="none"><rect x="6.5" y="5" width="3.6" height="14" rx="1" /><rect x="13.9" y="5" width="3.6" height="14" rx="1" /></svg>
    );
    case "prev": return (
      <svg {...props} fill={color} stroke="none"><path d="M6 5h2v14H6zM20 5.5v13a.5.5 0 0 1-.77.42L8.73 12.42a.5.5 0 0 1 0-.84L19.23 5.08A.5.5 0 0 1 20 5.5z" /></svg>
    );
    case "next": return (
      <svg {...props} fill={color} stroke="none"><path d="M16 5h2v14h-2zM4 5.5v13a.5.5 0 0 0 .77.42l10.5-6.5a.5.5 0 0 0 0-.84L4.77 5.08A.5.5 0 0 0 4 5.5z" /></svg>
    );
    case "shuffle": return (
      <svg {...props}><path d="M16 3h5v5M21 3l-7.5 7.5M8 3h-5v18h5l13-13M16 21h5v-5M14 14l7 7"/></svg>
    );
    case "repeat": return (
      <svg {...props}><path d="M17 1l4 4-4 4"/><path d="M3 11V9a4 4 0 0 1 4-4h14"/><path d="M7 23l-4-4 4-4"/><path d="M21 13v2a4 4 0 0 1-4 4H3"/></svg>
    );
    case "list": return (
      <svg {...props}><line x1="8" y1="6" x2="21" y2="6"/><line x1="8" y1="12" x2="21" y2="12"/><line x1="8" y1="18" x2="21" y2="18"/><circle cx="4" cy="6" r="1" fill={color} stroke="none"/><circle cx="4" cy="12" r="1" fill={color} stroke="none"/><circle cx="4" cy="18" r="1" fill={color} stroke="none"/></svg>
    );
    case "heart": return (
      <svg {...props}><path d="M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78L12 21l8.84-8.61a5.5 5.5 0 0 0 0-7.78z"/></svg>
    );
    case "heart-fill": return (
      <svg {...props} fill={color}><path d="M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78L12 21l8.84-8.61a5.5 5.5 0 0 0 0-7.78z"/></svg>
    );
    case "download": return (
      <svg {...props}><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/></svg>
    );
    case "message": return (
      <svg {...props}><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg>
    );
    case "more": return (
      <svg {...props} fill={color} stroke="none"><circle cx="12" cy="5" r="1.6"/><circle cx="12" cy="12" r="1.6"/><circle cx="12" cy="19" r="1.6"/></svg>
    );
    case "settings": return (
      <svg {...props}>
        <path d="M12 2.5l1.2 2.2 2.5-.4.4 2.5 2.2 1.2-1.2 2.2 1.2 2.2-2.2 1.2-.4 2.5-2.5-.4L12 17.7l-1.2-2.2-2.5.4-.4-2.5-2.2-1.2 1.2-2.2-1.2-2.2 2.2-1.2.4-2.5 2.5.4L12 2.5z" fill={color} stroke="none"/>
        <circle cx="12" cy="10.1" r="2.6" fill="#1a0f30" stroke="none"/>
      </svg>
    );
    case "minimize": return (
      <svg {...props}><line x1="5" y1="12" x2="19" y2="12"/></svg>
    );
    case "mini": return (
      <svg {...props}>
        <rect x="3" y="6" width="18" height="12" rx="2" />
        <rect x="7" y="10" width="10" height="4" rx="1" fill={color} stroke="none" />
      </svg>
    );
    case "expand": return (
      <svg {...props}>
        <rect x="3" y="4" width="18" height="16" rx="2" />
        <line x1="3" y1="9" x2="21" y2="9" />
      </svg>
    );
    case "volume-x": return (
      <svg {...props}><polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5" fill={color}/><line x1="23" y1="9" x2="17" y2="15"/><line x1="17" y1="9" x2="23" y2="15"/></svg>
    );
    case "volume-1": return (
      <svg {...props}><polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5" fill={color}/><path d="M15.54 8.46a5 5 0 0 1 0 7.07"/></svg>
    );
    case "volume-2": return (
      <svg {...props}><polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5" fill={color}/><path d="M15.54 8.46a5 5 0 0 1 0 7.07"/><path d="M19.07 4.93a10 10 0 0 1 0 14.14"/></svg>
    );
    case "search": return (
      <svg {...props}><circle cx="11" cy="11" r="7"/><line x1="21" y1="21" x2="16.65" y2="16.65"/></svg>
    );
    case "mic": return (
      <svg {...props}><path d="M12 2a3 3 0 0 0-3 3v7a3 3 0 0 0 6 0V5a3 3 0 0 0-3-3z"/><path d="M19 10v2a7 7 0 0 1-14 0v-2"/><line x1="12" y1="19" x2="12" y2="23"/><line x1="8" y1="23" x2="16" y2="23"/></svg>
    );
    default: return null;
  }
}

// ─── Circular control button (skeuomorphic) ─────────────────────────────────
function CtrlButton({ children, onClick, primary, theme, size = 46, title, pressed }) {
  const [hover, setHover] = useState(false);
  const [down, setDown] = useState(false);
  if (primary) {
    return (
      <button
        onClick={onClick}
        onMouseEnter={() => setHover(true)}
        onMouseLeave={() => { setHover(false); setDown(false); }}
        onMouseDown={() => setDown(true)}
        onMouseUp={() => setDown(false)}
        title={title}
        style={{
          width: 70, height: 70, borderRadius: "50%",
          background: `
            radial-gradient(circle at 30% 25%, rgba(255,255,255,.4) 0%, rgba(255,255,255,0) 35%),
            linear-gradient(160deg, ${theme.accentA} 0%, ${theme.accentB} 100%)
          `,
          border: "none",
          color: "#fff",
          cursor: "pointer",
          display: "flex", alignItems: "center", justifyContent: "center",
          boxShadow: down
            ? `
              inset 0 2px 6px rgba(0,0,0,.45),
              inset 0 -1px 1px rgba(255,255,255,.2),
              0 1px 2px rgba(0,0,0,.4),
              0 0 0 3px rgba(255,255,255,.04),
              0 0 24px ${theme.accentB}66
            `
            : `
              inset 0 1px 1px rgba(255,255,255,.5),
              inset 0 -2px 4px rgba(0,0,0,.35),
              0 1px 0 rgba(255,255,255,.15),
              0 6px 14px rgba(0,0,0,.45),
              0 0 0 1px rgba(0,0,0,.3),
              0 0 0 3px rgba(255,255,255,.06),
              0 0 30px ${theme.accentB}${hover ? "aa" : "66"}
            `,
          transition: "box-shadow .2s, transform .1s",
          transform: down ? "translateY(1px) scale(.98)" : (hover ? "scale(1.03)" : "scale(1)"),
        }}
      >
        <div style={{ filter: "drop-shadow(0 1px 1px rgba(0,0,0,.4))" }}>
          {children}
        </div>
      </button>
    );
  }
  return (
    <button
      onClick={onClick}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => { setHover(false); setDown(false); }}
      onMouseDown={() => setDown(true)}
      onMouseUp={() => setDown(false)}
      title={title}
      style={{
        width: size, height: size, borderRadius: "50%",
        background: down
          ? "radial-gradient(circle at 50% 50%, rgba(0,0,0,.3), rgba(255,255,255,.04))"
          : `radial-gradient(circle at 35% 30%, rgba(255,255,255,${hover ? .18 : .12}) 0%, rgba(255,255,255,.02) 60%, rgba(0,0,0,.2) 100%)`,
        border: "none",
        color: "#fff",
        cursor: "pointer",
        display: "flex", alignItems: "center", justifyContent: "center",
        boxShadow: down ? `
          inset 0 2px 5px rgba(0,0,0,.5),
          inset 0 -1px 1px rgba(255,255,255,.05),
          0 0 0 1px rgba(0,0,0,.4)
        ` : `
          inset 0 1px 0.5px rgba(255,255,255,.25),
          inset 0 -1px 2px rgba(0,0,0,.35),
          0 1px 2px rgba(0,0,0,.4),
          0 0 0 1px rgba(255,255,255,${hover ? .15 : .08})
        `,
        transition: "box-shadow .15s, transform .08s, background .15s",
        transform: down ? "translateY(.5px)" : "none",
      }}
    >
      <div style={{ filter: "drop-shadow(0 1px 0 rgba(0,0,0,.4))" }}>
        {children}
      </div>
    </button>
  );
}

// ─── Icon-only ghost button (no border) ─────────────────────────────────────
function GhostButton({ children, onClick, title, active, theme }) {
  const [hover, setHover] = useState(false);
  return (
    <button
      onClick={onClick}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      title={title}
      style={{
        background: "transparent",
        border: "none",
        color: active ? theme.accentA : (hover ? "#fff" : "rgba(255,255,255,.65)"),
        cursor: "pointer",
        padding: 8,
        display: "flex", alignItems: "center", justifyContent: "center",
        transition: "color .15s",
      }}
    >
      {children}
    </button>
  );
}

Object.assign(window, {
  THEMES, Vinyl, Waveform, ProgressBar, VolumeSlider,
  Icon, CtrlButton, GhostButton, TrafficLights, fmt,
});
