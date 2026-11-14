#!/usr/bin/env node
/**
 * Synchronizes the cove-extension-builder agent & skills suite
 * from its source repository (https://github.com/otter-twunk/cove-extension-builder).
 *
 * Usage:
 *   npm run sync:agent
 *   node scripts/sync-agent-skills.mjs
 */

import { execSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import process from "node:process";

const root = path.resolve(import.meta.dirname, "..");
const pluginDir = path.join(root, ".agents", "plugins", "cove-extension-builder");
const repoUrl = "https://github.com/otter-twunk/cove-extension-builder.git";

console.log("🔄 Syncing cove-extension-builder agent & skills from upstream source...");

try {
  if (!fs.existsSync(pluginDir) || fs.readdirSync(pluginDir).length === 0) {
    console.log("📦 Initializing submodule / cloning from", repoUrl);
    try {
      execSync(`git submodule update --init --recursive --remote "${pluginDir}"`, {
        cwd: root,
        stdio: "inherit"
      });
    } catch {
      execSync(`git clone "${repoUrl}" "${pluginDir}"`, {
        cwd: root,
        stdio: "inherit"
      });
    }
  } else {
    // Submodule or git repo already present: fetch and merge latest upstream main
    try {
      execSync("git submodule update --init --recursive --remote --merge .agents/plugins/cove-extension-builder", {
        cwd: root,
        stdio: "inherit"
      });
    } catch {
      // Fallback to direct git pull inside the plugin directory
      execSync("git fetch origin main && git pull --ff-only origin main", {
        cwd: pluginDir,
        stdio: "inherit"
      });
    }
  }

  // Display latest commit info
  const commitInfo = execSync("git -C .agents/plugins/cove-extension-builder log -1 --format='%h - %s (%ci)'", {
    cwd: root,
    encoding: "utf8"
  }).trim();

  console.log(`✅ Successfully synced cove-extension-builder!`);
  console.log(`📌 Current upstream version: ${commitInfo}`);
} catch (error) {
  console.error("❌ Failed to sync cove-extension-builder from upstream:", error.message);
  process.exit(1);
}
