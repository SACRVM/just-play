/**
 * oracle.js — Essentia.js key detection oracle
 *
 * Usage: node oracle.js "<audiofile>"
 * Outputs JSON: { key, scale, strength, camelot, profile:"edma" }
 *
 * Decodes audio to 44100 Hz mono float32 PCM using ffmpeg-static.
 * Runs essentia.js KeyExtractor with profile='edma' (primary)
 * and profile='bgate' (secondary, for sensitivity comparison).
 *
 * AGPL note: essentia.js is AGPLv3. This file stays in ml/essentia-check
 * (dev oracle only). Do NOT link into JustPlay product code.
 */

'use strict';

const { execFileSync } = require('child_process');
const { EssentiaWASM, Essentia } = require('essentia.js');
const ffmpeg = require('ffmpeg-static');
const path = require('path');

// ── Camelot lookup table (built manually, unit-verified below) ─────────────
// Key: "Tonic Scale" (e.g. "A minor", "C major"), Value: Camelot code
// Essentia returns key (e.g. "A", "C#") and scale ("major","minor")
// Camelot wheel anchor: A minor = 8A, C major = 8B
// Going clockwise (up a fifth = +1): Am=8A, Em=9A, Bm=10A, F#m=11A,
//   C#m=12A, G#m/Abm=1A, Ebm/D#m=2A, Bbm=3A, Fm=4A, Cm=5A, Gm=6A, Dm=7A
// Major: C=8B, G=9B, D=10B, A=11B, E=12B, B=1B, F#/Gb=2B, Db/C#=3B,
//   Ab/G#=4B, Eb/D#=5B, Bb=6B, F=7B

const CAMELOT = {
  // Minor keys (A)
  'A minor':   '8A',  'E minor':   '9A',  'B minor':  '10A',
  'F# minor': '11A',  'C# minor': '12A',  'G# minor':  '1A',
  'D# minor':  '2A',  'A# minor':  '3A',  'F minor':   '4A',
  'C minor':   '5A',  'G minor':   '6A',  'D minor':   '7A',
  // Enharmonic equivalents (minor)
  'Eb minor':  '2A',  'Bb minor':  '3A',  'Ab minor':  '1A',
  'Db minor': '12A',  'Gb minor': '11A',
  // Major keys (B)
  'C major':   '8B',  'G major':   '9B',  'D major':  '10B',
  'A major':  '11B',  'E major':  '12B',  'B major':   '1B',
  'F# major':  '2B',  'C# major':  '3B',  'G# major':  '4B',
  'D# major':  '5B',  'A# major':  '6B',  'F major':   '7B',
  // Enharmonic equivalents (major)
  'Gb major':  '2B',  'Db major':  '3B',  'Ab major':  '4B',
  'Eb major':  '5B',  'Bb major':  '6B',
};

// Unit verify a few key anchors
function verifyCamelot() {
  const checks = [
    ['A minor', '8A'], ['C major', '8B'], ['E minor', '9A'],
    ['B major', '1B'], ['C# minor', '12A'],
  ];
  for (const [k, expected] of checks) {
    if (CAMELOT[k] !== expected) {
      throw new Error(`Camelot verify FAIL: ${k} → got ${CAMELOT[k]}, expected ${expected}`);
    }
  }
}
verifyCamelot();

function keyToCamelot(key, scale) {
  const lookup = `${key} ${scale}`;
  return CAMELOT[lookup] || null;
}

/**
 * Decode audio to 44100 Hz mono float32 PCM using ffmpeg-static.
 * Returns a Float32Array.
 */
function decodeAudio(filePath) {
  // ffmpeg: decode to raw f32le at 44100 Hz, mono, output to stdout
  const buf = execFileSync(
    ffmpeg,
    [
      '-v', 'quiet',
      '-i', filePath,
      '-f', 'f32le',
      '-ar', '44100',
      '-ac', '1',
      'pipe:1',
    ],
    { maxBuffer: 200 * 1024 * 1024 } // 200 MB — ~36 min at 44100 Hz
  );
  // buf is a Node Buffer; reinterpret as Float32Array
  const ab = buf.buffer.slice(buf.byteOffset, buf.byteOffset + buf.byteLength);
  return new Float32Array(ab);
}

/**
 * Run Essentia KeyExtractor on float32 PCM with a given profile.
 * Returns { key, scale, strength }.
 *
 * Signature (from essentia.js source):
 *   KeyExtractor(audio, averageDetuningCorrection, frameSize, hopSize,
 *                hpcpSize, maxFrequency, maximumSpectralPeaks, minFrequency,
 *                pcpThreshold, profileType, sampleRate, spectralPeaksThreshold,
 *                tuningFrequency, weightType, windowType)
 */
async function runKeyExtractor(essentia, signal, profile) {
  const essentiaSignal = essentia.arrayToVector(signal);
  const result = essentia.KeyExtractor(
    essentiaSignal,  // audio
    true,            // averageDetuningCorrection
    4096,            // frameSize (match edmkey)
    4096,            // hopSize (non-overlapping, match edmkey)
    12,              // hpcpSize
    3500.0,          // maxFrequency (match edmkey)
    60,              // maximumSpectralPeaks (match edmkey)
    25.0,            // minFrequency (match edmkey: 25 Hz)
    0.2,             // pcpThreshold (match edmkey)
    profile,         // profileType ('edma' or 'bgate')
    44100,           // sampleRate
    0.0001,          // spectralPeaksThreshold (match edmkey: 1e-4)
    440.0,           // tuningFrequency (A440)
    'cosine',        // weightType (match edmkey: 'cosine')
    'hann'           // windowType
  );
  // Free WASM memory (essentia.js uses .delete() method on the vector)
  if (essentiaSignal && typeof essentiaSignal.delete === 'function') {
    essentiaSignal.delete();
  }
  return {
    key: result.key,
    scale: result.scale,
    strength: result.strength,
  };
}

async function main() {
  const filePath = process.argv[2];
  if (!filePath) {
    console.error('Usage: node oracle.js "<audiofile>"');
    process.exit(1);
  }

  // Init Essentia — EssentiaWASM is already the instantiated WASM module
  // (essentia.js v0.1.3+: EssentiaWASM is the module object, not a factory function)
  const essentia = new Essentia(EssentiaWASM);

  // Decode
  let signal;
  try {
    signal = decodeAudio(filePath);
  } catch (e) {
    console.error(JSON.stringify({ error: `Decode failed: ${e.message}` }));
    process.exit(2);
  }

  // Run with edma profile (primary)
  let edmaResult, bgateResult;
  try {
    edmaResult = await runKeyExtractor(essentia, signal, 'edma');
  } catch (e) {
    console.error(JSON.stringify({ error: `edma key extraction failed: ${e.message}` }));
    process.exit(3);
  }

  // Run with bgate profile (secondary — Essentia's default, for sensitivity comparison)
  try {
    bgateResult = await runKeyExtractor(essentia, signal, 'bgate');
  } catch (e) {
    bgateResult = { key: null, scale: null, strength: null, error: e.message };
  }

  const edmaCalelot = keyToCamelot(edmaResult.key, edmaResult.scale);
  const bgateCalelot = keyToCamelot(bgateResult.key, bgateResult.scale);

  const out = {
    file: filePath,
    edma: {
      key: edmaResult.key,
      scale: edmaResult.scale,
      strength: edmaResult.strength,
      camelot: edmaCalelot,
    },
    bgate: {
      key: bgateResult.key,
      scale: bgateResult.scale,
      strength: bgateResult.strength,
      camelot: bgateCalelot,
    },
  };

  console.log(JSON.stringify(out));
}

main().catch(e => {
  console.error(JSON.stringify({ error: String(e) }));
  process.exit(99);
});
