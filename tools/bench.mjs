// Pan and zoom the map in a headless Chrome and print ms per frame, the check
// that keeps the viewer at the screen's rate: 16.7 means it never missed one.
// Same Chrome and proxy as shoot.mjs:
//   node tools/bench.mjs http://127.0.0.1:8765 9333
const [BASE, PORT] = process.argv.slice(2);
const tab = await (await fetch(`http://127.0.0.1:${PORT}/json/new?about:blank`, { method: "PUT" })).json();
const ws = new WebSocket(tab.webSocketDebuggerUrl); await new Promise(r => ws.onopen = r);
let id = 0; const pending = new Map(); const errors = [];
ws.onmessage = e => { const m = JSON.parse(e.data);
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result ?? m.error); pending.delete(m.id); }
  if (m.method === "Runtime.exceptionThrown") errors.push((m.params.exceptionDetails.exception?.description ?? m.params.exceptionDetails.text).slice(0, 200)); };
const send = (method, params = {}) => new Promise(r => { pending.set(++id, r); ws.send(JSON.stringify({ id, method, params })); });
const ev = async (expression) => (await send("Runtime.evaluate", { returnByValue: true, awaitPromise: true, expression })).result?.value;
await send("Runtime.enable"); await send("Page.enable"); await send("Network.enable"); await send("Network.setCacheDisabled", { cacheDisabled: true });
await send("Page.navigate", { url: BASE + "/" }); await new Promise(r => setTimeout(r, 18000));
// the world fitted, then the thick of the builds at a base's zoom
const views = {
  world: "fitExplored(true);",
  base: "{ const c = PIECES.reduce((a, p) => [a[0] + p.px/PIECES.length, a[1] + p.py/PIECES.length], [0, 0]);"
      + " scale = 8; tx = stage.clientWidth/2 - c[0]*scale; ty = stage.clientHeight/2 - c[1]*scale; apply(); }",
};
const ms = (setup, step) => ev(`(async () => {
  ${setup}
  await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));
  let n = 0; const t0 = performance.now();
  await new Promise(res => { function f(){ ${step}; if(++n >= 60) res(); else requestAnimationFrame(f); } requestAnimationFrame(f); });
  return +((performance.now() - t0) / n).toFixed(1);
})()`);
console.log(await ev("({pieces: PIECES && PIECES.length, markers: document.querySelectorAll('#markers .marker').length})"));
for (const [name, setup] of Object.entries(views)) {
  const pan = await ms(setup, "tx += (n % 2 ? 7 : -7); ty += 3; apply()");
  const zoom = await ms(setup, "zoomAt(stage.clientWidth/2, stage.clientHeight/2, n < 30 ? 1.03 : 1/1.03)");
  console.log(name, { pan, zoom });
}
if (errors.length) console.log("errors", errors);
await fetch(`http://127.0.0.1:${PORT}/json/close/${tab.id}`);
ws.close(); process.exit(0);
