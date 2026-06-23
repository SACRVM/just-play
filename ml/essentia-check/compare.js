/**
 * compare.js — JustPlay ↔ Essentia key detection comparison harness
 *
 * Usage:
 *   node compare.js "<folder>"              -- analyze all audio in folder
 *   node compare.js --filelist filelist.txt -- analyze files listed one per line
 *   node compare.js "<folder>" --out results.csv
 *
 * Strategy:
 *   1. Batch-analyze the folder with `justplay analyze` → sidecar index (fast, parallel)
 *   2. Per-file: run Essentia oracle (edma + bgate) via oracle.js
 *   3. Compare Camelot codes, classify relationship, emit CSV + summary
 *
 * Output:
 *   results.csv — one row per track
 *   Summary printed to stdout
 *
 * AGPL note: essentia.js is AGPLv3. This script is a dev oracle only.
 * Do NOT link into JustPlay product code.
 */

'use strict';

const path = require('path');
const fs = require('fs');
const { execFileSync, spawnSync } = require('child_process');
const { EssentiaWASM, Essentia } = require('essentia.js');
const ffmpeg = require('ffmpeg-static');
const { toCamelot, classifyRelationship } = require('./camelot.js');

// ── Config ─────────────────────────────────────────────────────────────────
const REPO_ROOT = path.resolve(__dirname, '..', '..');
const CLI_PROJECT = path.join(REPO_ROOT, 'src', 'JustPlay.Cli');
const DOTNET = 'dotnet';

// ── CLI argument parsing ────────────────────────────────────────────────────
const args = process.argv.slice(2);
let folderArg = null;
let filelistArg = null;
let outCsv = path.join(__dirname, 'results.csv');
let skipJustPlay = false;
let justplayCacheIndex = null;

for (let i = 0; i < args.length; i++) {
  if (args[i] === '--filelist' && args[i+1]) { filelistArg = args[++i]; }
  else if (args[i] === '--out' && args[i+1]) { outCsv = args[++i]; }
  else if (args[i] === '--skip-justplay') { skipJustPlay = true; }
  else if (args[i] === '--index' && args[i+1]) { justplayCacheIndex = args[++i]; }
  else if (!folderArg && !args[i].startsWith('--')) { folderArg = args[i]; }
}

if (!folderArg && !filelistArg) {
  console.error('Usage: node compare.js "<folder>" [--filelist file.txt] [--out results.csv] [--index existing.json]');
  process.exit(1);
}

// ── Audio file enumeration ──────────────────────────────────────────────────
const AUDIO_EXTS = new Set(['.mp3', '.flac', '.aiff', '.aif', '.wav', '.ogg', '.m4a']);

function enumerateFiles(folder) {
  const results = [];
  for (const entry of fs.readdirSync(folder, { withFileTypes: true })) {
    const full = path.join(folder, entry.name);
    if (entry.isDirectory()) results.push(...enumerateFiles(full));
    else if (AUDIO_EXTS.has(path.extname(entry.name).toLowerCase())) results.push(full);
  }
  return results.sort();
}

// ── JustPlay batch analysis ─────────────────────────────────────────────────
function runJustPlayAnalyze(folder, indexPath) {
  console.log(`\n[compare] Running JustPlay analyze on: ${folder}`);
  console.log(`[compare] Index: ${indexPath}`);

  const result = spawnSync(
    DOTNET,
    ['run', '--project', CLI_PROJECT, '--no-build', '--',
     'analyze', folder, '--index', indexPath, '--threads', '4'],
    { stdio: 'inherit', timeout: 600000 }
  );

  if (result.error) {
    console.error('[compare] JustPlay analyze error:', result.error.message);
    return false;
  }
  // Exit code 255 is a known BASS teardown crash — data is valid
  if (result.status !== 0 && result.status !== 255) {
    console.error(`[compare] JustPlay analyze exited with code ${result.status}`);
    return false;
  }
  return true;
}

function loadJustPlayIndex(indexPath) {
  if (!fs.existsSync(indexPath)) return {};
  const raw = JSON.parse(fs.readFileSync(indexPath, 'utf8'));
  return raw.entries || {};
}

// ── Essentia oracle (per-file) ──────────────────────────────────────────────
function decodeAudio(filePath) {
  const buf = execFileSync(
    ffmpeg,
    ['-v', 'quiet', '-i', filePath, '-f', 'f32le', '-ar', '44100', '-ac', '1', 'pipe:1'],
    { maxBuffer: 200 * 1024 * 1024 }
  );
  const ab = buf.buffer.slice(buf.byteOffset, buf.byteOffset + buf.byteLength);
  return new Float32Array(ab);
}

function runKeyExtractor(essentia, signal, profile) {
  const vec = essentia.arrayToVector(signal);
  try {
    const r = essentia.KeyExtractor(
      vec,
      true,   // averageDetuningCorrection
      4096,   // frameSize
      4096,   // hopSize
      12,     // hpcpSize
      3500.0, // maxFrequency
      60,     // maximumSpectralPeaks
      25.0,   // minFrequency
      0.2,    // pcpThreshold
      profile,// profileType
      44100,  // sampleRate
      0.0001, // spectralPeaksThreshold
      440.0,  // tuningFrequency
      'cosine',// weightType
      'hann'  // windowType
    );
    return { key: r.key, scale: r.scale, strength: r.strength };
  } finally {
    if (vec && typeof vec.delete === 'function') vec.delete();
  }
}

function runEssentiaOnFile(essentia, filePath) {
  try {
    const signal = decodeAudio(filePath);
    const edma  = runKeyExtractor(essentia, signal, 'edma');
    const bgate = runKeyExtractor(essentia, signal, 'bgate');
    return {
      edma:  { ...edma,  camelot: toCamelot(edma.key,  edma.scale)  },
      bgate: { ...bgate, camelot: toCamelot(bgate.key, bgate.scale) },
    };
  } catch (e) {
    return { error: e.message };
  }
}

// ── Camelot lookup helpers ─────────────────────────────────────────────────
// JustPlay index stores keyCamelot already e.g. "8A"
// Also stores keyName e.g. "A minor" but we use keyCamelot directly

// ── Main ───────────────────────────────────────────────────────────────────
async function main() {
  // 1. Determine file list
  let files;
  if (filelistArg) {
    files = fs.readFileSync(filelistArg, 'utf8')
      .split('\n').map(l => l.trim()).filter(Boolean)
      .filter(l => !l.startsWith('#'));
  } else {
    files = enumerateFiles(folderArg);
  }

  if (files.length === 0) {
    console.error('[compare] No audio files found.');
    process.exit(1);
  }
  console.log(`[compare] Files to compare: ${files.length}`);

  // 2. Run JustPlay batch analyze (unless we have a pre-built index)
  const tmpIndex = justplayCacheIndex ||
    path.join(__dirname, `_justplay_index_${Date.now()}.json`);
  const usePrebuiltIndex = !!justplayCacheIndex && fs.existsSync(justplayCacheIndex);

  if (!usePrebuiltIndex && !skipJustPlay) {
    // JustPlay analyze needs a root folder; if we have a filelist spanning multiple
    // folders, we need to find the common ancestor, or run per-genre folder.
    // Simple approach: if folderArg given, use it; else derive from filelist.
    const analyzeRoot = folderArg || path.dirname(files[0]);
    const ok = runJustPlayAnalyze(analyzeRoot, tmpIndex);
    if (!ok) {
      console.error('[compare] JustPlay analysis failed. Aborting.');
      process.exit(1);
    }
  }

  const jpIndex = loadJustPlayIndex(tmpIndex);
  console.log(`[compare] JustPlay index loaded: ${Object.keys(jpIndex).length} entries`);

  // 3. Init Essentia
  console.log('[compare] Initializing Essentia...');
  const essentia = new Essentia(EssentiaWASM);

  // 4. Per-file comparison
  const rows = [];
  let essentiaErrors = 0;
  let jpMissed = 0;

  for (let i = 0; i < files.length; i++) {
    const filePath = files[i];
    const basename = path.basename(filePath);
    process.stdout.write(`\r[compare] ${i+1}/${files.length} ${basename.slice(0, 50).padEnd(50)}`);

    // JustPlay result — look up by path (index uses backslash-form on Windows)
    const jpKey = Object.keys(jpIndex).find(k =>
      k.replace(/\\/g, '/') === filePath.replace(/\\/g, '/')
    );
    const jpEntry = jpKey ? jpIndex[jpKey] : null;

    let ourCamelot = null, ourConf = null;
    if (jpEntry && jpEntry.success) {
      ourCamelot = jpEntry.keyCamelot || null;
      ourConf    = jpEntry.keyConfidence != null ? jpEntry.keyConfidence.toFixed(4) : null;
    } else {
      jpMissed++;
    }

    // Essentia result
    const esResult = runEssentiaOnFile(essentia, filePath);
    let esEdmaCamelot = null, esEdmaStrength = null;
    let esBgateCamelot = null, esBgateStrength = null;

    if (esResult.error) {
      essentiaErrors++;
    } else {
      esEdmaCamelot   = esResult.edma.camelot;
      esEdmaStrength  = esResult.edma.strength != null ? esResult.edma.strength.toFixed(4) : null;
      esBgateCamelot  = esResult.bgate.camelot;
      esBgateStrength = esResult.bgate.strength != null ? esResult.bgate.strength.toFixed(4) : null;
    }

    const relationship = classifyRelationship(ourCamelot, esEdmaCamelot);
    const agree = relationship === 'exact' ? 'yes' : 'no';

    rows.push({
      file: filePath,
      ourKeyCamelot: ourCamelot || '',
      ourConf: ourConf || '',
      essentiaEdma: esEdmaCamelot || (esResult.error ? `ERROR: ${esResult.error.slice(0, 60)}` : ''),
      essentiaEdmaStrength: esEdmaStrength || '',
      essentiaBgate: esBgateCamelot || '',
      essentiaBgateStrength: esBgateStrength || '',
      relationship,
      agree,
    });
  }
  process.stdout.write('\n');

  // 5. Write CSV
  const csvHeader = [
    'file', 'ourKeyCamelot', 'ourConf',
    'essentiaEdma', 'essentiaEdmaStrength',
    'essentiaBgate', 'essentiaBgateStrength',
    'relationship', 'agree',
  ].join(',');

  const csvRows = rows.map(r => [
    `"${r.file.replace(/"/g, '""')}"`,
    r.ourKeyCamelot,
    r.ourConf,
    r.essentiaEdma,
    r.essentiaEdmaStrength,
    r.essentiaBgate,
    r.essentiaBgateStrength,
    r.relationship,
    r.agree,
  ].join(','));

  fs.writeFileSync(outCsv, [csvHeader, ...csvRows].join('\n'), 'utf8');
  console.log(`\n[compare] Results written to: ${outCsv}`);

  // 6. Summary
  const validRows = rows.filter(r => r.ourKeyCamelot && r.essentiaEdma && !r.essentiaEdma.startsWith('ERROR'));
  const total = validRows.length;
  const byCat = {};
  for (const r of validRows) {
    byCat[r.relationship] = (byCat[r.relationship] || 0) + 1;
  }

  console.log('\n' + '='.repeat(60));
  console.log('SUMMARY — JustPlay vs Essentia (edma profile)');
  console.log('='.repeat(60));
  console.log(`Total files:                    ${files.length}`);
  console.log(`JustPlay results missing:       ${jpMissed}`);
  console.log(`Essentia errors:                ${essentiaErrors}`);
  console.log(`Valid comparisons:              ${total}`);
  console.log('');

  const pct = n => total > 0 ? `${(n/total*100).toFixed(1)}%` : 'n/a';

  const exact    = byCat['exact']    || 0;
  const fifth    = byCat['fifth']    || 0;
  const relative = byCat['relative'] || 0;
  const parallel = byCat['parallel'] || 0;
  const semitone = byCat['semitone'] || 0;
  const tritone  = byCat['tritone']  || 0;
  const other    = byCat['other']    || 0;

  const harmOk = exact + fifth + relative + parallel;
  const hardFail = semitone + tritone + other;

  console.log(`Agreement (exact match):        ${exact} / ${total} = ${pct(exact)}`);
  console.log(`Harmonically ok (±1 or rel/par):${harmOk} / ${total} = ${pct(harmOk)}`);
  console.log('');
  console.log('Disagreement breakdown:');
  console.log(`  exact:    ${exact} (${pct(exact)})`);
  console.log(`  fifth:    ${fifth} (${pct(fifth)})`);
  console.log(`  relative: ${relative} (${pct(relative)})`);
  console.log(`  parallel: ${parallel} (${pct(parallel)})`);
  console.log(`  semitone: ${semitone} (${pct(semitone)})`);
  console.log(`  tritone:  ${tritone} (${pct(tritone)})`);
  console.log(`  other:    ${other} (${pct(other)})`);
  console.log('');

  // bgate vs edma agreement
  const bgateVsEdma = rows.filter(r => r.essentiaBgate && r.essentiaEdma &&
    !r.essentiaEdma.startsWith('ERROR') && r.essentiaBgate !== r.essentiaEdma);
  console.log(`Essentia edma≠bgate (profile sensitivity): ${bgateVsEdma.length} / ${rows.filter(r=>r.essentiaBgate).length}`);
  console.log('');

  // Top divergences: other + tritone + semitone (worst cases)
  const worst = validRows
    .filter(r => r.relationship === 'other' || r.relationship === 'tritone' || r.relationship === 'semitone')
    .sort((a, b) => {
      const order = { other: 0, tritone: 1, semitone: 2 };
      return (order[a.relationship] || 9) - (order[b.relationship] || 9);
    })
    .slice(0, 15);

  if (worst.length > 0) {
    console.log('TOP DIVERGENCES (unrelated / tritone / semitone):');
    console.log('-'.repeat(60));
    for (const r of worst) {
      const shortFile = path.basename(r.file).slice(0, 55);
      console.log(`  [${r.relationship.padEnd(8)}] JP=${r.ourCamelot||r.ourKeyCamelot||'?'} vs ES=${r.essentiaEdma||'?'} | conf=${r.ourConf||'?'} | esStr=${r.essentiaEdmaStrength||'?'}`);
      console.log(`          ${shortFile}`);
    }
    console.log('');
  }

  // Fifth / relative breakdown (forgivable but worth noting)
  const forgivable = validRows.filter(r => r.relationship === 'fifth' || r.relationship === 'relative');
  if (forgivable.length > 0) {
    console.log('FORGIVABLE DISAGREEMENTS (fifth / relative):');
    console.log('-'.repeat(60));
    for (const r of forgivable) {
      const shortFile = path.basename(r.file).slice(0, 55);
      console.log(`  [${r.relationship.padEnd(8)}] JP=${r.ourCamelot||r.ourKeyCamelot||'?'} vs ES=${r.essentiaEdma||'?'} | conf=${r.ourConf||'?'} | esStr=${r.essentiaEdmaStrength||'?'}`);
      console.log(`          ${shortFile}`);
    }
  }

  console.log('\n[compare] Done.');

  // Clean up temp index if we created it
  if (!justplayCacheIndex && !skipJustPlay && fs.existsSync(tmpIndex)) {
    // Keep it for re-runs — don't delete
    console.log(`[compare] JustPlay index kept at: ${tmpIndex}`);
  }
}

main().catch(e => {
  console.error('[compare] Fatal error:', e);
  process.exit(99);
});
