#!/usr/bin/env node
/**
 * Publishes a packaged Cove extension ZIP to Gitea Releases:
 *   1. Versioned release for the given tag (unless --skip-versioned)
 *   2. Rolling 'latest' prerelease containing <id>-latest.zip, <id>-latest.zip.sha256,
 *      and updated latest-manifest.json (schemaVersion: 1).
 *
 * Uses only Node 22 built-ins (fetch, FormData, Blob, crypto, fs, path).
 */
import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";

function parseArgs(argv) {
  const args = {
    zip: null,
    manifest: null,
    tag: process.env.RELEASE_TAG || "",
    releaseTitle: "",
    skipVersioned: false,
  };
  for (let i = 2; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--zip") args.zip = argv[++i];
    else if (a === "--manifest") args.manifest = argv[++i];
    else if (a === "--tag") args.tag = argv[++i];
    else if (a === "--release-title") args.releaseTitle = argv[++i];
    else if (a === "--skip-versioned") args.skipVersioned = true;
    else throw new Error(`Unknown argument: ${a}`);
  }
  if (!args.zip || !fs.existsSync(args.zip)) {
    throw new Error(`--zip file not found: ${args.zip}`);
  }
  if (!args.manifest || !fs.existsSync(args.manifest)) {
    throw new Error(`--manifest file not found: ${args.manifest}`);
  }
  return args;
}

const args = parseArgs(process.argv);

const publicServerUrl = (
  process.env.PUBLIC_SERVER_URL || "https://gitea.tail971353.ts.net"
).replace(/\/+$/, "");
const apiUrl = (
  process.env.GITEA_API_URL || `${publicServerUrl}/api/v1`
).replace(/\/+$/, "");
const token = process.env.GITEA_TOKEN || process.env.GITHUB_TOKEN || "";
const repo = process.env.GITEA_REPOSITORY || process.env.GITHUB_REPOSITORY || "";
const targetCommitish =
  process.env.TARGET_COMMITISH || process.env.GITHUB_SHA || "main";

if (!token) throw new Error("Missing GITEA_TOKEN / GITHUB_TOKEN environment variable.");
if (!repo || !repo.includes("/")) {
  throw new Error(`Invalid GITEA_REPOSITORY: '${repo}'`);
}

const headers = {
  Authorization: `token ${token}`,
  Accept: "application/json",
};

async function apiFetch(relPath, options = {}) {
  const url = `${apiUrl}${relPath}`;
  const res = await fetch(url, {
    ...options,
    headers: {
      ...headers,
      ...(options.headers || {}),
    },
  });
  return res;
}

async function apiJson(relPath, options = {}) {
  const res = await apiFetch(relPath, options);
  const text = await res.text();
  if (!res.ok) {
    throw new Error(`Gitea API ${options.method || "GET"} ${relPath} failed (${res.status}): ${text}`);
  }
  return text ? JSON.parse(text) : null;
}

async function ensureRelease(tagName, { name, body, prerelease }) {
  const encodedTag = encodeURIComponent(tagName);
  const existingRes = await apiFetch(`/repos/${repo}/releases/tags/${encodedTag}`);
  if (existingRes.ok) {
    return await existingRes.json();
  }
  if (existingRes.status !== 404) {
    const errText = await existingRes.text();
    throw new Error(`Failed checking release '${tagName}' (${existingRes.status}): ${errText}`);
  }
  return await apiJson(`/repos/${repo}/releases`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      tag_name: tagName,
      target_commitish: targetCommitish,
      name,
      body,
      draft: false,
      prerelease,
    }),
  });
}

async function replaceAsset(releaseId, buffer, assetName, contentType = "application/octet-stream") {
  const assets = await apiJson(`/repos/${repo}/releases/${releaseId}/assets`);
  for (const asset of assets || []) {
    if (asset.name === assetName) {
      await apiJson(`/repos/${repo}/releases/${releaseId}/assets/${asset.id}`, {
        method: "DELETE",
      });
    }
  }
  const form = new FormData();
  form.append("attachment", new Blob([buffer], { type: contentType }), assetName);
  const uploaded = await apiJson(
    `/repos/${repo}/releases/${releaseId}/assets?name=${encodeURIComponent(assetName)}`,
    {
      method: "POST",
      body: form,
    }
  );
  return uploaded;
}

function sha256Hex(buffer) {
  return crypto.createHash("sha256").update(buffer).digest("hex");
}

function encodeTagPath(tag) {
  return tag
    .split("/")
    .map((segment) => encodeURIComponent(segment))
    .join("/");
}

async function main() {
  const ext = JSON.parse(fs.readFileSync(args.manifest, "utf8"));
  if (!ext.id || !ext.version) {
    throw new Error(`Manifest missing required id/version: ${args.manifest}`);
  }

  const zipBuffer = fs.readFileSync(args.zip);
  const zipName = path.basename(args.zip);
  const hash = sha256Hex(zipBuffer);
  const shaName = `${zipName}.sha256`;
  const shaBuffer = Buffer.from(`${hash}  ${zipName}\n`, "utf8");

  const now = new Date().toISOString();
  const tag = args.tag || "";

  // 1. Versioned release (when tag is provided and not skipped)
  if (!args.skipVersioned && tag) {
    const title = args.releaseTitle || `${ext.name || ext.id} v${ext.version}`;
    const release = await ensureRelease(tag, {
      name: title,
      body: `Automated Gitea release for ${ext.name || ext.id} (\`${ext.id}\`) v${ext.version}.\n\nSHA256: \`${hash}\``,
      prerelease: false,
    });
    await replaceAsset(release.id, zipBuffer, zipName, "application/zip");
    await replaceAsset(release.id, shaBuffer, shaName, "text/plain");
    console.log(`Published versioned release '${tag}' (${zipName}, sha256=${hash}).`);
  }

  // 2. Rolling 'latest' release & latest-manifest.json
  const latestRelease = await ensureRelease("latest", {
    name: "Latest Rolling Releases",
    body: "Static rolling-release ZIPs and latest-manifest.json for automatic/static-URL Cove extension updates.",
    prerelease: true,
  });

  let manifest = { schemaVersion: 1, updatedAt: now, extensions: {} };
  const latestAssets = await apiJson(`/repos/${repo}/releases/${latestRelease.id}/assets`);
  const existingManifestAsset = (latestAssets || []).find(
    (a) => a.name === "latest-manifest.json"
  );
  if (existingManifestAsset && existingManifestAsset.browser_download_url) {
    // Rewrite public host to apiUrl origin if needed, or fetch directly with token
    const mRes = await fetch(existingManifestAsset.browser_download_url, { headers });
    if (mRes.ok) {
      try {
        const parsed = await mRes.json();
        if (parsed && typeof parsed === "object") {
          manifest = parsed;
        }
      } catch {
        console.warn("Existing latest-manifest.json could not be parsed; reinitializing.");
      }
    }
  }

  const latestZipName = `${ext.id}-latest.zip`;
  const latestShaName = `${latestZipName}.sha256`;
  const latestShaBuffer = Buffer.from(`${hash}  ${latestZipName}\n`, "utf8");

  const downloadUrl = `${publicServerUrl}/${repo}/releases/download/latest/${encodeURIComponent(latestZipName)}`;
  const versionedUrl = tag
    ? `${publicServerUrl}/${repo}/releases/download/${encodeTagPath(tag)}/${encodeURIComponent(zipName)}`
    : downloadUrl;

  manifest.schemaVersion = 1;
  manifest.updatedAt = now;
  manifest.extensions = manifest.extensions || {};
  manifest.extensions[ext.id] = {
    id: ext.id,
    name: ext.name || ext.id,
    version: ext.version,
    description: ext.description || "",
    minCoveVersion: ext.minCoveVersion || null,
    categories: Array.isArray(ext.categories) ? ext.categories : [],
    tag: tag || null,
    sha256: hash,
    downloadUrl,
    versionedUrl,
    updatedAt: now,
  };

  const manifestBuffer = Buffer.from(JSON.stringify(manifest, null, 2) + "\n", "utf8");

  await replaceAsset(latestRelease.id, zipBuffer, latestZipName, "application/zip");
  await replaceAsset(latestRelease.id, latestShaBuffer, latestShaName, "text/plain");
  await replaceAsset(latestRelease.id, manifestBuffer, "latest-manifest.json", "application/json");

  console.log(`Updated rolling 'latest' release:`);
  console.log(`  Feed:     ${publicServerUrl}/${repo}/releases/download/latest/latest-manifest.json`);
  console.log(`  Package:  ${downloadUrl}`);
  console.log(`  Version:  ${ext.version}`);
  console.log(`  SHA256:   ${hash}`);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
