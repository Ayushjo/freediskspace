// node dusty-avatar.test.mjs
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { cycle, mix, tint, recolor, validate } from './dusty-avatar.js';

const s = n => Array.from({ length: n }, () => ({ pose: 'hello' }));
assert.deepEqual(cycle({ playbackMode: 'loop', steps: s(3) }), [0, 1, 2]);
assert.deepEqual(cycle({ playbackMode: 'once', steps: s(3) }), [0, 1, 2]);
assert.deepEqual(cycle({ playbackMode: 'pingPong', steps: s(4) }), [0, 1, 2, 3, 2, 1]);
assert.deepEqual(cycle({ playbackMode: 'pingPong', steps: s(2) }), [0, 1]);
assert.deepEqual(cycle({ playbackMode: 'pingPong', steps: s(1) }), [0]);

assert.equal(mix('#000000', '#FFFFFF', .5), '#808080');
const def = validate(JSON.parse(readFileSync(new URL('./dusty.avatar.json', import.meta.url))));
const mint = tint(def.palettes.clean, '#9fd8c9', def.ink);
assert.equal(mint.fill, '#9FD8C9');
assert.equal(mint.ear, def.palettes.clean.ear, 'pink ears stay');
const hello = recolor(def.poses.hello.svg, def.palettes.clean, mint);
assert.ok(!hello.includes(def.palettes.clean.fill) && hello.includes('#9FD8C9'));

const bad = svg => () => validate({ ...def, poses: { ...def.poses, hello: { ...def.poses.hello, svg } } });
assert.throws(bad('<image href="x" onerror="alert(1)"/>'), /unsafe/);
assert.throws(bad('<g onclick="x()"></g>'), /unsafe/);
assert.throws(bad('<script>1</script>'), /unsafe/);
assert.throws(() => validate({ ...def, animations: { x: { steps: [{ pose: 'nope' }] } } }), /unknown pose/);
assert.throws(() => validate({ schema: 'bible-strong/avatar-definition', schemaVersion: 1 }), /Not a dusty/);
console.log('dusty-avatar: ok');
