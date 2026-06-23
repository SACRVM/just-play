/**
 * camelot.js — Camelot wheel utilities
 *
 * Camelot wheel rules (verified from first principles):
 * - Anchor: A minor = 8A, C major = 8B
 * - Going up a fifth (clockwise): Am(8A) → Em(9A) → Bm(10A) → F#m(11A) → C#m(12A) →
 *   G#m/Abm(1A) → D#m/Ebm(2A) → A#m/Bbm(3A) → Fm(4A) → Cm(5A) → Gm(6A) → Dm(7A)
 * - Relative major for each number: same number, B suffix
 *   e.g. 8A (Am) ↔ 8B (C major) — they share the same notes
 * - Parallel = same tonic, different mode: e.g. Am(8A) ↔ A major(11B) — NOT adjacent
 */

'use strict';

// Map pitch-class + mode to { number, letter } in Camelot
// pitch class: 0=C, 1=C#/Db, 2=D, 3=D#/Eb, 4=E, 5=F, 6=F#/Gb, 7=G, 8=G#/Ab, 9=A, 10=A#/Bb, 11=B
// Verified by: A(9)minor=8A, C(0)major=8B, E(4)minor=9A, B(11)major=1B, C#(1)minor=12A
const MINOR_CAMELOT_NUMBER = [5, 12, 7, 2, 9, 4, 11, 6, 1, 8, 3, 10]; // indexed by pitch class
const MAJOR_CAMELOT_NUMBER = [8, 3, 10, 5, 12, 7, 2, 9, 4, 11, 6, 1]; // indexed by pitch class

// Essentia pitch names to pitch class
const PITCH_CLASS = {
  'C': 0, 'C#': 1, 'Db': 1, 'D': 2, 'D#': 3, 'Eb': 3, 'E': 4,
  'F': 5, 'F#': 6, 'Gb': 6, 'G': 7, 'G#': 8, 'Ab': 8, 'A': 9,
  'A#': 10, 'Bb': 10, 'B': 11,
};

/**
 * Convert Essentia key/scale to Camelot string e.g. "8A"
 * @param {string} key  e.g. "Ab", "C#", "G"
 * @param {string} scale "major" or "minor"
 * @returns {string|null} e.g. "8A" or null on failure
 */
function toCamelot(key, scale) {
  if (!key || !scale) return null;
  const pc = PITCH_CLASS[key];
  if (pc === undefined) return null;
  if (scale === 'minor') return `${MINOR_CAMELOT_NUMBER[pc]}A`;
  if (scale === 'major') return `${MAJOR_CAMELOT_NUMBER[pc]}B`;
  return null;
}

/**
 * Parse a Camelot string (e.g. "8A", "12B") into { number, letter }.
 * Returns null on failure.
 */
function parseCamelot(camelot) {
  if (!camelot) return null;
  const m = /^(\d+)([AB])$/.exec(camelot.trim());
  if (!m) return null;
  return { number: parseInt(m[1], 10), letter: m[2] };
}

/**
 * Classify the relationship between two Camelot codes.
 * @returns {string} one of: exact | fifth | relative | parallel | semitone | tritone | other
 */
function classifyRelationship(camelot1, camelot2) {
  if (!camelot1 || !camelot2) return 'other';
  if (camelot1 === camelot2) return 'exact';

  const c1 = parseCamelot(camelot1);
  const c2 = parseCamelot(camelot2);
  if (!c1 || !c2) return 'other';

  // Relative: same number, different letter (e.g. 8A ↔ 8B)
  if (c1.number === c2.number && c1.letter !== c2.letter) return 'relative';

  // Fifth: same letter, adjacent number (±1, wrapping 1–12)
  if (c1.letter === c2.letter) {
    const diff = ((c2.number - c1.number + 12) % 12);
    if (diff === 1 || diff === 11) return 'fifth';
  }

  // Parallel: same tonic, different mode
  // Parallel minor/major share a tonic; their Camelot codes have:
  // minor: number M, letter A → major: MAJOR_CAMELOT_NUMBER[pc] B
  // We can detect by checking if they share a pitch class
  // Approach: reverse the Camelot → pitch class + mode, then compare pitch class
  const pc1 = camelotToPitchClass(c1);
  const pc2 = camelotToPitchClass(c2);
  if (pc1 !== null && pc2 !== null && pc1 === pc2 && c1.letter !== c2.letter) {
    return 'parallel';
  }

  // Semitone: one pitch class apart (any direction), any mode
  if (pc1 !== null && pc2 !== null) {
    const semDiff = Math.min(
      Math.abs(pc1 - pc2),
      12 - Math.abs(pc1 - pc2)
    );
    if (semDiff === 1) return 'semitone';
    if (semDiff === 6) return 'tritone';
  }

  return 'other';
}

/**
 * Reverse Camelot { number, letter } → pitch class (0–11)
 */
function camelotToPitchClass({ number, letter }) {
  // Inverse lookup of MINOR/MAJOR_CAMELOT_NUMBER
  if (letter === 'A') {
    return MINOR_CAMELOT_NUMBER.indexOf(number);
  } else {
    return MAJOR_CAMELOT_NUMBER.indexOf(number);
  }
}

// ── Unit tests ─────────────────────────────────────────────────────────────
function verify() {
  const tests = [
    // toCamelot tests
    [toCamelot('A', 'minor'),  '8A',  'A minor → 8A'],
    [toCamelot('C', 'major'),  '8B',  'C major → 8B'],
    [toCamelot('E', 'minor'),  '9A',  'E minor → 9A'],
    [toCamelot('B', 'major'),  '1B',  'B major → 1B'],
    [toCamelot('C#', 'minor'), '12A', 'C# minor → 12A'],
    [toCamelot('Ab', 'minor'), '1A',  'Ab minor → 1A'],
    [toCamelot('F#', 'major'), '2B',  'F# major → 2B'],
    // classifyRelationship tests
    [classifyRelationship('8A', '8A'), 'exact',    '8A↔8A = exact'],
    [classifyRelationship('8A', '8B'), 'relative', '8A↔8B = relative (Am↔C)'],
    [classifyRelationship('8A', '9A'), 'fifth',    '8A↔9A = fifth (Am↔Em)'],
    [classifyRelationship('8A', '7A'), 'fifth',    '8A↔7A = fifth (Am↔Dm)'],
    [classifyRelationship('1A', '12A'), 'fifth',   '1A↔12A = fifth (wrap)'],
    // Parallel: Am=8A, A major=11B — same pitch class A, different mode
    [classifyRelationship('8A', '11B'), 'parallel', '8A↔11B = parallel (Am↔Amaj)'],
    // Semitone: 8A(Am) vs adjacent pitch — Bbm = 3A? No. Ab minor=1A, Am=8A
    // C minor = 5A (pc=0), Db minor = 12A (pc=1) → 1 semitone apart, same mode
    [classifyRelationship('5A', '12A'), 'semitone', '5A↔12A = semitone (Cm↔C#m)'],
    // Tritone: C=0, F#=6 → 6 semitones
    [classifyRelationship('8B', '2B'), 'tritone',  '8B↔2B = tritone (C↔F#)'],
  ];
  let pass = 0, fail = 0;
  for (const [got, expected, label] of tests) {
    if (got === expected) {
      pass++;
    } else {
      console.error(`FAIL: ${label} — got ${got}, expected ${expected}`);
      fail++;
    }
  }
  if (fail > 0) throw new Error(`${fail} Camelot unit tests FAILED`);
  // console.log(`Camelot unit tests: ${pass} passed`);
}
verify();

module.exports = { toCamelot, parseCamelot, classifyRelationship, camelotToPitchClass };
