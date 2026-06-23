/**
 * run-compare.js — Run the comparison using pre-built JustPlay index
 *
 * Usage: node run-compare.js --filelist <path> --index <jp-index.json> [--out results.csv]
 *
 * Reads the filelist and JustPlay index, runs Essentia oracle per file,
 * then produces comparison CSV + summary.
 */

'use strict';

const path = require('path');
const fs = require('fs');
const { execFileSync } = require('child_process');
const { EssentiaWASM, Essentia } = require('essentia.js');
const ffmpeg = require('ffmpeg-static');
const { toCamelot, classifyRelationship } = require('./camelot.js');

// ── Arg parsing ──────────────────────────────────────────────────────────────
const args = process.argv.slice(2);
let filelistPath = null;
let jpIndexPath = null;
let outCsv = path.join(__dirname, 'results.csv');
let genre_map_path = null;

for (let i = 0; i < args.length; i++) {
  if (args[i] === '--filelist' && args[i+1]) filelistPath = args[++i];
  else if (args[i] === '--index' && args[i+1]) jpIndexPath = args[++i];
  else if (args[i] === '--out' && args[i+1]) outCsv = args[++i];
  else if (args[i] === '--genre-map' && args[i+1]) genre_map_path = args[++i];
}

if (!filelistPath || !jpIndexPath) {
  console.error('Usage: node run-compare.js --filelist <path> --index <jp-merged.json> [--out results.csv]');
  process.exit(1);
}

// ── Load data ────────────────────────────────────────────────────────────────
const files = fs.readFileSync(filelistPath, 'utf8')
  .split('\n').map(l => l.trim()).filter(l => l && !l.startsWith('#'));

const jpRaw = JSON.parse(fs.readFileSync(jpIndexPath, 'utf8'));
const jpIndex = jpRaw.entries || {};

// Load genre map (optional): maps basename -> genre
const genreMap = {};
if (genre_map_path && fs.existsSync(genre_map_path)) {
  const lines = fs.readFileSync(genre_map_path, 'utf8').split('\n');
  for (const l of lines) {
    const [genre, file] = l.split('\t');
    if (genre && file) genreMap[path.basename(file.trim())] = genre.trim();
  }
}

// Build genre map from filelist paths (genre = parent folder name)
for (const fp of files) {
  const parts = fp.replace(/\\/g, '/').split('/');
  const basename = parts[parts.length - 1];
  const parent = parts[parts.length - 2];
  if (!genreMap[basename]) genreMap[basename] = parent;
}

console.log(`Files: ${files.length}`);
console.log(`JustPlay index entries: ${Object.keys(jpIndex).length}`);

// ── Essentia setup ───────────────────────────────────────────────────────────
function decodeAudio(filePath) {
  // Normalize path separators for ffmpeg
  const fp = filePath.replace(/\//g, '\\');
  const buf = execFileSync(
    ffmpeg,
    ['-v', 'quiet', '-i', fp, '-f', 'f32le', '-ar', '44100', '-ac', '1', 'pipe:1'],
    { maxBuffer: 200 * 1024 * 1024 }
  );
  const ab = buf.buffer.slice(buf.byteOffset, buf.byteOffset + buf.byteLength);
  return new Float32Array(ab);
}

function runKeyExtractor(essentia, signal, profile) {
  const vec = essentia.arrayToVector(signal);
  try {
    const r = essentia.KeyExtractor(
      vec, true, 4096, 4096, 12, 3500.0, 60, 25.0, 0.2,
      profile, 44100, 0.0001, 440.0, 'cosine', 'hann'
    );
    return { key: r.key, scale: r.scale, strength: r.strength };
  } finally {
    if (vec && typeof vec.delete === 'function') vec.delete();
  }
}

// ── Main ─────────────────────────────────────────────────────────────────────
async function main() {
  const essentia = new Essentia(EssentiaWASM);
  console.log('Essentia initialized.');

  const rows = [];
  let essentiaErrors = 0;
  let jpMissed = 0;

  for (let i = 0; i < files.length; i++) {
    const filePath = files[i];
    const basename = path.basename(filePath);
    const genre = genreMap[basename] || '';

    process.stdout.write(`\r[${i+1}/${files.length}] ${basename.slice(0, 55).padEnd(55)}`);

    // JustPlay result: index keys use Windows backslashes; normalize for lookup
    const fpNorm = filePath.replace(/\//g, '\\');
    // Try multiple key forms
    const jpKey = Object.keys(jpIndex).find(k => {
      const kn = k.replace(/\\/g, '/');
      const fn = filePath.replace(/\\/g, '/');
      return kn === fn || kn.endsWith('/' + basename) || k.endsWith('\\' + basename);
    });
    const jpEntry = jpKey ? jpIndex[jpKey] : null;

    let ourCamelot = null, ourConf = null, jpSuccess = false;
    if (jpEntry && jpEntry.success) {
      ourCamelot = jpEntry.keyCamelot || null;
      ourConf    = jpEntry.keyConfidence != null ? Number(jpEntry.keyConfidence).toFixed(4) : null;
      jpSuccess  = true;
    } else {
      jpMissed++;
    }

    // Essentia result
    let esEdmaCamelot = null, esEdmaStrength = null;
    let esBgateCamelot = null, esBgateStrength = null;
    let esError = null;

    try {
      const signal = decodeAudio(filePath);
      const edma  = runKeyExtractor(essentia, signal, 'edma');
      const bgate = runKeyExtractor(essentia, signal, 'bgate');
      esEdmaCamelot   = toCamelot(edma.key,   edma.scale);
      esEdmaStrength  = edma.strength  != null ? Number(edma.strength).toFixed(4)  : null;
      esBgateCamelot  = toCamelot(bgate.key,  bgate.scale);
      esBgateStrength = bgate.strength != null ? Number(bgate.strength).toFixed(4) : null;
    } catch(e) {
      esError = e.message.slice(0, 80);
      essentiaErrors++;
    }

    const relationship = (ourCamelot && esEdmaCamelot)
      ? classifyRelationship(ourCamelot, esEdmaCamelot)
      : 'n/a';
    const agree = relationship === 'exact' ? 'yes' : 'no';

    rows.push({
      file: filePath,
      basename,
      genre,
      ourKeyCamelot:        ourCamelot    || '',
      ourConf:              ourConf       || '',
      essentiaEdma:         esEdmaCamelot || (esError ? `ERR:${esError}` : ''),
      essentiaEdmaStrength: esEdmaStrength|| '',
      essentiaBgate:        esBgateCamelot|| '',
      essentiaBgateStrength:esBgateStrength||'',
      relationship,
      agree,
    });
  }
  process.stdout.write('\n');

  // ── Write CSV ───────────────────────────────────────────────────────────────
  const header = 'file,genre,ourKeyCamelot,ourConf,essentiaEdma,essentiaEdmaStrength,essentiaBgate,essentiaBgateStrength,relationship,agree';
  const csvRows = rows.map(r => [
    `"${r.file.replace(/"/g,'""')}"`,
    r.genre,
    r.ourKeyCamelot,
    r.ourConf,
    r.essentiaEdma,
    r.essentiaEdmaStrength,
    r.essentiaBgate,
    r.essentiaBgateStrength,
    r.relationship,
    r.agree,
  ].join(','));
  fs.writeFileSync(outCsv, [header, ...csvRows].join('\n'), 'utf8');
  console.log(`\nResults CSV: ${outCsv}`);

  // ── Summary ─────────────────────────────────────────────────────────────────
  const valid = rows.filter(r => r.ourKeyCamelot && r.essentiaEdma && !r.essentiaEdma.startsWith('ERR'));
  const total = valid.length;
  const byCat = {};
  for (const r of valid) byCat[r.relationship] = (byCat[r.relationship]||0) + 1;

  const pct = n => total > 0 ? `${(n/total*100).toFixed(1)}%` : 'n/a';
  const exact    = byCat['exact']    || 0;
  const fifth    = byCat['fifth']    || 0;
  const relative = byCat['relative'] || 0;
  const parallel = byCat['parallel'] || 0;
  const semitone = byCat['semitone'] || 0;
  const tritone  = byCat['tritone']  || 0;
  const other    = byCat['other']    || 0;
  const harmOk   = exact + fifth + relative + parallel;

  console.log('\n' + '='.repeat(65));
  console.log('SUMMARY: JustPlay vs Essentia/edma on real DJ-pool tracks');
  console.log('='.repeat(65));
  console.log(`Total files:              ${files.length}`);
  console.log(`JustPlay missing:         ${jpMissed}`);
  console.log(`Essentia errors:          ${essentiaErrors}`);
  console.log(`Valid comparisons:        ${total}`);
  console.log('');
  console.log(`Exact agreement:          ${exact}/${total} = ${pct(exact)}`);
  console.log(`Harmonically ok (tot):    ${harmOk}/${total} = ${pct(harmOk)}`);
  console.log('');
  console.log('Relationship breakdown:');
  console.log(`  exact:    ${exact.toString().padStart(3)} (${pct(exact)})`);
  console.log(`  fifth:    ${fifth.toString().padStart(3)} (${pct(fifth)})`);
  console.log(`  relative: ${relative.toString().padStart(3)} (${pct(relative)})`);
  console.log(`  parallel: ${parallel.toString().padStart(3)} (${pct(parallel)})`);
  console.log(`  semitone: ${semitone.toString().padStart(3)} (${pct(semitone)})`);
  console.log(`  tritone:  ${tritone.toString().padStart(3)} (${pct(tritone)})`);
  console.log(`  other:    ${other.toString().padStart(3)} (${pct(other)})`);
  console.log('');

  // Profile sensitivity
  const bgateDisagree = valid.filter(r => r.essentiaBgate && r.essentiaBgate !== r.essentiaEdma).length;
  console.log(`Essentia edma!=bgate:     ${bgateDisagree}/${valid.filter(r=>r.essentiaBgate).length}`);

  // bgate vs ours (to see if bgate is closer)
  const bgateValid = rows.filter(r => r.ourKeyCamelot && r.essentiaBgate && !r.essentiaBgate.startsWith('ERR'));
  const bgateExact = bgateValid.filter(r => classifyRelationship(r.ourKeyCamelot, r.essentiaBgate) === 'exact').length;
  console.log(`JP vs bgate exact:        ${bgateExact}/${bgateValid.length} = ${bgateValid.length>0?(bgateExact/bgateValid.length*100).toFixed(1)+'%':'n/a'}`);
  console.log('');

  // Worst divergences
  const worst = valid
    .filter(r => r.relationship === 'other' || r.relationship === 'tritone' || r.relationship === 'semitone')
    .sort((a,b) => {
      const o = {other:0,tritone:1,semitone:2};
      return (o[a.relationship]||9)-(o[b.relationship]||9);
    });

  if (worst.length > 0) {
    console.log('WORST DIVERGENCES (unrelated/tritone/semitone):');
    console.log('-'.repeat(65));
    for (const r of worst.slice(0, 15)) {
      console.log(`  [${r.relationship.padEnd(8)}] JP=${(r.ourKeyCamelot||'?').padEnd(4)} ES=${(r.essentiaEdma||'?').padEnd(4)} bgate=${(r.essentiaBgate||'?').padEnd(4)} conf=${r.ourConf||'?'} str=${r.essentiaEdmaStrength||'?'}`);
      console.log(`    [${r.genre}] ${r.basename.slice(0,60)}`);
    }
    console.log('');
  }

  // Forgivable
  const forgiv = valid.filter(r => r.relationship === 'fifth' || r.relationship === 'relative');
  if (forgiv.length > 0) {
    console.log('FORGIVABLE DISAGREEMENTS (fifth/relative):');
    console.log('-'.repeat(65));
    for (const r of forgiv) {
      console.log(`  [${r.relationship.padEnd(8)}] JP=${(r.ourKeyCamelot||'?').padEnd(4)} ES=${(r.essentiaEdma||'?').padEnd(4)} bgate=${(r.essentiaBgate||'?').padEnd(4)} conf=${r.ourConf||'?'} str=${r.essentiaEdmaStrength||'?'}`);
      console.log(`    [${r.genre}] ${r.basename.slice(0,60)}`);
    }
    console.log('');
  }

  // Per-genre breakdown
  const genreStats = {};
  for (const r of valid) {
    if (!genreStats[r.genre]) genreStats[r.genre] = { total: 0, exact: 0 };
    genreStats[r.genre].total++;
    if (r.relationship === 'exact') genreStats[r.genre].exact++;
  }
  console.log('PER-GENRE EXACT AGREEMENT:');
  for (const [g, s] of Object.entries(genreStats).sort((a,b) => a[0].localeCompare(b[0]))) {
    const pctG = (s.exact/s.total*100).toFixed(0);
    console.log(`  ${g.padEnd(15)}: ${s.exact}/${s.total} (${pctG}%)`);
  }

  console.log('\n[done]');
}

main().catch(e => {
  console.error('Fatal:', e);
  process.exit(99);
});
