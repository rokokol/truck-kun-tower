// Draws the favicon set from the lockup in docs/logo.png: wwwroot/favicon.ico at 16, 32 and 48,
// wwwroot/icon-192.png, wwwroot/icon-512.png and wwwroot/apple-touch-icon.png. Needs ImageMagick
// (`magick`) built with librsvg. Nothing here reaches the network or a browser.
//
//   node tools/icons.mjs          draw the set, failing on a moved mark or a crowded small icon
//
// The PNG is the artwork and docs/logo.svg is the same lockup as a vector. A 71x119 mark cannot
// fill a 512 icon, so the set comes from the vector at four times the lockup, and every run
// proves the two agree: the vector rendered at the PNG's own size must match the PNG's mark.
// The mark's ink box is measured from the PNG, so a redrawn logo draws itself and a logo whose
// mark moved fails instead of shipping a crop of the wrong part.

import { spawnSync } from 'node:child_process';
import { copyFileSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
const png = join(root, 'docs/logo.png');
const vector = join(root, 'docs/logo.svg');
const out = join(root, 'wwwroot');

// The mark fills 7/8 of an icon, which leaves one clear pixel of air each way at 16 px
const size = 512;
const mark = Math.round(size * 7 / 8);
const similar = 0.03;

const fail = message => {
  console.error(`icons: ${message}`);
  process.exit(1);
};

const magick = (args, options = {}) => {
  const run = spawnSync('magick', args, { encoding: 'utf8', ...options });
  if (run.error) fail(`cannot run magick: ${run.error.message}`);
  return run;
};

const value = (args, label) => {
  const run = magick(args);
  if (run.status !== 0) fail(`${label}: ${run.stderr.trim()}`);
  return run.stdout.trim();
};

const box = text => {
  const found = /^(\d+)x(\d+)\+(\d+)\+(\d+)$/.exec(text.replace(/ /g, ''));
  if (!found) fail(`unreadable box: ${text}`);
  return found.slice(1).map(Number);
};

const wh = text => {
  const found = text.replace(/ /g, '').split('x').map(Number);
  if (found.length !== 2 || found.some(n => !Number.isFinite(n))) fail(`unreadable size: ${text}`);
  return found;
};

const scratch = mkdtempSync(join(tmpdir(), 'tower-icons-'));
const at = name => join(scratch, name);

if (magick(['-version']).status !== 0) fail('ImageMagick (magick) is needed to draw the set');

// The mark is the largest ink object of the lockup; the wordmark's letters are smaller ones
const objects = value([png, '-alpha', 'extract', '-threshold', '5%', '-define',
  'connected-components:verbose=true', '-connected-components', '8', 'null:'], 'reading the lockup');
const ink = objects.split('\n')
  .map(line => /^\s*\d+:\s+(\d+x\d+\+\d+\+\d+)\s+[\d.-]+,[\d.-]+\s+(\d+)\s+srgb\(255,255,255\)/.exec(line))
  .filter(Boolean)
  .map(([, geometry, area]) => ({ box: box(geometry), area: Number(area) }))
  .sort((a, b) => b.area - a.area);
if (!ink.length) fail(`${png} holds no ink`);
const [markWidth, markHeight, markX, markY] = ink[0].box;

// A box that still frames the mark touches its ink on all four sides; the edge rows say so
const edges = {
  left: `${1}x${markHeight}+${markX}+${markY}`,
  right: `${1}x${markHeight}+${markX + markWidth - 1}+${markY}`,
  top: `${markWidth}x1+${markX}+${markY}`,
  bottom: `${markWidth}x1+${markX}+${markY + markHeight - 1}`,
};
const empty = Object.entries(edges)
  .filter(([, crop]) => Number(value([png, '-crop', crop, '+repage', '-alpha', 'extract', '-format',
    '%[fx:maxima*255]', 'info:'], 'reading the mark')) < 8)
  .map(([edge]) => edge);
if (empty.length) fail(`the mark has no ink on the ${empty.join(' and ')} edge of its box`);

const [lockupWidth] = wh(value([png, '-format', '%w x %h', 'info:'], 'reading the lockup'));
value(['-background', 'none', '-density', '384', vector, at('vector@4x.png')], 'drawing the vector');
const rendered = wh(value([at('vector@4x.png'), '-format', '%w x %h', 'info:'], 'reading the vector'));
if (rendered[0] !== lockupWidth * 4) fail(`${vector} and ${png} disagree on the lockup's width`);

// The same mark out of the vector at four times the lockup, checked against the PNG at one time
value([at('vector@4x.png'), '-crop', `${markWidth * 4}x${markHeight * 4}+${markX * 4}+${markY * 4}`,
  '+repage', at('mark@4x.png')], 'cropping the vector');
value([png, '-crop', `${markWidth}x${markHeight}+${markX}+${markY}`, '+repage', at('mark.png')], 'cropping the PNG');
value([at('mark@4x.png'), '-filter', 'Lanczos', '-resize', `${markWidth}x${markHeight}`, '-depth', '8',
  at('mark@4x-small.png')], 'shrinking the vector mark');
const compared = magick(['compare', '-metric', 'RMSE', at('mark.png'), at('mark@4x-small.png'), 'null:']);
const distance = compared.stderr.trim().split(' ')[0].replace(/[()]/g, '');
if (!Number.isFinite(Number(distance))) fail(`the PNG and the vector cannot be compared: ${compared.stderr.trim()}`);
if (Number(distance) > similar * 65535) fail(`${vector} and ${png} are different artwork: ${(Number(distance) / 65535 * 100).toFixed(1)}% apart`);

value([at('mark@4x.png'), '-filter', 'Lanczos', '-resize', `x${mark}`, '-background', 'none', '-gravity',
  'center', '-extent', `${size}x${size}`, '-depth', '8', at('master.png')], 'drawing the master');
copyFileSync(at('master.png'), join(out, 'icon-512.png'));
for (const [side, name] of [[192, 'icon-192.png'], [180, 'apple-touch-icon.png']]) {
  value([at('master.png'), '-filter', 'Lanczos', '-resize', `${side}x${side}`, '-depth', '8', join(out, name)], name);
}

// A mark this small loses its outline to the downscale alone, and a light sharpen brings it back
const frames = [];
for (const side of [16, 32, 48]) {
  const name = at(`${side}.png`);
  value([at('master.png'), '-filter', 'Lanczos', '-resize', `${side}x${side}`, '-unsharp', '0x0.4+0.3+0',
    '-depth', '8', name], `${side}px frame`);
  frames.push({ side, name });
}

// ImageMagick writes each frame as a bitmap with a one-bit mask, and a reader that applies the mask
// flattens the partial alpha to specks. The ICO is assembled here instead, with each PNG frame
// embedded unchanged, which every browser since Vista reads.
const entries = [...frames].reverse().map(({ side, name }) => ({ side, bytes: readFileSync(name) }));
const head = Buffer.alloc(6 + 16 * entries.length);
head.writeUInt16LE(1, 2);
head.writeUInt16LE(entries.length, 4);
let offset = head.length;
entries.forEach(({ side, bytes }, place) => {
  const entry = 6 + 16 * place;
  head.writeUInt8(side % 256, entry);
  head.writeUInt8(side % 256, entry + 1);
  head.writeUInt16LE(1, entry + 4);
  head.writeUInt16LE(32, entry + 6);
  head.writeUInt32LE(bytes.length, entry + 8);
  head.writeUInt32LE(offset, entry + 12);
  offset += bytes.length;
});
writeFileSync(join(out, 'favicon.ico'), Buffer.concat([head, ...entries.map(({ bytes }) => bytes)]));

// The frame has to hold its own ink: a smaller margin is a mark a tab bar clips
const [smallWidth, smallHeight, smallX, smallY] = box(value([at('16.png'), '-alpha', 'extract', '-threshold',
  '5%', '-format', '%@', 'info:'], 'reading the 16px frame'));
const air = Math.min(smallX, smallY, 16 - smallX - smallWidth, 16 - smallY - smallHeight);
if (air < 1) fail(`the 16px mark keeps ${air}px of air, so the pad is too small`);

// A frame that reads back different from the file it was made of lost its alpha on the way
const reread = magick(['compare', '-metric', 'RMSE', at('16.png'), `${join(out, 'favicon.ico')}[2]`, 'null:']);
const drift = reread.stderr.trim().split(' ')[0].replace(/[()]/g, '');
if (!(Number(drift) < 0.01 * 65535)) fail(`favicon.ico gives back a 16px frame ${(Number(drift) / 65535 * 100).toFixed(1)}% away from the one drawn`);

const written = new Map([['favicon.ico', [48, 32, 16]], ['icon-192.png', [192]], ['icon-512.png', [512]],
  ['apple-touch-icon.png', [180]]]);
for (const [name, widths] of written) {
  const got = value([join(out, name), '-format', '%w\n', 'info:'], name).split('\n').filter(Boolean).map(Number);
  if (got.join(',') !== widths.join(',')) fail(`${name} holds ${got.join(', ')} instead of ${widths.join(', ')}`);
}

console.log(`icons: ${[...written.keys()].join(', ')}, mark ${mark} of ${size} with ${air}px of air at 16, scratch ${scratch}`);
