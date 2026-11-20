// Draws the promo pictures from docs/truck-kun-tower.jpg: the 640×360 banner the bot profile and
// the project page show, and the 1200×630 link preview the page names in og:image. One artwork is
// the source of both, so a change to the picture is one file and one command.
//
//   node tools/banner.mjs        draw docs/telegram-banner.png and docs/og.png
//   node tools/crop.mjs --help   the crop rules, if they ever need to move
//
// The artwork is 2:1 and both targets are wider-than-tall, so each is a centre crop rather than a
// stretch: a stretched truck would be a different picture. The crop is placed to keep the drawn
// wordmark on the left and the keep on the right, which is why it is measured here and not guessed.

import { execFileSync } from 'node:child_process';
import { copyFileSync, existsSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
const art = join(root, 'docs/truck-kun-tower.jpg');
const banner = join(root, 'docs/telegram-banner.png');
const preview = join(root, 'docs/og.png');
const servedPreview = join(root, 'wwwroot/og.png');

// Both targets are wider than the artwork is, so the picture is fitted to the width and the rest of
// the frame is a black bar: a crop would cut the drawn wordmark on the left or the keep on the
// right, a stretch would show a longer truck than the drawing has, and a bar in the artwork's own
// paper colour glows white on a dark page.
const targets = [
  { out: banner, width: 640, height: 360, keep: 'the banner' },
  { out: preview, width: 1200, height: 630, keep: 'the link preview' },
];

function fail(problem) {
  throw new Error(problem);
}

function magick(...args) {
  try {
    execFileSync('magick', args, {stdio: ['ignore', 'pipe', 'pipe']});
  } catch (error) {
    fail(`magick ${args.join(' ')} failed: ${error.stderr?.toString().trim() || error.message}`);
  }
}

function probe(file) {
  if (!existsSync(file)) fail(`missing ${file}`);
  const [width, height] = execFileSync('magick', ['identify', '-format', '%w %h', file], {encoding: 'utf8'})
    .trim().split(' ').map(Number);
  return { width, height, bytes: statSync(file).size };
}

const source = probe(art);
if (source.width < source.height) fail(`${art} is taller than wide, so a wide banner cannot be a crop of it`);

const bars = '#000000';

for (const target of targets) {
  const fitted = Math.round(target.width * source.height / source.width);
  if (fitted > target.height) fail(`${target.keep} cannot hold the whole artwork at ${target.width} wide`);
  magick(art, '-filter', 'Lanczos', '-resize', `${target.width}x${fitted}`,
    '-background', bars, '-gravity', 'center', '-extent', `${target.width}x${target.height}`,
    '-strip', '-colors', '256', '-depth', '8', target.out);
  const made = probe(target.out);
  if (made.width !== target.width || made.height !== target.height)
    fail(`${target.keep} came out ${made.width}x${made.height}, expected ${target.width}x${target.height}`);
  console.log(`banner: ${target.out.replace(root, '')} ${made.width}x${made.height} ${Math.round(made.bytes / 1024)} KB ` +
    `from the artwork fitted to ${target.width}x${fitted} on ${bars}`);
}

// The page names /og.png, and the container serves wwwroot/, so the preview travels there too
copyFileSync(preview, servedPreview);
const served = probe(servedPreview);
if (served.bytes !== probe(preview).bytes) fail(`${servedPreview} is not a copy of ${preview}`);
console.log(`banner: ${servedPreview.replace(root, '')} ${served.width}x${served.height} ${Math.round(served.bytes / 1024)} KB`);
