// Plays a dusty.avatar.json (written by dusty.py): poses crossfade, eyes blink, Dusty breathes.
// No dependencies. mountDusty(el, def) -> { pose, play, stop, set, snapshot, destroy }.

const CSS = `
.dl{width:100%;height:100%;overflow:visible;display:block}
.dl-layer{transition-property:opacity,transform;transform-origin:120px 206px}
.dl-layer.pop{animation:dl-pop .5s cubic-bezier(.3,1.6,.5,1)}
.dl-breathe{transform-origin:120px 206px;animation:dl-breathe 3.6s ease-in-out infinite}
.dl .eye{transform-box:fill-box;transform-origin:center;transform:translate(var(--ex,0px),var(--ey,0px)) scaleY(var(--blink,1));transition:transform .09s ease-out}
@keyframes dl-pop{0%{transform:scale(1.06,.9)}100%{transform:scale(1,1)}}
@keyframes dl-breathe{0%,100%{transform:scale(1,1)}50%{transform:scale(1.012,.985)}}
@media (prefers-reduced-motion:reduce){.dl-breathe,.dl-layer.pop{animation:none}}`;

const EASE = { smooth: 'ease-in-out', snappy: 'cubic-bezier(.2,.8,.2,1)', spring: 'cubic-bezier(.3,1.6,.5,1)' };
const UNSAFE = /<\s*(script|foreignObject|iframe|image|use|a|animate|set)\b|\son\w+\s*=|javascript:|href\s*=/i;
const esc = s => String(s).replace(/[&<>"]/g, c => `&#${c.charCodeAt(0)};`);
const INK_VAR =/var\(--dusty-ink, ?(#[0-9A-Fa-f]{6})\)/g;

/** Step indices for one pass: loop/once play in order, pingPong comes back without repeating the ends. */
export function cycle(anim) {
  const n = anim.steps.length, fwd = [...Array(n).keys()];
  return anim.playbackMode === 'pingPong' && n > 2 ? fwd.concat(fwd.slice(1, -1).reverse())
       : anim.playbackMode === 'pingPong' && n === 2 ? [0, 1] : fwd;
}

const hex = c => [1, 3, 5].map(i => parseInt(c.slice(i, i + 2), 16));
export const mix = (a, b, t) => '#' + hex(a).map((v, i) => Math.round(v + (hex(b)[i] - v) * t).toString(16).padStart(2, '0')).join('').toUpperCase();

/** A clean palette built from one body colour, keeping Dusty's pink ears, blush and nose. */
export function tint(base, body, ink) {
  return { ...base, fill: body.toUpperCase(), shade: mix(body, ink, .14), light: mix(body, '#FFFFFF', .5),
           fur: mix(body, ink, .38), paw: mix(body, '#FFFFFF', .45) };
}

/** Swap one palette's colours for another's inside a pose's markup. */
export function recolor(svg, from, to) {
  for (const k in from) if (from[k] && to[k] && from[k] !== to[k]) svg = svg.replaceAll(from[k], to[k]);
  return svg;
}

export function validate(def) {
  if (def?.schema !== 'freedisk/dusty-avatar' || def.schemaVersion !== 1) throw new Error('Not a dusty.avatar.json (schema freedisk/dusty-avatar v1)');
  // Poses go in via innerHTML, so an imported file must not carry script, handlers or external refs.
  for (const [name, p] of Object.entries(def.poses || {}))
    if (typeof p.svg !== 'string' || UNSAFE.test(p.svg)) throw new Error(`Pose "${name}" has unsafe or missing markup`);
  for (const [name, a] of Object.entries(def.animations || {}))
    for (const s of a.steps) if (!def.poses[s.pose]) throw new Error(`Animation "${name}" uses unknown pose "${s.pose}"`);
  return def;
}

export function mountDusty(el, def, opts = {}) {
  validate(def);
  if (!document.getElementById('dl-style')) document.head.append(Object.assign(document.createElement('style'), { id: 'dl-style', textContent: CSS }));
  const reduce = matchMedia('(prefers-reduced-motion: reduce)').matches;
  const o = { gaze: [0, 0], color: null, flip: false, rotate: 0, zoom: 1, x: 0, y: 0, breathe: true, ...opts };
  el.innerHTML = `<svg class="dl" viewBox="${def.viewBox}" role="img" aria-label="${esc(def.name || 'Avatar')}"><g class="dl-view"><g class="dl-breathe"></g></g></svg>`;
  const svg = el.firstChild, view = svg.firstChild, stage = view.firstChild;
  let current = null, timers = [], blinkTimer = 0, anim = null;

  const markup = name => {
    const p = def.poses[name];
    return o.color && p.palette === 'clean' ? recolor(p.svg, def.palettes.clean, tint(def.palettes.clean, o.color, def.ink)) : p.svg;
  };
  function apply() {
    svg.style.setProperty('--ex', o.gaze[0] + 'px'); svg.style.setProperty('--ey', o.gaze[1] + 'px');
    view.setAttribute('transform', `translate(${120 + o.x} ${120 + o.y}) rotate(${o.rotate}) scale(${o.zoom * (o.flip ? -1 : 1)} ${o.zoom}) translate(-120 -120)`);
    stage.classList.toggle('dl-breathe', o.breathe && !reduce);
  }

  function pose(name, ms = 0, ease = 'smooth') {
    if (!def.poses[name]) throw new Error(`Unknown pose "${name}"`);
    if (name === current && stage.lastChild) return;
    const next = document.createElementNS('http://www.w3.org/2000/svg', 'g');
    next.setAttribute('class', 'dl-layer'); next.innerHTML = markup(name);
    const old = [...stage.children];
    ms = reduce ? 0 : ms;
    next.style.transitionDuration = ms + 'ms'; next.style.transitionTimingFunction = EASE[ease] || EASE.smooth;
    next.style.opacity = ms ? 0 : 1;
    stage.append(next); current = name;
    if (ease === 'spring' && !reduce) next.classList.add('pop');
    if (ms) requestAnimationFrame(() => requestAnimationFrame(() => { next.style.opacity = 1; }));
    old.forEach(l => { l.style.transitionDuration = ms + 'ms'; l.style.opacity = 0; });
    const drop = () => old.forEach(l => l.remove());
    ms ? setTimeout(drop, ms + 30) : drop();
  }

  function blink(b) {
    clearTimeout(blinkTimer);
    if (!b?.enabled || reduce) return;
    const next = d => { blinkTimer = setTimeout(() => {
      if (def.poses[current]?.blinks) { svg.style.setProperty('--blink', .08); setTimeout(() => svg.style.setProperty('--blink', 1), b.durationMs); }
      next(b.minIntervalMs + Math.random() * (b.maxIntervalMs - b.minIntervalMs));
    }, d); };
    next(b.initialDelayMs);
  }

  function stop() { timers.forEach(clearTimeout); timers = []; clearTimeout(blinkTimer); anim = null; }

  /** Play an animation by name (or an animation object). onStep(i) fires as each step starts; onEnd when "once" finishes. */
  function play(a, { onStep, onEnd } = {}) {
    stop();
    anim = typeof a === 'string' ? def.animations[a] : a;
    if (!anim?.steps.length) return;
    const order = cycle(anim);
    let k = 0, first = true;
    const run = () => {
      const i = order[k], s = anim.steps[i];
      pose(s.pose, first ? 0 : s.transitionMs, s.transition); first = false;
      onStep?.(i);
      k++;
      if (k === order.length) { if (anim.playbackMode === 'once') { timers.push(setTimeout(() => { onEnd?.(); }, s.holdMs)); return; } k = 0; }
      timers.push(setTimeout(run, s.holdMs));
    };
    blink(anim.blink);
    run();
  }

  /** Change view options: gaze [x,y], color (hex|null), flip, rotate, zoom, x, y, breathe. */
  function set(next) {
    const recolorNeeded = 'color' in next && next.color !== o.color;
    Object.assign(o, next); apply();
    if (recolorNeeded && current) { const c = current; current = null; pose(c); }
  }

  /** The current pose as a standalone SVG string: background {type,from,to,angle} and ink are optional. */
  function snapshot({ size = 1024, background = { type: 'transparent' }, ink = def.ink } = {}) {
    const bg = background, d = `<defs>${
      bg.type === 'linear' ? `<linearGradient id="bg" gradientTransform="rotate(${bg.angle ?? 90} .5 .5)"><stop offset="0" stop-color="${bg.from}"/><stop offset="1" stop-color="${bg.to}"/></linearGradient>`
      : bg.type === 'radial' ? `<radialGradient id="bg" cx=".5" cy=".55" r=".7"><stop offset="0" stop-color="${bg.from}"/><stop offset="1" stop-color="${bg.to}"/></radialGradient>` : ''}</defs>`;
    const rect = bg.type === 'solid' ? `<rect width="240" height="240" fill="${bg.from}"/>` : bg.type === 'linear' || bg.type === 'radial' ? '<rect width="240" height="240" fill="url(#bg)"/>' : '';
    const eye = `<style>.eye{transform-box:fill-box;transform-origin:center;transform:translate(${o.gaze[0]}px,${o.gaze[1]}px)}</style>`;
    return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${def.viewBox}" width="${size}" height="${size}">${eye}${d}${rect}`
         + `<g transform="${view.getAttribute('transform')}">${markup(current).replace(INK_VAR, ink)}</g></svg>`;
  }

  apply();
  pose(opts.pose || def.poseOrder[0]);
  return { pose, play, stop, set, snapshot, get current() { return current; }, destroy() { stop(); el.innerHTML = ''; } };
}
