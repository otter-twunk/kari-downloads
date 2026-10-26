#!/usr/bin/env node
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import process from "node:process";

const root = path.resolve(import.meta.dirname, "..");
const catalogPath = path.join(root, "extensions", "catalog.json");

const HOST_PROVIDED_DLLS = new Set([
  "Cove.Core.dll",
  "Cove.Plugins.dll",
  "Cove.Sdk.dll",
  "Microsoft.EntityFrameworkCore.dll",
  "Microsoft.EntityFrameworkCore.Abstractions.dll",
  "Microsoft.EntityFrameworkCore.Relational.dll",
  "Npgsql.dll",
  "Npgsql.EntityFrameworkCore.PostgreSQL.dll",
  "Pgvector.dll",
  "Pgvector.EntityFrameworkCore.dll",
  "MediatR.Contracts.dll"
]);

const targetFilter = process.argv[2] ?? null;
const catalog = JSON.parse(fs.readFileSync(catalogPath, "utf8"));
const entries = catalog.extensions.filter(
  entry => !targetFilter || entry.name === targetFilter || entry.id === targetFilter
);

if (entries.length === 0) {
  console.error(`ERROR: No extension matched '${targetFilter}'.`);
  process.exit(1);
}

execFileSync("node", [path.join(root, "scripts", "validate-extension-repo.mjs")], {
  cwd: root,
  stdio: "inherit"
});

const packagesDir = path.join(root, "artifacts", "packages");
fs.mkdirSync(packagesDir, { recursive: true });

for (const entry of entries) {
  const extDir = path.join(root, entry.path);
  const manifestPath = path.join(extDir, "extension.json");
  const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
  const version = manifest.version ?? "0.1.0";
  const publishDir = path.join(root, "artifacts", entry.name);

  fs.rmSync(publishDir, { recursive: true, force: true });
  fs.mkdirSync(publishDir, { recursive: true });

  if (!entry.manifestOnly) {
    execFileSync(
      "dotnet",
      [
        "publish",
        entry.path,
        "-c",
        "Release",
        "-o",
        publishDir,
        `-p:Version=${version}`,
        "-p:ContinuousIntegrationBuild=true"
      ],
      { cwd: root, stdio: "inherit" }
    );
  }

  fs.copyFileSync(manifestPath, path.join(publishDir, "extension.json"));
  if (entry.contentOnly && fs.existsSync(path.join(extDir, "scrapers"))) {
    fs.cpSync(path.join(extDir, "scrapers"), path.join(publishDir, "scrapers"), { recursive: true });
  }

  // Verify no host-provided assemblies leaked into publishDir
  const publishedFiles = fs.readdirSync(publishDir);
  const leaked = publishedFiles.filter(file => HOST_PROVIDED_DLLS.has(file));
  if (leaked.length > 0) {
    console.error(`ERROR: ${entry.id} publish output contains host-provided assemblies: ${leaked.join(", ")}`);
    process.exit(1);
  }

  if (!entry.manifestOnly && !publishedFiles.includes(manifest.entryDll)) {
    console.error(`ERROR: ${entry.id} publish output is missing entryDll '${manifest.entryDll}'.`);
    process.exit(1);
  }

  const zipName = `${entry.id}-${version}.zip`;
  const zipPath = path.join(packagesDir, zipName);
  fs.rmSync(zipPath, { force: true });

  execFileSync("zip", ["-r", "-q", zipPath, "."], {
    cwd: publishDir,
    stdio: "inherit"
  });

  console.log(`Packaged and verified ${zipPath}`);
}
