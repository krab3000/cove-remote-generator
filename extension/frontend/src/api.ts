import { extensionFetch } from "@cove/runtime/api";

export const EXTENSION_ID = "com.cove.remote-heavylifter";
const BASE = `/api/ext/${EXTENSION_ID}`;

/** "cove-dials" when the worker has a URL Cove connects to; "worker-dials" when Cove waits for the worker. */
export type WorkerConnection = "cove-dials" | "worker-dials";

export interface WorkerView {
  id: string;
  name: string;
  workerTokenId: string;
  hasToken: boolean;
  tokenHint: string | null;
  url: string | null;
  coveUrlOverride: string | null;
  enabled: boolean;
  maxConcurrency: number;
  connection: WorkerConnection;
}

export interface WorkerInput {
  id?: string | null;
  name: string;
  /** null keeps the stored token (or hash). */
  token: string | null;
  url: string | null;
  coveUrlOverride: string | null;
  enabled: boolean;
  maxConcurrency: number;
}

export interface PendingWorker {
  workerTokenId: string;
  name: string | null;
  version: string | null;
  remoteAddress: string | null;
  firstSeen: string;
  lastSeen: string;
}

export type WorkerState = "live" | "offline" | "unauthorized" | "incompatible" | "disabled" | "waiting";

export interface WorkerHealth {
  id: string;
  name: string;
  enabled: boolean;
  state: WorkerState;
  live: boolean;
  connection: WorkerConnection;
  workerVersion: string | null;
  ffmpegVersion: string | null;
  encoder: string | null;
  capacity: number | null;
  running: number | null;
  remoteAddress: string | null;
  connectedSince: string | null;
  error: string | null;
  checkedAt: string;
}

export interface WorkerTestResult {
  ok: boolean;
  message: string;
}

export interface WorkerSettings {
  coveUrlForWorkers: string | null;
  coveAuthEnabled: boolean;
}

export interface GenerateOptions {
  workerIds: string[];
  paths: string[];
  cover: boolean;
  preview: boolean;
  sprite: boolean;
  /** Video perceptual hash (Cove's "phash" fingerprint); older stored options may lack it. */
  phash?: boolean;
  overwrite: boolean;
  /** Sprite tile width in px; older stored options may lack it. */
  spriteWidth?: number;
}

export const SPRITE_WIDTH = { default: 160, min: 16, max: 1920 } as const;

export interface LibraryFolder {
  name: string;
  path: string;
  hasChildren: boolean;
}

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly code?: string,
    readonly errors?: string[],
  ) {
    super(message);
  }
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await extensionFetch(url, {
    ...init,
    headers: init?.body ? { "Content-Type": "application/json", ...init.headers } : init?.headers,
  });
  if (!response.ok) {
    let body: { code?: string; message?: string; errors?: string[]; title?: string } = {};
    try {
      body = await response.json();
    } catch {
      // not JSON
    }
    const fallback =
      response.status === 403
        ? "You do not have permission to do this."
        : `Request failed (${response.status} ${response.statusText})`;
    throw new ApiError(body.message ?? body.title ?? fallback, response.status, body.code, body.errors);
  }
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

export const api = {
  workers: () => request<WorkerView[]>(`${BASE}/workers`),
  saveWorkers: (workers: WorkerInput[]) =>
    request<WorkerView[]>(`${BASE}/workers`, { method: "PUT", body: JSON.stringify(workers) }),
  pendingWorkers: () => request<PendingWorker[]>(`${BASE}/workers/pending`),
  trustWorker: (workerTokenId: string) =>
    request<WorkerView>(`${BASE}/workers/pending/${encodeURIComponent(workerTokenId)}/trust`, { method: "POST" }),
  dismissWorker: (workerTokenId: string) =>
    request<void>(`${BASE}/workers/pending/${encodeURIComponent(workerTokenId)}`, { method: "DELETE" }),
  testWorker: (id: string) => request<WorkerTestResult>(`${BASE}/workers/${id}/test`, { method: "POST" }),
  health: (refresh = false) => request<WorkerHealth[]>(`${BASE}/workers/health?refresh=${refresh}`),
  settings: () => request<WorkerSettings>(`${BASE}/settings`),
  saveSettings: (settings: Pick<WorkerSettings, "coveUrlForWorkers">) =>
    request<WorkerSettings>(`${BASE}/settings`, { method: "PUT", body: JSON.stringify({ ...settings, coveAuthEnabled: false }) }),
  options: () => request<GenerateOptions>(`${BASE}/generate/options`),
  generate: (options: GenerateOptions) =>
    request<{ jobId: string }>(`${BASE}/generate`, { method: "POST", body: JSON.stringify(options) }),
  libraryFolders: (path: string) =>
    request<LibraryFolder[]>(`/api/metadata/library-folders?path=${encodeURIComponent(path)}&probeChildren=true`),
};

export const queryKeys = {
  workers: ["remote-heavylifter", "workers"] as const,
  pending: ["remote-heavylifter", "pending"] as const,
  settings: ["remote-heavylifter", "settings"] as const,
  health: ["remote-heavylifter", "health"] as const,
  options: ["remote-heavylifter", "options"] as const,
  folders: (path: string) => ["remote-heavylifter", "folders", path] as const,
};
