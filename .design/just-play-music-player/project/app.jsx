// app.jsx â€” Just Play desktop music player main app

const { useState, useEffect, useRef } = React;

const TWEAK_DEFAULTS = /*EDITMODE-BEGIN*/{
  "theme": "aurora",
  "vinylSpin": true,
  "waveform": true,
  "rightPanel": "queue",
  "compact": false
}/*EDITMODE-END*/;

// â”€â”€â”€ Mock album: Rihanna - LOUD (2010) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
const ALBUM = {
  title: "LOUD",
  artist: "Rihanna",
  date: "2010-11-15",
  cover: null, // placeholder â€” drawn as SVG label
};

const TRACKS = [
  { n: 1,  title: "S&M",                            dur: 244, bpm: 128, key: "11A", energy: 8 },
  { n: 2,  title: "What's My Name?",                dur: 264, bpm: 105, key: "9A",  energy: 7, feat: "Drake" },
  { n: 3,  title: "Cheers (Drink to That)",         dur: 203, bpm: 124, key: "4A",  energy: 7 },
  { n: 4,  title: "Fading",                         dur: 201, bpm: 87,  key: "5A",  energy: 5 },
  { n: 5,  title: "Only Girl (In the World)",       dur: 235, bpm: 126, key: "8A",  energy: 8 },
  { n: 6,  title: "California King Bed",            dur: 251, bpm: 70,  key: "10A", energy: 5 },
  { n: 7,  title: "Man Down",                       dur: 267, bpm: 96,  key: "11B", energy: 6 },
  { n: 8,  title: "Raining Men",                    dur: 222, bpm: 96,  key: "7A",  energy: 7, feat: "Nicki Minaj" },
  { n: 9,  title: "Complicated",                    dur: 256, bpm: 122, key: "4A",  energy: 6 },
  { n: 10, title: "Skin",                           dur: 295, bpm: 90,  key: "6A",  energy: 5 },
  { n: 11, title: "Love the Way You Lie (Part II)", dur: 223, bpm: 87,  key: "12A", energy: 6, feat: "Eminem" },
];

// Mock lyrics for "Only Girl (In the World)" â€” abstract placeholder lines
const LYRICS = [
  { t: 0,   line: "" },
  { t: 8,   line: "La, la, la, la, la, la, la, la, la" },
  { t: 16,  line: "I want you to love me" },
  { t: 22,  line: "Like I'm a hot ride" },
  { t: 28,  line: "Be thinking of me" },
  { t: 34,  line: "Do me in the bedside" },
  { t: 42,  line: "I want to be your only" },
  { t: 50,  line: "Want to make you feel like" },
  { t: 58,  line: "You're the only boy in the world" },
  { t: 70,  line: "Like I'm the only girl in the world" },
  { t: 84,  line: "Want you to make me feel" },
  { t: 96,  line: "Like I'm the only girl in the world" },
  { t: 108, line: "Like I'm the only one that you'll ever love" },
  { t: 124, line: "Like I'm the only one who knows your heart" },
  { t: 140, line: "Only girl in the world" },
  { t: 158, line: "Like I'm the only one in command" },
  { t: 174, line: "'Cause you're the only one who's in my heart" },
];

// â”€â”€â”€ Album cover SVG (placeholder when no image) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
function CoverArt({ theme, size = 64 }) {
  return (
    <svg width={size} height={size} viewBox="0 0 100 100" style={{ display: "block", borderRadius: 8 }}>
      <defs>
        <linearGradient id={`cov-${theme.name}`} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0%" stopColor={theme.accentB} />
          <stop offset="50%" stopColor={theme.accentC} />
          <stop offset="100%" stopColor={theme.accentA} />
        </linearGradient>
        <radialGradient id={`cov-r-${theme.name}`} cx="0.7" cy="0.3" r="0.7">
          <stop offset="0%" stopColor="rgba(255,255,255,.4)" />
          <stop offset="100%" stopColor="rgba(255,255,255,0)" />
        </radialGradient>
      </defs>
      <rect width="100" height="100" fill={`url(#cov-${theme.name})`} />
      <rect width="100" height="100" fill={`url(#cov-r-${theme.name})`} />
      <text x="50" y="56" textAnchor="middle"
            style={{ fontFamily: "'Major Mono Display', monospace", fontWeight: 700, letterSpacing: ".05em" }}
            fontSize="20" fill="rgba(255,255,255,.95)">
        LOUD
      </text>
      <text x="50" y="72" textAnchor="middle"
            style={{ fontFamily: "'Inter', sans-serif" }}
            fontSize="7" fill="rgba(255,255,255,.7)" letterSpacing="2">
        RIHANNA
      </text>
    </svg>
  );
}

// â”€â”€â”€ Queue list â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
// Grid columns: # | title | BPM | KEY | ENERGY | DURATION
const QUEUE_GRID = "22px minmax(0,1fr) 44px 40px 56px 44px";

function Queue({ theme, currentIdx, onSelect }) {
  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      {/* Header bar */}
      <div style={{
        display: "grid",
        gridTemplateColumns: QUEUE_GRID,
        alignItems: "baseline",
        columnGap: 12,
        padding: "0 12px 8px 12px",
        marginBottom: 4,
        borderBottom: "1px solid rgba(255,255,255,.06)",
        fontSize: 10, letterSpacing: "0.16em", textTransform: "uppercase",
        color: "rgba(255,255,255,.45)", fontWeight: 600,
      }}>
        <span>#</span>
        <span style={{ display: "flex", gap: 8, alignItems: "baseline" }}>
          <span style={{ color: "rgba(255,255,255,.6)" }}>Up Next</span>
          <span style={{ color: "rgba(255,255,255,.3)" }}>Â· {TRACKS.length} tracks</span>
        </span>
        <span style={{ textAlign: "right" }}>BPM</span>
        <span style={{ textAlign: "center" }}>Key</span>
        <span style={{ textAlign: "center" }}>Energy</span>
        <span style={{ textAlign: "right" }}>{fmt(TRACKS.reduce((a, t) => a + t.dur, 0))}</span>
      </div>

      <div style={{
        flex: 1, minHeight: 0, overflowY: "auto",
        marginRight: -8, paddingRight: 8,
      }}
      className="just-play-scroll">
        {TRACKS.map((t, i) => {
          const active = i === currentIdx;
          return (
            <QueueRow key={i}
                      track={t}
                      active={active}
                      theme={theme}
                      onClick={() => onSelect(i)} />
          );
        })}
      </div>
    </div>
  );
}

// Single row â€” kept as its own component so we can hover-state cleanly
function QueueRow({ track, active, theme, onClick }) {
  const [hover, setHover] = useState(false);
  return (
    <button
      onClick={onClick}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: "grid",
        gridTemplateColumns: QUEUE_GRID,
        alignItems: "center",
        columnGap: 12,
        width: "100%",
        padding: "9px 12px",
        border: "none",
        borderRadius: 10,
        background: active
          ? `linear-gradient(90deg, ${theme.accentC}22, ${theme.accentB}10 60%, transparent)`
          : hover ? "rgba(255,255,255,.04)" : "transparent",
        color: "#fff",
        cursor: "pointer",
        textAlign: "left",
        marginBottom: 1,
        transition: "background .15s",
        position: "relative",
        fontFamily: "inherit",
      }}
    >
      {active && (
        <div style={{
          position: "absolute", left: 2, top: 6, bottom: 6, width: 2.5, borderRadius: 999,
          background: `linear-gradient(to bottom, ${theme.accentA}, ${theme.accentB})`,
          boxShadow: `0 0 6px ${theme.accentB}`,
        }} />
      )}

      {/* # / Playing indicator */}
      <div style={{
        fontSize: 11.5, fontVariantNumeric: "tabular-nums",
        color: active ? theme.accentA : "rgba(255,255,255,.35)",
        fontWeight: active ? 600 : 400,
        textAlign: "left",
        display: "flex", alignItems: "center",
      }}>
        {active ? <PlayingBars color={theme.accentA} /> : String(track.n).padStart(2, "0")}
      </div>

      {/* Title + artist */}
      <div style={{ minWidth: 0 }}>
        <div style={{
          fontSize: 13.5,
          fontWeight: active ? 600 : 500,
          color: active ? "#fff" : "rgba(255,255,255,.88)",
          whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis",
          letterSpacing: "-0.005em",
        }}>
          {track.title}
        </div>
        <div style={{
          fontSize: 11, color: "rgba(255,255,255,.42)", marginTop: 1,
          whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis",
        }}>
          {ALBUM.artist}{track.feat ? ` feat. ${track.feat}` : ""}
        </div>
      </div>

      {/* BPM */}
      <div style={{
        fontSize: 12, fontVariantNumeric: "tabular-nums",
        color: active ? "rgba(255,255,255,.85)" : "rgba(255,255,255,.55)",
        textAlign: "right",
        fontWeight: 500,
      }}>
        {track.bpm}
      </div>

      {/* Camelot key â€” pill */}
      <div style={{ display: "flex", justifyContent: "center" }}>
        <KeyPill keyCode={track.key} active={active} theme={theme} />
      </div>

      {/* Energy bars */}
      <div style={{ display: "flex", justifyContent: "center" }}>
        <EnergyBars value={track.energy} active={active} theme={theme} />
      </div>

      {/* Duration */}
      <div style={{
        fontSize: 12, fontVariantNumeric: "tabular-nums",
        color: active ? "rgba(255,255,255,.85)" : "rgba(255,255,255,.55)",
        textAlign: "right",
        fontWeight: 500,
      }}>
        {fmt(track.dur)}
      </div>
    </button>
  );
}

// Camelot key colored pill â€” outer ring (A=cool, B=warm), letter-suffix
function KeyPill({ keyCode, active, theme }) {
  // Camelot wheel-ish color mapping: A=blue/cool, B=warm/pink
  const isB = keyCode.endsWith("B");
  const bg = isB
    ? `linear-gradient(135deg, ${theme.accentB}55, ${theme.accentB}22)`
    : `linear-gradient(135deg, ${theme.accentA}55, ${theme.accentC}22)`;
  const border = isB ? `${theme.accentB}80` : `${theme.accentA}80`;
  return (
    <div style={{
      fontSize: 10.5, fontWeight: 700, letterSpacing: "0.04em",
      padding: "3px 7px",
      borderRadius: 999,
      background: bg,
      color: active ? "#fff" : "rgba(255,255,255,.85)",
      border: `1px solid ${border}`,
      fontVariantNumeric: "tabular-nums",
      lineHeight: 1, minWidth: 30, textAlign: "center",
      boxShadow: active ? `0 0 8px ${isB ? theme.accentB : theme.accentA}66` : "none",
    }}>
      {keyCode}
    </div>
  );
}

// Energy bars â€” 5 small vertical bars filled per value (5..9 typical)
function EnergyBars({ value, active, theme }) {
  // Map value 1..10 to 5 bars (so each bar = 2 energy units)
  // value 5 -> 3 bars, 8 -> 4 bars, 9 -> 5 bars
  const filled = Math.max(1, Math.min(5, Math.round(value / 2)));
  return (
    <div style={{
      display: "flex", alignItems: "flex-end", gap: 2, height: 14,
    }}>
      {[0, 1, 2, 3, 4].map((i) => {
        const isFilled = i < filled;
        const h = 4 + i * 2; // 4, 6, 8, 10, 12
        const color = isFilled
          ? (i >= 3 ? theme.accentB : i >= 2 ? theme.accentC : theme.accentA)
          : "rgba(255,255,255,.12)";
        return (
          <div key={i} style={{
            width: 2.5, height: h, borderRadius: 1,
            background: color,
            boxShadow: isFilled && active ? `0 0 4px ${color}` : "none",
            transition: "background .15s",
          }} />
        );
      })}
      <span style={{
        marginLeft: 4,
        fontSize: 10, fontVariantNumeric: "tabular-nums", fontWeight: 600,
        color: active ? "#fff" : "rgba(255,255,255,.5)",
        minWidth: 8,
      }}>
        {value}
      </span>
    </div>
  );
}

// Three animated bars for "now playing" indicator
function PlayingBars({ color }) {
  return (
    <svg width="14" height="14" viewBox="0 0 14 14" style={{ display: "inline-block" }}>
      {[0, 1, 2].map((i) => (
        <rect
          key={i}
          x={1 + i * 4.5}
          y={3}
          width={2.5}
          height={8}
          rx={1}
          fill={color}
          style={{
            transformOrigin: "center bottom",
            animation: `bar${i} ${0.7 + i * 0.1}s ease-in-out infinite alternate`,
          }}
        />
      ))}
      <style>{`
        @keyframes bar0 { 0% { transform: scaleY(0.3) translateY(2px) } 100% { transform: scaleY(1) translateY(0) } }
        @keyframes bar1 { 0% { transform: scaleY(0.6) translateY(1px) } 100% { transform: scaleY(0.4) translateY(2px) } }
        @keyframes bar2 { 0% { transform: scaleY(0.4) translateY(2px) } 100% { transform: scaleY(0.9) translateY(0) } }
      `}</style>
    </svg>
  );
}

// â”€â”€â”€ Lyrics scroller â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
function Lyrics({ theme, current }) {
  const ref = useRef(null);
  // Find current line
  let activeIdx = 0;
  for (let i = 0; i < LYRICS.length; i++) {
    if (LYRICS[i].t <= current) activeIdx = i;
  }
  useEffect(() => {
    const el = ref.current?.querySelector(`[data-lyric-i="${activeIdx}"]`);
    if (el && ref.current) {
      const top = el.offsetTop - ref.current.clientHeight / 2 + el.clientHeight / 2;
      ref.current.scrollTo({ top, behavior: "smooth" });
    }
  }, [activeIdx]);

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
      <div style={{
        fontSize: 11, letterSpacing: "0.18em", textTransform: "uppercase",
        color: "rgba(255,255,255,.55)", fontWeight: 600,
        marginBottom: 14,
      }}>
        Lyrics Â· Synced
      </div>
      <div
        ref={ref}
        className="just-play-scroll"
        style={{
          flex: 1, minHeight: 0, overflowY: "auto",
          paddingRight: 8, marginRight: -8,
          maskImage: "linear-gradient(to bottom, transparent 0%, #000 12%, #000 88%, transparent 100%)",
          WebkitMaskImage: "linear-gradient(to bottom, transparent 0%, #000 12%, #000 88%, transparent 100%)",
        }}
      >
        {LYRICS.map((l, i) => {
          const active = i === activeIdx;
          const dist = Math.abs(i - activeIdx);
          return (
            <div
              key={i}
              data-lyric-i={i}
              style={{
                fontSize: active ? 22 : 18,
                fontWeight: active ? 700 : 500,
                lineHeight: 1.35,
                padding: "10px 0",
                color: active ? "#fff" : `rgba(255,255,255,${Math.max(0.18, 0.6 - dist * 0.1)})`,
                transition: "all .35s",
                textShadow: active ? `0 0 28px ${theme.accentB}66` : "none",
                letterSpacing: "-0.005em",
              }}
            >
              {l.line || "â™ª"}
            </div>
          );
        })}
      </div>
    </div>
  );
}

// Reusable transport row
function TransportRow({ theme, playing, prev, next, togglePlay, shuffle, setShuffle,
                       repeatMode, setRepeatMode, repeatIcon, volume, setVolume,
                       liked, setLiked, compact, onToggleCompact }) {
  return (
    <div style={{
      display: "grid",
      gridTemplateColumns: "1fr auto 1fr",
      alignItems: "center",
      padding: compact ? "8px 20px 12px 20px" : "10px 36px 14px 36px",
      gap: compact ? 12 : 24,
      position: "relative", zIndex: 1,
    }}>
      <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
        <GhostButton title="Shuffle" theme={theme} active={shuffle}
                     onClick={() => setShuffle((s) => !s)}>
          <Icon name="shuffle" size={compact ? 16 : 18} color="currentColor" />
        </GhostButton>
        <GhostButton title="Repeat" theme={theme} active={repeatMode > 0}
                     onClick={() => setRepeatMode((m) => (m + 1) % 3)}>
          {repeatIcon}
        </GhostButton>
      </div>

      <div style={{ display: "flex", alignItems: "center", gap: compact ? 12 : 18 }}>
        <CtrlButton theme={theme} title="Previous (Shift+â†)" onClick={prev} size={compact ? 38 : 46}>
          <Icon name="prev" size={compact ? 18 : 20} color="#fff" />
        </CtrlButton>
        <CtrlButton primary theme={theme} title="Play/Pause (Space)" onClick={togglePlay}>
          <Icon name={playing ? "pause" : "play"} size={compact ? 22 : 26} color="#fff" />
        </CtrlButton>
        <CtrlButton theme={theme} title="Next (Shift+â†’)" onClick={next} size={compact ? 38 : 46}>
          <Icon name="next" size={compact ? 18 : 20} color="#fff" />
        </CtrlButton>
      </div>

      <div style={{ display: "flex", alignItems: "center", gap: compact ? 8 : 14, justifyContent: "flex-end" }}>
        {!compact && <VolumeSlider theme={theme} value={volume} onChange={setVolume} />}
        {!compact && <div style={{ width: 1, height: 18, background: "rgba(255,255,255,.12)" }} />}
        <GhostButton title={liked ? "Unlike" : "Like"} theme={theme} active={liked}
                     onClick={() => setLiked((l) => !l)}>
          <Icon name={liked ? "heart-fill" : "heart"} size={compact ? 16 : 18}
                color={liked ? theme.accentB : "currentColor"} />
        </GhostButton>
        <GhostButton title="More" theme={theme}>
          <Icon name="more" size={compact ? 16 : 18} color="currentColor" />
        </GhostButton>
      </div>
    </div>
  );
}

// â”€â”€â”€ Main App â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
function App() {
  const [t, setTweak] = useTweaks(TWEAK_DEFAULTS);
  const theme = THEMES[t.theme] || THEMES.aurora;

  const [playing, setPlaying] = useState(true);
  const [currentIdx, setCurrentIdx] = useState(4); // Only Girl
  const [progress, setProgress] = useState(185); // 3:05
  const [volume, setVolume] = useState(0.72);
  const [liked, setLiked] = useState(true);
  const [shuffle, setShuffle] = useState(false);
  const [repeatMode, setRepeatMode] = useState(0); // 0 off, 1 all, 2 one
  const [rightTab, setRightTab] = useState(t.rightPanel || "queue");

  useEffect(() => { setRightTab(t.rightPanel); }, [t.rightPanel]);

  const track = TRACKS[currentIdx];

  // Apply canvas dimensions for compact / full layouts
  useEffect(() => {
    const root = document.documentElement;
    if (t.compact) {
      root.style.setProperty("--canvas-w", "640px");
      root.style.setProperty("--canvas-h", "660px");
    } else {
      root.style.setProperty("--canvas-w", "1280px");
      root.style.setProperty("--canvas-h", "820px");
    }
    // Trigger the host scale-to-fit
    window.dispatchEvent(new Event("resize"));
  }, [t.compact]);

  // Time ticker
  useEffect(() => {
    if (!playing) return;
    const iv = setInterval(() => {
      setProgress((p) => {
        if (p + 1 >= track.dur) {
          if (repeatMode === 2) return 0;
          // advance
          setCurrentIdx((i) => (i + 1) % TRACKS.length);
          return 0;
        }
        return p + 1;
      });
    }, 1000);
    return () => clearInterval(iv);
  }, [playing, track.dur, repeatMode]);

  // Reset progress when track changes
  useEffect(() => { setProgress(0); }, [currentIdx]);

  const prev = () => {
    if (progress > 3) setProgress(0);
    else setCurrentIdx((i) => (i - 1 + TRACKS.length) % TRACKS.length);
  };
  const next = () => setCurrentIdx((i) => (i + 1) % TRACKS.length);
  const togglePlay = () => setPlaying((p) => !p);

  // Keyboard shortcuts
  useEffect(() => {
    const onKey = (e) => {
      if (e.target.tagName === "INPUT" || e.target.tagName === "TEXTAREA") return;
      if (e.code === "Space") { e.preventDefault(); togglePlay(); }
      else if (e.code === "ArrowRight" && e.shiftKey) next();
      else if (e.code === "ArrowLeft" && e.shiftKey) prev();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  // Cycle repeat mode icon
  const repeatIcon = (
    <div style={{ position: "relative" }}>
      <Icon name="repeat" size={18}
            color={repeatMode > 0 ? theme.accentA : "rgba(255,255,255,.65)"} />
      {repeatMode === 2 && (
        <div style={{
          position: "absolute", top: -4, right: -6,
          fontSize: 9, fontWeight: 800,
          color: theme.accentA,
          background: "rgba(0,0,0,.4)",
          borderRadius: 99, padding: "0 3px",
        }}>1</div>
      )}
    </div>
  );

  return (
    <div style={{
      position: "absolute", inset: 0, color: "#fff",
      fontFamily: "'Inter', ui-sans-serif, system-ui, sans-serif",
      background: `
        radial-gradient(ellipse 80% 50% at 70% 20%, ${theme.glow} 0%, transparent 55%),
        radial-gradient(ellipse 60% 50% at 10% 80%, ${theme.accentC}55 0%, transparent 55%),
        linear-gradient(160deg, ${theme.bgFrom} 0%, ${theme.bgVia} 50%, ${theme.bgTo} 100%)
      `,
      display: "flex", flexDirection: "column",
      overflow: "hidden",
      borderRadius: 14,
    }}>

      {/* Subtle noise/grain */}
      <div aria-hidden style={{
        position: "absolute", inset: 0, pointerEvents: "none",
        opacity: 0.05, mixBlendMode: "overlay",
        backgroundImage: "url(\"data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='200' height='200'><filter id='n'><feTurbulence baseFrequency='0.9' numOctaves='2' stitchTiles='stitch'/></filter><rect width='100%25' height='100%25' filter='url(%23n)'/></svg>\")",
      }} />

      {/* â”€â”€ Window chrome â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ */}
      <div style={{
        display: "flex", alignItems: "center", justifyContent: "space-between",
        padding: "14px 18px 6px 18px",
        WebkitAppRegion: "drag",
        position: "relative", zIndex: 2,
      }}>
        <div style={{ display: "flex", alignItems: "center", gap: 18, WebkitAppRegion: "no-drag" }}>
          <TrafficLights />
          <div style={{ display: "flex", alignItems: "center", gap: 8, marginLeft: 4 }}>
            <div style={{
              width: 22, height: 22, borderRadius: 6,
              background: `linear-gradient(135deg, ${theme.accentA}, ${theme.accentB})`,
              display: "flex", alignItems: "center", justifyContent: "center",
              boxShadow: `0 0 10px ${theme.accentB}66`,
            }}>
              <svg width="11" height="11" viewBox="0 0 24 24" fill="#fff">
                <path d="M7 4v16l13-8z"/>
              </svg>
            </div>
            <div style={{ fontSize: 12, fontWeight: 600, letterSpacing: ".08em",
                          color: "rgba(255,255,255,.85)" }}>
              JUST PLAY
            </div>
          </div>
        </div>

        {/* Center: album breadcrumb (hidden in compact) */}
        {!t.compact && (
          <div style={{
            position: "absolute", left: "50%", top: 18, transform: "translateX(-50%)",
            display: "flex", alignItems: "center", gap: 12,
            color: "rgba(255,255,255,.7)", fontSize: 12,
            WebkitAppRegion: "no-drag",
          }}>
            <span style={{ fontWeight: 700, letterSpacing: ".12em", color: "#fff" }}>
              {ALBUM.title}
            </span>
            <span style={{ opacity: .5 }}>Â·</span>
            <span>{ALBUM.artist}</span>
            <span style={{ opacity: .5 }}>Â·</span>
            <span style={{ fontVariantNumeric: "tabular-nums" }}>{ALBUM.date}</span>
          </div>
        )}

        <div style={{ display: "flex", alignItems: "center", gap: 4, WebkitAppRegion: "no-drag" }}>
          <GhostButton title="Search" theme={theme}>
            <Icon name="search" size={16} color="currentColor" />
          </GhostButton>
          <GhostButton title={t.compact ? "Expand" : "Mini player"} theme={theme} active={t.compact}
                       onClick={() => setTweak("compact", !t.compact)}>
            <Icon name={t.compact ? "expand" : "mini"} size={16} color="currentColor" />
          </GhostButton>
          <GhostButton title="Settings" theme={theme}>
            <Icon name="settings" size={16} color="currentColor" />
          </GhostButton>
        </div>
      </div>

      {/* â”€â”€ Waveform â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ */}
      {t.waveform && (
        <div style={{ marginTop: -4, marginBottom: -10, opacity: 0.9 }}>
          <Waveform theme={theme} playing={playing}
                    height={t.compact ? 60 : 90}
                    bpm={track.bpm} energy={track.energy} />
        </div>
      )}

      {/* â”€â”€ Main content â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ */}
      {t.compact ? (
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ COMPACT MINI LAYOUT â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        <div style={{
          flex: 1, minHeight: 0,
          display: "flex", flexDirection: "column", alignItems: "center",
          padding: "8px 24px 0 24px",
          position: "relative", zIndex: 1,
        }}>
          <div style={{ display: "flex", justifyContent: "center", flex: 1, alignItems: "center" }}>
            <Vinyl theme={theme} playing={playing && t.vinylSpin} size={260} />
          </div>
          <div style={{ width: "100%", textAlign: "center", padding: "4px 0 0 0" }}>
            <h1 style={{
              margin: 0, fontSize: 20, lineHeight: 1.15, fontWeight: 700,
              letterSpacing: "-0.01em",
              background: `linear-gradient(120deg, ${theme.accentA} 0%, #ffffff 50%, ${theme.accentB} 100%)`,
              WebkitBackgroundClip: "text", backgroundClip: "text",
              WebkitTextFillColor: "transparent",
              whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis",
            }}>
              {track.title}
            </h1>
            <div style={{
              marginTop: 3, fontSize: 12,
              color: "rgba(255,255,255,.65)",
              display: "flex", alignItems: "center", justifyContent: "center", gap: 10,
              flexWrap: "nowrap",
            }}>
              <span style={{ fontWeight: 600, color: "#fff" }}>
                {ALBUM.artist}{track.feat ? ` feat. ${track.feat}` : ""}
              </span>
              <span style={{ opacity: .4 }}>Â·</span>
              <span style={{ fontVariantNumeric: "tabular-nums" }}>{track.bpm} bpm</span>
              <span style={{ opacity: .4 }}>Â·</span>
              <KeyPill keyCode={track.key} active theme={theme} />
            </div>
          </div>
        </div>
      ) : (
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ FULL LAYOUT (2-column) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        <div style={{
          flex: 1, minHeight: 0,
          display: "grid",
          gridTemplateColumns: "minmax(0, 1fr) minmax(0, 1.05fr)",
          gap: 32,
          padding: "12px 44px 0 44px",
          position: "relative", zIndex: 1,
        }}>
          {/* Left: vinyl stage */}
          <div style={{
            display: "flex", flexDirection: "column", alignItems: "center",
            justifyContent: "center", minHeight: 0,
          }}>
            <Vinyl theme={theme} playing={playing && t.vinylSpin} size={360} />
          </div>

          {/* Right: track meta + queue/lyrics */}
          <div style={{
            display: "flex", flexDirection: "column", minHeight: 0, gap: 18,
            paddingTop: 4,
          }}>
            <div>
              <div style={{
                fontSize: 11, letterSpacing: "0.22em", textTransform: "uppercase",
                color: "rgba(255,255,255,.5)", marginBottom: 8, fontWeight: 600,
              }}>
                Now Playing Â· Track {track.n}
              </div>
              <h1 style={{
                margin: 0, fontSize: 32, lineHeight: 1.1, fontWeight: 700,
                letterSpacing: "-0.015em",
                background: `linear-gradient(120deg, ${theme.accentA} 0%, #ffffff 50%, ${theme.accentB} 100%)`,
                WebkitBackgroundClip: "text", backgroundClip: "text",
                WebkitTextFillColor: "transparent",
                textShadow: `0 0 40px ${theme.accentB}33`,
              }}>
                {track.title}
              </h1>
              <div style={{
                marginTop: 8, display: "flex", alignItems: "center", gap: 12,
                fontSize: 15, color: "rgba(255,255,255,.7)",
              }}>
                <CoverArt theme={theme} size={42} />
                <div>
                  <div style={{ fontWeight: 600, color: "#fff" }}>
                    {ALBUM.artist}{track.feat ? ` feat. ${track.feat}` : ""}
                  </div>
                  <div style={{ fontSize: 12, color: "rgba(255,255,255,.55)" }}>
                    {ALBUM.title} Â· {ALBUM.date.slice(0, 4)}
                  </div>
                </div>
              </div>
            </div>

            <div style={{ display: "flex", gap: 4, borderBottom: "1px solid rgba(255,255,255,.08)" }}>
              {[
                { id: "queue", label: "Up Next" },
                { id: "lyrics", label: "Lyrics" },
              ].map((tab) => {
                const active = rightTab === tab.id;
                return (
                  <button
                    key={tab.id}
                    onClick={() => { setRightTab(tab.id); setTweak("rightPanel", tab.id); }}
                    style={{
                      background: "transparent", border: "none",
                      padding: "10px 14px",
                      fontSize: 13, fontWeight: 600,
                      letterSpacing: ".06em", textTransform: "uppercase",
                      color: active ? "#fff" : "rgba(255,255,255,.45)",
                      cursor: "pointer",
                      position: "relative",
                    }}
                  >
                    {tab.label}
                    {active && (
                      <div style={{
                        position: "absolute", left: 14, right: 14, bottom: -1, height: 2,
                        background: `linear-gradient(90deg, ${theme.accentA}, ${theme.accentB})`,
                        borderRadius: 2,
                        boxShadow: `0 0 8px ${theme.accentB}`,
                      }} />
                    )}
                  </button>
                );
              })}
            </div>

            <div style={{ flex: 1, minHeight: 0 }}>
              {rightTab === "queue" && (
                <Queue theme={theme} currentIdx={currentIdx} onSelect={setCurrentIdx} />
              )}
              {rightTab === "lyrics" && (
                <Lyrics theme={theme} current={progress} />
              )}
            </div>
          </div>
        </div>
      )}

      {/* â”€â”€ Progress bar â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ */}
      <div style={{
        padding: t.compact ? "8px 24px 4px 24px" : "12px 48px 6px 48px",
        position: "relative", zIndex: 1,
      }}>
        <ProgressBar theme={theme} current={progress} total={track.dur} onSeek={setProgress} />
      </div>

      {/* â”€â”€ Transport row â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ */}
      <TransportRow
        theme={theme} playing={playing}
        prev={prev} next={next} togglePlay={togglePlay}
        shuffle={shuffle} setShuffle={setShuffle}
        repeatMode={repeatMode} setRepeatMode={setRepeatMode}
        repeatIcon={repeatIcon}
        volume={volume} setVolume={setVolume}
        liked={liked} setLiked={setLiked}
        compact={t.compact}
      />

      {/* â”€â”€ Tweaks panel â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ */}
      <TweaksPanel>
        <TweakSection label="Theme" />
        <TweakRadio label="Palette" value={t.theme}
                    options={["aurora", "sunset", "midnight", "neon"]}
                    onChange={(v) => setTweak("theme", v)} />
        <TweakSection label="Stage" />
        <TweakToggle label="Vinyl spin" value={t.vinylSpin}
                     onChange={(v) => setTweak("vinylSpin", v)} />
        <TweakToggle label="Waveform" value={t.waveform}
                     onChange={(v) => setTweak("waveform", v)} />
        <TweakToggle label="Compact layout" value={t.compact}
                     onChange={(v) => setTweak("compact", v)} />
        <TweakSection label="Right panel" />
        <TweakRadio label="Default tab" value={t.rightPanel}
                    options={["queue", "lyrics"]}
                    onChange={(v) => setTweak("rightPanel", v)} />
      </TweaksPanel>

      <style>{`
        .just-play-scroll::-webkit-scrollbar { width: 8px; }
        .just-play-scroll::-webkit-scrollbar-track { background: transparent; }
        .just-play-scroll::-webkit-scrollbar-thumb {
          background: rgba(255,255,255,.08); border-radius: 4px;
        }
        .just-play-scroll::-webkit-scrollbar-thumb:hover {
          background: rgba(255,255,255,.18);
        }
        @keyframes vinylSpin {
          from { transform: rotate(0deg) }
          to   { transform: rotate(360deg) }
        }
      `}</style>
    </div>
  );
}

ReactDOM.createRoot(document.getElementById("root")).render(<App />);


