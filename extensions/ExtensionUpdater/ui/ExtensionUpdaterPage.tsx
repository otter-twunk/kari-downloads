import React, { useEffect, useMemo, useState } from "@cove/runtime/react";
import { extensionFetch } from "@cove/runtime/api";
import { useDocumentTitle } from "@cove/runtime/components";
import {
  RefreshCw,
  Download,
  Plus,
  Trash2,
  CheckCircle2,
  AlertCircle,
  ArrowUpCircle,
  Loader2,
  Globe,
  GitBranch,
  FileJson,
  RotateCcw,
  Search,
  HardDrive,
} from "@cove/runtime/lucide-react";

type SourceKind = "manifest-feed" | "static-zip" | "github-release" | "gitea-release" | "local-cache";
type StatusFilter = "all" | "updates" | "installed" | "available";
type CategoryFilter = "all" | "downloaders" | "scrapers" | "tools";

interface ExtensionSource {
  id: string;
  kind: SourceKind;
  label: string;
  url: string;
  tagPrefix?: string | null;
  assetPattern?: string | null;
  extensionId?: string | null;
  enabled: boolean;
}

interface DiscoveredItem {
  sourceId: string;
  sourceLabel: string;
  sourceKind: SourceKind;
  extensionId?: string | null;
  name: string;
  remoteVersion?: string | null;
  minCoveVersion?: string | null;
  categories?: string[] | null;
  description?: string | null;
  downloadUrl: string;
  updatedAt?: string | null;
  error?: string | null;
  localCached?: boolean;
  localVersion?: string | null;
  localFileName?: string | null;
  localPackageUrl?: string | null;
}

interface InstalledExtension {
  id: string;
  name: string;
  version: string;
}

/**
 * Compares two SemVer strings including pre-release suffixes (e.g. 1.0.0 > 1.0.0-alpha.1).
 */
export function compareSemver(a?: string | null, b?: string | null): number {
  if (!a || !b) return 0;
  const cleanA = a.trim().replace(/^v/i, "").split("+")[0];
  const cleanB = b.trim().replace(/^v/i, "").split("+")[0];

  const [coreA, preA] = cleanA.split("-", 2);
  const [coreB, preB] = cleanB.split("-", 2);

  const pa = coreA.split(".").map((n) => parseInt(n, 10) || 0);
  const pb = coreB.split(".").map((n) => parseInt(n, 10) || 0);
  const len = Math.max(pa.length, pb.length, 3);
  for (let i = 0; i < len; i++) {
    const va = pa[i] ?? 0;
    const vb = pb[i] ?? 0;
    if (va !== vb) return va > vb ? 1 : -1;
  }

  // Stable release is higher precedence than pre-release of the same core version
  if (!preA && preB) return 1;
  if (preA && !preB) return -1;
  if (!preA && !preB) return 0;

  return preA!.localeCompare(preB!, undefined, { numeric: true, sensitivity: "base" });
}

function getItemKey(item: DiscoveredItem, idx?: number): string {
  return `${item.sourceId}:${item.extensionId || item.downloadUrl || idx || ""}`;
}

function inferItemCategory(item: DiscoveredItem): "downloaders" | "scrapers" | "tools" {
  const id = (item.extensionId ?? "").toLowerCase();
  const name = item.name.toLowerCase();
  const cats = (item.categories ?? []).map((c) => c.toLowerCase());
  if (id.includes(".downloaders.") || cats.includes("downloader") || (name.includes("downloader") && !id.includes(".tools."))) {
    return "downloaders";
  }
  if (id.includes(".scrapers.") || cats.includes("scraper") || (name.includes("scraper") && !id.includes(".tools."))) {
    return "scrapers";
  }
  return "tools";
}

export function ExtensionUpdaterPage() {
  useDocumentTitle("Extension Updater");

  const [sources, setSources] = useState<ExtensionSource[]>([]);
  const [discovered, setDiscovered] = useState<DiscoveredItem[]>([]);
  const [installedMap, setInstalledMap] = useState<Record<string, InstalledExtension>>({});
  const [checkErrors, setCheckErrors] = useState<string[]>([]);

  const [loading, setLoading] = useState(true);
  const [checking, setChecking] = useState(false);
  const [syncingLocal, setSyncingLocal] = useState(false);
  const [updatingKey, setUpdatingKey] = useState<string | null>(null);
  const [batchRunning, setBatchRunning] = useState(false);
  const [statusLog, setStatusLog] = useState<Record<string, { ok: boolean; message: string }>>({});

  // Search & Filter state
  const [searchQuery, setSearchQuery] = useState("");
  const [statusFilter, setStatusFilter] = useState<StatusFilter>("all");
  const [categoryFilter, setCategoryFilter] = useState<CategoryFilter>("all");

  // Add source form state
  const [showAddForm, setShowAddForm] = useState(false);
  const [newKind, setNewKind] = useState<SourceKind>("static-zip");
  const [newLabel, setNewLabel] = useState("");
  const [newUrl, setNewUrl] = useState("");
  const [newTagPrefix, setNewTagPrefix] = useState("");
  const [newAssetPattern, setNewAssetPattern] = useState("");
  const [savingSource, setSavingSource] = useState(false);

  async function loadInstalled() {
    const res = await extensionFetch("/api/extensions");
    if (!res.ok) return;
    const data = await res.json();
    const list: InstalledExtension[] = Array.isArray(data) ? data : data.extensions ?? [];
    const map: Record<string, InstalledExtension> = {};
    for (const ext of list) {
      if (ext.id) map[ext.id.toLowerCase()] = ext;
    }
    setInstalledMap(map);
  }

  async function loadSources() {
    const res = await extensionFetch("/api/ext/extension-updater/sources");
    if (res.ok) {
      setSources(await res.json());
    }
  }

  async function checkUpdates() {
    setChecking(true);
    setCheckErrors([]);
    try {
      await loadInstalled();
      const res = await extensionFetch("/api/ext/extension-updater/check?inspectStaticZips=true", {
        method: "POST",
      });
      if (!res.ok) {
        setCheckErrors([`Check failed with HTTP ${res.status}`]);
        return;
      }
      const data = await res.json();
      setDiscovered(data.items ?? []);
      setCheckErrors(data.errors ?? []);
    } finally {
      setChecking(false);
    }
  }

  useEffect(() => {
    (async () => {
      setLoading(true);
      await Promise.all([loadSources(), loadInstalled()]);
      setLoading(false);
      await checkUpdates();
    })();
  }, []);

  const enrichedItems = useMemo(() => {
    return discovered.map((item, idx) => {
      const key = getItemKey(item, idx);
      const installed = item.extensionId ? installedMap[item.extensionId.toLowerCase()] : undefined;
      const hasUpdate =
        !!installed && !!item.remoteVersion && compareSemver(item.remoteVersion, installed.version) > 0;
      const isUpToDate =
        !!installed && !!item.remoteVersion && compareSemver(item.remoteVersion, installed.version) <= 0;
      const category = inferItemCategory(item);
      return { key, item, installed, hasUpdate, isUpToDate, category };
    });
  }, [discovered, installedMap]);

  const outdatedItems = useMemo(
    () => enrichedItems.filter((x) => x.hasUpdate && !x.item.error && (x.item.localPackageUrl || x.item.downloadUrl)),
    [enrichedItems]
  );

  const filteredItems = useMemo(() => {
    const q = searchQuery.trim().toLowerCase();
    return enrichedItems.filter(({ item, installed, hasUpdate, category }) => {
      if (statusFilter === "updates" && !hasUpdate) return false;
      if (statusFilter === "installed" && !installed) return false;
      if (statusFilter === "available" && installed) return false;
      if (categoryFilter !== "all" && category !== categoryFilter) return false;
      if (q) {
        const hay = `${item.name} ${item.extensionId ?? ""} ${item.description ?? ""} ${item.sourceLabel}`.toLowerCase();
        if (!hay.includes(q)) return false;
      }
      return true;
    });
  }, [enrichedItems, statusFilter, categoryFilter, searchQuery]);

  async function syncLocalCache() {
    setSyncingLocal(true);
    setCheckErrors([]);
    try {
      const res = await extensionFetch("/api/ext/extension-updater/local-cache/sync", {
        method: "POST",
      });
      const body = await res.json().catch(() => ({}));
      if (!res.ok) {
        setCheckErrors([body.message || `Local cache sync failed with HTTP ${res.status}`]);
        return;
      }
      if (Array.isArray(body.errors) && body.errors.length > 0) {
        setCheckErrors(body.errors);
      } else if (body.rawError && !body.success) {
        setCheckErrors([body.rawError]);
      }
      await checkUpdates();
    } catch (err: any) {
      setCheckErrors([err?.message || "Local cache sync failed"]);
    } finally {
      setSyncingLocal(false);
    }
  }

  async function installPackage(item: DiscoveredItem, itemKey: string, options?: { skipRefresh?: boolean }) {
    setUpdatingKey(itemKey);
    try {
      let res: Response;
      if (item.localCached && item.localPackageUrl) {
        const pkgRes = await extensionFetch(item.localPackageUrl);
        if (!pkgRes.ok) {
          throw new Error(`Failed to fetch local package (HTTP ${pkgRes.status})`);
        }
        const blob = await pkgRes.blob();
        const fileName = item.localFileName || `${item.extensionId || "extension"}.zip`;
        const formData = new FormData();
        formData.append("file", new File([blob], fileName, { type: "application/zip" }));
        formData.append("trustUnverified", "true");
        res = await extensionFetch("/api/extensions/install-from-zip", {
          method: "POST",
          body: formData,
        });
      } else {
        res = await extensionFetch("/api/extensions/install-from-url", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ url: item.downloadUrl, trustUnverified: true }),
        });
      }
      const body = await res.json().catch(() => ({}));
      if (!res.ok) {
        setStatusLog((prev) => ({
          ...prev,
          [itemKey]: { ok: false, message: body.message || `HTTP ${res.status}` },
        }));
        return false;
      }
      setStatusLog((prev) => ({
        ...prev,
        [itemKey]: {
          ok: true,
          message: body.message || (item.localCached ? "Installed from local ZIP" : "Installed"),
        },
      }));
      if (!options?.skipRefresh) {
        await loadInstalled();
      }
      return true;
    } catch (err: any) {
      setStatusLog((prev) => ({
        ...prev,
        [itemKey]: { ok: false, message: err?.message || "Install failed" },
      }));
      return false;
    } finally {
      setUpdatingKey(null);
    }
  }

  async function updateAllOutdated() {
    if (outdatedItems.length === 0) return;
    setBatchRunning(true);
    try {
      // Update self last so all other extensions finish before ExtensionUpdater reloads
      const ordered = [...outdatedItems].sort((a, b) => {
        const aSelf = a.item.extensionId === "karinauts.tools.extension-updater" ? 1 : 0;
        const bSelf = b.item.extensionId === "karinauts.tools.extension-updater" ? 1 : 0;
        return aSelf - bSelf;
      });
      for (const entry of ordered) {
        await installPackage(entry.item, entry.key, { skipRefresh: true });
      }
      await loadInstalled();
    } finally {
      setBatchRunning(false);
    }
  }

  async function handleAddSource(e: React.FormEvent) {
    e.preventDefault();
    if (!newUrl.trim()) return;
    setSavingSource(true);
    try {
      const res = await extensionFetch("/api/ext/extension-updater/sources", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          kind: newKind,
          label: newLabel.trim() || newUrl.trim(),
          url: newUrl.trim(),
          tagPrefix: newKind === "github-release" || newKind === "gitea-release" ? newTagPrefix.trim() || null : null,
          assetPattern: newKind === "github-release" || newKind === "gitea-release" ? newAssetPattern.trim() || null : null,
          enabled: true,
        }),
      });
      if (res.ok) {
        setNewLabel("");
        setNewUrl("");
        setNewTagPrefix("");
        setNewAssetPattern("");
        setShowAddForm(false);
        await loadSources();
        await checkUpdates();
      } else {
        const err = await res.json().catch(() => ({}));
        setCheckErrors([err.message || `Failed to save source (HTTP ${res.status})`]);
      }
    } finally {
      setSavingSource(false);
    }
  }

  async function handleToggleSourceEnabled(src: ExtensionSource) {
    const res = await extensionFetch("/api/ext/extension-updater/sources", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        ...src,
        enabled: !src.enabled,
      }),
    });
    if (res.ok) {
      await loadSources();
      await checkUpdates();
    }
  }

  async function handleDeleteSource(id: string) {
    const res = await extensionFetch(`/api/ext/extension-updater/sources/${encodeURIComponent(id)}`, {
      method: "DELETE",
    });
    if (res.ok) {
      await loadSources();
      await checkUpdates();
    }
  }

  async function handleResetDefaults() {
    const res = await extensionFetch("/api/ext/extension-updater/sources/reset-defaults", { method: "POST" });
    if (res.ok) {
      await loadSources();
      await checkUpdates();
    }
  }

  return (
    <div className="p-6 max-w-6xl mx-auto space-y-6" style={{ color: "var(--text-primary)" }}>
      {/* Header */}
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold flex items-center gap-2">
            <RefreshCw className="w-6 h-6" style={{ color: "var(--color-primary)" }} />
            Extension Updater
          </h1>
          <p className="text-sm mt-1" style={{ color: "var(--text-secondary)" }}>
            Check and update extensions from static rolling URLs, GitHub releases, and external feeds.
          </p>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <button
            onClick={() => setShowAddForm((v) => !v)}
            className="px-3 py-2 rounded-lg text-sm font-medium flex items-center gap-1.5 border cursor-pointer"
            style={{
              background: "var(--bg-surface)",
              borderColor: "var(--border-subtle)",
            }}
          >
            <Plus className="w-4 h-4" />
            Add External Source
          </button>

          <button
            onClick={syncLocalCache}
            disabled={syncingLocal || checking || batchRunning}
            className="px-3 py-2 rounded-lg text-sm font-medium flex items-center gap-1.5 border cursor-pointer disabled:opacity-50"
            style={{
              background: "var(--bg-surface)",
              borderColor: "var(--border-subtle)",
            }}
            title="Run Sync-ExtensionPackages.ps1 to download latest .zip packages locally"
          >
            {syncingLocal ? <Loader2 className="w-4 h-4 animate-spin" /> : <HardDrive className="w-4 h-4" />}
            Sync Local Cache
          </button>

          <button
            onClick={checkUpdates}
            disabled={checking || syncingLocal || batchRunning}
            className="px-3 py-2 rounded-lg text-sm font-medium flex items-center gap-1.5 border cursor-pointer disabled:opacity-50"
            style={{
              background: "var(--bg-surface)",
              borderColor: "var(--border-subtle)",
            }}
          >
            {checking ? <Loader2 className="w-4 h-4 animate-spin" /> : <RefreshCw className="w-4 h-4" />}
            Check All
          </button>

          <button
            onClick={updateAllOutdated}
            disabled={batchRunning || checking || outdatedItems.length === 0}
            className="px-4 py-2 rounded-lg text-sm font-semibold flex items-center gap-1.5 text-white cursor-pointer disabled:opacity-50"
            style={{ background: "var(--color-primary)" }}
          >
            {batchRunning ? <Loader2 className="w-4 h-4 animate-spin" /> : <ArrowUpCircle className="w-4 h-4" />}
            Update All ({outdatedItems.length})
          </button>
        </div>
      </div>

      {/* Add External Source Form */}
      {showAddForm && (
        <form
          onSubmit={handleAddSource}
          className="p-4 rounded-xl border space-y-4"
          style={{ background: "var(--bg-surface)", borderColor: "var(--border-subtle)" }}
        >
          <div className="flex items-center justify-between">
            <h2 className="font-semibold text-base">Add External Extension Source</h2>
            <span className="text-xs" style={{ color: "var(--text-secondary)" }}>
              Supports any repo, static ZIP URL, or manifest feed
            </span>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
            <div>
              <label className="block text-xs mb-1" style={{ color: "var(--text-secondary)" }}>
                Source Type
              </label>
              <select
                value={newKind}
                onChange={(e) => setNewKind(e.target.value as SourceKind)}
                className="w-full px-3 py-2 rounded-lg border text-sm"
                style={{ background: "var(--bg-elevated)", borderColor: "var(--border-subtle)" }}
              >
                <option value="static-zip">Static ZIP URL</option>
                <option value="github-release">GitHub Repo Release</option>
                <option value="gitea-release">Gitea Repo Release (via gt)</option>
                <option value="manifest-feed">Manifest JSON Feed</option>
              </select>
            </div>

            <div>
              <label className="block text-xs mb-1" style={{ color: "var(--text-secondary)" }}>
                Display Label
              </label>
              <input
                type="text"
                value={newLabel}
                onChange={(e) => setNewLabel(e.target.value)}
                placeholder="e.g. External Plugin Name"
                className="w-full px-3 py-2 rounded-lg border text-sm"
                style={{ background: "var(--bg-elevated)", borderColor: "var(--border-subtle)" }}
              />
            </div>

            <div>
              <label className="block text-xs mb-1" style={{ color: "var(--text-secondary)" }}>
                {newKind === "github-release"
                  ? "GitHub Repo (owner/repo or URL)"
                  : newKind === "gitea-release"
                  ? "Gitea Repo (owner/repo or URL)"
                  : "URL"}
              </label>
              <input
                type="text"
                required
                value={newUrl}
                onChange={(e) => setNewUrl(e.target.value)}
                placeholder={
                  newKind === "github-release"
                    ? "owner/repo"
                    : newKind === "gitea-release"
                    ? "https://gitea.tail971353.ts.net/owner/repo"
                    : newKind === "manifest-feed"
                    ? "https://.../latest-manifest.json"
                    : "https://.../extension-latest.zip"
                }
                className="w-full px-3 py-2 rounded-lg border text-sm"
                style={{ background: "var(--bg-elevated)", borderColor: "var(--border-subtle)" }}
              />
            </div>
          </div>

          {(newKind === "github-release" || newKind === "gitea-release") && (
            <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
              <div>
                <label className="block text-xs mb-1" style={{ color: "var(--text-secondary)" }}>
                  Tag Prefix (optional, for multi-extension repos)
                </label>
                <input
                  type="text"
                  value={newTagPrefix}
                  onChange={(e) => setNewTagPrefix(e.target.value)}
                  placeholder="e.g. my-plugin/"
                  className="w-full px-3 py-2 rounded-lg border text-sm"
                  style={{ background: "var(--bg-elevated)", borderColor: "var(--border-subtle)" }}
                />
              </div>
              <div>
                <label className="block text-xs mb-1" style={{ color: "var(--text-secondary)" }}>
                  Asset Pattern (optional)
                </label>
                <input
                  type="text"
                  value={newAssetPattern}
                  onChange={(e) => setNewAssetPattern(e.target.value)}
                  placeholder="e.g. *.zip or com.example.*.zip"
                  className="w-full px-3 py-2 rounded-lg border text-sm"
                  style={{ background: "var(--bg-elevated)", borderColor: "var(--border-subtle)" }}
                />
              </div>
            </div>
          )}

          <div className="flex justify-end gap-2">
            <button
              type="button"
              onClick={() => setShowAddForm(false)}
              className="px-3 py-1.5 rounded-lg text-sm border cursor-pointer"
              style={{ borderColor: "var(--border-subtle)" }}
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={savingSource}
              className="px-4 py-1.5 rounded-lg text-sm font-medium text-white cursor-pointer disabled:opacity-50"
              style={{ background: "var(--color-primary)" }}
            >
              {savingSource ? "Saving..." : "Save Source"}
            </button>
          </div>
        </form>
      )}

      {/* Errors */}
      {checkErrors.length > 0 && (
        <div className="p-3 rounded-lg border border-red-500/40 bg-red-500/10 text-sm space-y-1">
          {checkErrors.map((err, i) => (
            <div key={i} className="flex items-center gap-2 text-red-400">
              <AlertCircle className="w-4 h-4 shrink-0" />
              <span>{err}</span>
            </div>
          ))}
        </div>
      )}

      {/* Tracked Sources */}
      <div
        className="p-4 rounded-xl border space-y-3"
        style={{ background: "var(--bg-surface)", borderColor: "var(--border-subtle)" }}
      >
        <div className="flex items-center justify-between">
          <h2 className="font-semibold text-sm uppercase tracking-wider" style={{ color: "var(--text-secondary)" }}>
            Tracked Sources ({sources.length})
          </h2>
          <button
            onClick={handleResetDefaults}
            className="text-xs flex items-center gap-1 cursor-pointer hover:underline"
            style={{ color: "var(--text-secondary)" }}
          >
            <RotateCcw className="w-3.5 h-3.5" />
            Ensure Default Feed
          </button>
        </div>

        <div className="divide-y" style={{ borderColor: "var(--border-subtle)" }}>
          {sources.map((src) => (
            <div
              key={src.id}
              className={`py-2.5 flex items-center justify-between gap-3 ${!src.enabled ? "opacity-50" : ""}`}
            >
              <div className="flex items-center gap-2.5 min-w-0">
                {src.kind === "github-release" ? (
                  <GitBranch className="w-4 h-4 shrink-0" style={{ color: "var(--text-secondary)" }} />
                ) : src.kind === "manifest-feed" ? (
                  <FileJson className="w-4 h-4 shrink-0" style={{ color: "var(--text-secondary)" }} />
                ) : (
                  <Globe className="w-4 h-4 shrink-0" style={{ color: "var(--text-secondary)" }} />
                )}
                <div className="min-w-0">
                  <div className="text-sm font-medium truncate">{src.label}</div>
                  <div className="text-xs truncate" style={{ color: "var(--text-secondary)" }}>
                    {src.url}
                    {src.tagPrefix ? ` • tag: ${src.tagPrefix}*` : ""}
                  </div>
                </div>
              </div>
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  onClick={() => handleToggleSourceEnabled(src)}
                  className="px-2 py-1 rounded text-xs border cursor-pointer"
                  style={{
                    background: src.enabled ? "var(--bg-elevated)" : "transparent",
                    borderColor: "var(--border-subtle)",
                  }}
                  title={src.enabled ? "Disable source" : "Enable source"}
                >
                  {src.enabled ? "Enabled" : "Disabled"}
                </button>
                <button
                  type="button"
                  onClick={() => handleDeleteSource(src.id)}
                  title="Remove source"
                  aria-label={`Remove ${src.label}`}
                  className="p-1.5 rounded hover:bg-red-500/15 text-red-400 cursor-pointer"
                >
                  <Trash2 className="w-4 h-4" />
                </button>
              </div>
            </div>
          ))}
        </div>
      </div>

      {/* Discovered Extensions */}
      <div
        className="rounded-xl border overflow-hidden"
        style={{ background: "var(--bg-surface)", borderColor: "var(--border-subtle)" }}
      >
        {/* Filter & Search Toolbar */}
        <div
          className="px-4 py-3 border-b flex flex-wrap items-center justify-between gap-3"
          style={{ borderColor: "var(--border-subtle)" }}
        >
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="font-semibold text-sm uppercase tracking-wider mr-2" style={{ color: "var(--text-secondary)" }}>
              Extensions ({filteredItems.length}/{enrichedItems.length})
            </h2>

            {(
              [
                { id: "all", label: "All" },
                { id: "updates", label: `Updates (${outdatedItems.length})` },
                { id: "installed", label: "Installed" },
                { id: "available", label: "Not Installed" },
              ] as const
            ).map((tab) => (
              <button
                key={tab.id}
                type="button"
                onClick={() => setStatusFilter(tab.id)}
                className="px-2.5 py-1 rounded-md text-xs font-medium cursor-pointer border"
                style={
                  statusFilter === tab.id
                    ? { background: "var(--color-primary)", color: "#fff", borderColor: "transparent" }
                    : { background: "var(--bg-elevated)", borderColor: "var(--border-subtle)" }
                }
              >
                {tab.label}
              </button>
            ))}
          </div>

          <div className="flex flex-wrap items-center gap-2">
            <select
              value={categoryFilter}
              onChange={(e) => setCategoryFilter(e.target.value as CategoryFilter)}
              className="px-2.5 py-1.5 rounded-lg border text-xs"
              style={{ background: "var(--bg-elevated)", borderColor: "var(--border-subtle)" }}
            >
              <option value="all">All Categories</option>
              <option value="downloaders">Downloaders</option>
              <option value="scrapers">Scrapers</option>
              <option value="tools">Tools & UI</option>
            </select>

            <div className="relative">
              <Search
                className="w-3.5 h-3.5 absolute left-2.5 top-1/2 -translate-y-1/2"
                style={{ color: "var(--text-secondary)" }}
              />
              <input
                type="text"
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                placeholder="Filter extensions..."
                className="pl-8 pr-3 py-1.5 rounded-lg border text-xs w-48"
                style={{ background: "var(--bg-elevated)", borderColor: "var(--border-subtle)" }}
              />
            </div>
          </div>
        </div>

        {loading || (checking && enrichedItems.length === 0) ? (
          <div className="p-8 text-center text-sm flex items-center justify-center gap-2" style={{ color: "var(--text-secondary)" }}>
            <Loader2 className="w-4 h-4 animate-spin" />
            Checking sources...
          </div>
        ) : filteredItems.length === 0 ? (
          <div className="p-8 text-center text-sm" style={{ color: "var(--text-secondary)" }}>
            {enrichedItems.length === 0
              ? "No extensions discovered yet. Add a source or publish a release to populate the feed."
              : "No extensions match your current filter."}
          </div>
        ) : (
          <div className="divide-y" style={{ borderColor: "var(--border-subtle)" }}>
            {filteredItems.map(({ key, item, installed, hasUpdate, isUpToDate, category }) => {
              const log = statusLog[key];
              const isInstalling = updatingKey === key;
              return (
                <div key={key} className="p-4 flex flex-wrap items-center justify-between gap-4">
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="font-semibold text-sm">{item.name}</span>
                      {item.extensionId && (
                        <code className="text-xs px-1.5 py-0.5 rounded" style={{ background: "var(--bg-elevated)" }}>
                          {item.extensionId}
                        </code>
                      )}
                      <span
                        className="text-[11px] px-2 py-0.5 rounded capitalize"
                        style={{ background: "var(--bg-elevated)", color: "var(--text-secondary)" }}
                      >
                        {category === "tools" ? "Tool / UI" : category.slice(0, -1)}
                      </span>
                      {hasUpdate && (
                        <span className="text-xs px-2 py-0.5 rounded-full bg-amber-500/20 text-amber-400 font-medium">
                          Update: {installed?.version} → {item.remoteVersion}
                        </span>
                      )}
                      {isUpToDate && (
                        <span className="text-xs px-2 py-0.5 rounded-full bg-emerald-500/15 text-emerald-400 flex items-center gap-1">
                          <CheckCircle2 className="w-3 h-3" />
                          v{installed?.version}
                        </span>
                      )}
                      {!installed && item.remoteVersion && (
                        <span className="text-xs px-2 py-0.5 rounded-full bg-blue-500/15 text-blue-400">
                          Available: v{item.remoteVersion}
                        </span>
                      )}
                      {item.localCached && (
                        <span className="text-[11px] px-2 py-0.5 rounded-full bg-indigo-500/15 text-indigo-300">
                          Local ZIP
                        </span>
                      )}
                      {item.minCoveVersion && (
                        <span className="text-[11px]" style={{ color: "var(--text-secondary)" }}>
                          Cove {item.minCoveVersion}+
                        </span>
                      )}
                    </div>

                    {item.description && (
                      <p className="text-xs mt-1 line-clamp-1" style={{ color: "var(--text-secondary)" }}>
                        {item.description}
                      </p>
                    )}

                    <div className="text-xs mt-1 flex flex-wrap items-center gap-3" style={{ color: "var(--text-secondary)" }}>
                      <span>Source: {item.sourceLabel}</span>
                      {log && (
                        <span className={log.ok ? "text-emerald-400" : "text-red-400"}>
                          • {log.message}
                        </span>
                      )}
                      {item.error && <span className="text-red-400">• {item.error}</span>}
                    </div>
                  </div>

                  <div>
                    <button
                      onClick={() => installPackage(item, key)}
                      disabled={isInstalling || batchRunning || (!item.localPackageUrl && !item.downloadUrl)}
                      className="px-3 py-1.5 rounded-lg text-xs font-medium flex items-center gap-1.5 cursor-pointer disabled:opacity-50 border"
                      style={
                        hasUpdate || !installed
                          ? { background: "var(--color-primary)", color: "#fff", borderColor: "transparent" }
                          : { background: "var(--bg-elevated)", borderColor: "var(--border-subtle)" }
                      }
                    >
                      {isInstalling ? (
                        <Loader2 className="w-3.5 h-3.5 animate-spin" />
                      ) : (
                        <Download className="w-3.5 h-3.5" />
                      )}
                      {hasUpdate ? "Update" : installed ? "Reinstall" : "Install"}
                    </button>
                  </div>
                </div>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
}
