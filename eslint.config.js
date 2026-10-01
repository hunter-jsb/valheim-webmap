// The viewer's lint (`npm run lint`): the rules that catch real mistakes -- a name never
// declared or never read, a second global from a file that defines one -- and no style.
import globals from "globals";
import html from "eslint-plugin-html";

// The classic scripts, each defining one global the pages and the others read.
const MODULES = {MapCore: "map-core.js", PlayerCard: "js/card.js", Layers: "js/layers.js", Spot: "js/spot.js", Street: "js/street.js", Tour: "js/tour.js"};
// The 3D view's ES modules, loaded on first use through the pages' import map.
const ES_MODULES = ["WebMap/web/js/rig.js", "WebMap/web/js/sky.js", "WebMap/web/js/view3d.js"];

// Window properties a missing local would read without a word: the browser declares them,
// so no-undef cannot see the slip. The ones the pages mean (location, history, ...) stay.
const CONFUSING = ["blur", "close", "closed", "event", "external", "find", "focus", "frames", "length", "name", "open", "opener",
  "origin", "parent", "print", "screen", "scroll", "self", "status", "stop", "top"];

const UNUSED = {args: "after-used", argsIgnorePattern: "^_", varsIgnorePattern: "^_", caughtErrors: "none"};
const rules = {
  // names
  "no-undef": "error",
  "no-unused-vars": ["error", UNUSED],
  "no-redeclare": "error",
  "no-shadow-restricted-names": "error",
  "no-global-assign": "error",
  "no-use-before-define": ["error", {functions: false, classes: false, variables: false}],   // read in its own scope before it is set
  "no-var": "error",
  "prefer-const": "error",
  // mistakes
  "eqeqeq": ["warn", "smart"],                       // == null is the null-or-undefined test
  "no-cond-assign": "error",
  "no-const-assign": "error",
  "no-constant-binary-expression": "error",
  "no-dupe-class-members": "error",
  "no-dupe-else-if": "error",
  "no-dupe-keys": "error",
  "no-duplicate-case": "error",
  "no-empty": ["error", {allowEmptyCatch: true}],    // a storage or parse that may fail and need not
  "no-fallthrough": "error",
  "no-func-assign": "error",
  "no-self-assign": "error",
  "no-sparse-arrays": "error",
  "no-unreachable": "error",
  "no-unsafe-negation": "error",
  "no-unsafe-optional-chaining": "error",
  "no-unused-expressions": ["error", {allowShortCircuit: true, allowTernary: true}],
  "use-isnan": "error",
  "valid-typeof": "error",
};

export default [
  {ignores: ["WebMap/web/vendor/**"]},
  {
    files: ["WebMap/web/**/*.{js,html}"],
    plugins: {html},
    languageOptions: {sourceType: "script", globals: {...globals.browser, ...Object.fromEntries(Object.keys(MODULES).map(g => [g, "readonly"]))}},
    rules: {...rules, "no-restricted-globals": ["error", ...CONFUSING]},
  },
  // A classic script is its one global and nothing else at its top level: its own name is
  // writable there, so no-implicit-globals passes it and reports any other. The pages are not
  // held to it: their top-level functions are the page's names, shared across its inline scripts.
  {
    files: ["WebMap/web/**/*.js"],
    ignores: ES_MODULES,
    rules: {"no-implicit-globals": ["error", {lexicalBindings: true}], "no-redeclare": ["error", {builtinGlobals: false}],
      "no-unused-vars": ["error", {...UNUSED, vars: "local"}]},
  },
  ...Object.entries(MODULES).map(([g, file]) => ({files: [`WebMap/web/${file}`], languageOptions: {globals: {[g]: "writable"}}})),
  {files: ES_MODULES, languageOptions: {sourceType: "module"}},
  {files: ["WebMap.Tests/web/*.mjs", "tools/*.mjs"], languageOptions: {sourceType: "module", globals: globals.node}, rules},
];
