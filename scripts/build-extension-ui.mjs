#!/usr/bin/env node
/**
 * Extension UI Build Script
 * Compiles TypeScript React components into an ESM bundle for Cove's frontend extension runtime.
 * Usage: node scripts/build-extension-ui.mjs [ExtensionName]
 */

import * as esbuild from "esbuild";
import fs from "node:fs";
import path from "node:path";
import process from "node:process";

const root = path.resolve(import.meta.dirname, "..");
const extensionsDir = path.join(root, "extensions");

const runtimeAliases = {
  react: "@cove/runtime/react",
  "react-dom": "@cove/runtime/react-dom",
  "react-dom/client": "@cove/runtime/react-dom-client",
  "react/jsx-runtime": "@cove/runtime/react-jsx-runtime",
  "react/jsx-dev-runtime": "@cove/runtime/react-jsx-dev-runtime",
  "@tanstack/react-query": "@cove/runtime/react-query",
  "lucide-react": "@cove/runtime/lucide-react",
  "@cove/runtime/components": "@cove/runtime/components",
  "@cove/runtime/api": "@cove/runtime/api",
};

const targetFilter = process.argv[2] ?? null;

const subdirs = fs.readdirSync(extensionsDir, { withFileTypes: true })
  .filter(d => d.isDirectory())
  .map(d => d.name)
  .filter(name => !targetFilter || name === targetFilter);

let builtCount = 0;

for (const name of subdirs) {
  const extDir = path.join(extensionsDir, name);
  const uiDir = path.join(extDir, "ui");
  if (!fs.existsSync(uiDir)) continue;

  const entryFile = [
    path.join(uiDir, `${name}.tsx`),
    path.join(uiDir, `${name}.ts`),
    path.join(uiDir, "index.tsx"),
    path.join(uiDir, "index.ts")
  ].find(p => fs.existsSync(p));

  if (!entryFile) {
    console.warn(`[build:ui] Skipping ${name}: no entry file found in ${uiDir}`);
    continue;
  }

  const distDir = path.join(extDir, "dist");
  fs.mkdirSync(distDir, { recursive: true });

  const outFile = path.join(distDir, "bundle.js");
  const cssOutFile = path.join(distDir, "bundle.css");

  await esbuild.build({
    entryPoints: [entryFile],
    bundle: true,
    format: "esm",
    outfile: outFile,
    alias: runtimeAliases,
    external: Object.values(runtimeAliases),
    jsx: "automatic",
    jsxImportSource: "react",
    splitting: false,
    sourcemap: false,
    minify: true,
  });

  // Ensure bundle.css exists (Cove host injects Tailwind utilities, extension styles can augment)
  const entryCss = path.join(uiDir, "bundle.css");
  if (fs.existsSync(entryCss)) {
    fs.copyFileSync(entryCss, cssOutFile);
  } else if (!fs.existsSync(cssOutFile)) {
    fs.writeFileSync(cssOutFile, "/* Built-in theme styles inherited from Cove host */\n");
  }

  console.log(`✅ Built ${name} UI bundle: ${outFile}`);
  builtCount++;
}

if (builtCount === 0 && targetFilter) {
  console.warn(`[build:ui] No UI extensions matched '${targetFilter}'`);
}
