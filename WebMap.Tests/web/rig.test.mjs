// The 3D view's rig logic that needs no three.js, under `node --test`.
import { test } from "node:test";
import assert from "node:assert/strict";
import { lookKey } from "../../WebMap/web/js/rig.js";

const look = () => ({ model: 0, skin: [1, 0.82, 0.68], hair: [0.55, 0.32, 0.14], slots: { helmet: "HelmetBronze" },
  parts: ["Player@body0", "HelmetBronze@Helmet_attach", "Hair5_2@Helmet_attach"] });

test("a player is dressed again only when what they wear changes, not on every poll", () => {
  assert.equal(lookKey(look()), lookKey(look()));
  const bare = look(); bare.parts = ["Player@body0", "Hair5@Helmet_attach"];
  assert.notEqual(lookKey(bare), lookKey(look()));
  const grey = look(); grey.hair = [0.5, 0.5, 0.5];
  assert.notEqual(lookKey(grey), lookKey(look()));
  assert.equal(lookKey(undefined), null);                       // a mod from before the rigs: a plain figure
});
