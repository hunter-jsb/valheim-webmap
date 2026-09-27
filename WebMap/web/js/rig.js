// A live player's rig in the 3D view, the parts that need no three.js: when a look has
// changed, and which paint lies over the body's skin.

// What a rig is built from: the body, the parts drawn and the two colours. The same look
// gives the same key, so a player is dressed again only when what they wear changes.
export function lookKey(look) {
  if (!look || !Array.isArray(look.parts) || !look.parts.length) return null;
  const c = (v) => (Array.isArray(v) ? v.map((x) => Math.round(x * 1000)).join(',') : '');
  return [look.model | 0, look.parts.join('|'), c(look.skin), c(look.hair)].join('/');
}

// The body's chest and legs paint: the garment worn over each, else the body's own
// (entries: the library's, in the look's order, body first; ct / lt the texture files).
export function paintOf(entries) {
  const body = entries[0] || {};
  let chest = body.ct || null, legs = body.lt || null;
  for (const e of entries.slice(1)) {
    if (e && e.ct) chest = e.ct;
    if (e && e.lt) legs = e.lt;
  }
  return { chest, legs };
}
