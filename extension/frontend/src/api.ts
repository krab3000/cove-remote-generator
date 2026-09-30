import { extensionFetch } from "@cove/runtime/api";

export const EXTENSION_ID = "com.cove.remote-heavylifter";
const BASE = `/api/ext/${EXTENSION_ID}`;

export interface PathMapping {
  covePrefix: string;
  remotePrefix: string;
}

export interface ServerView {
  id: string;
  name: string;
  baseUrl: string;
  hasApiKey: boolean;
  apiKeyHint: string | null;
  enabled: boolean;
  maxConcurrency: number;
  mappings: PathMapping[];
}

export interface ServerInput {
  id?: string | null;
  name: string;
  baseUrl: string;
  /** null keeps the stored key. */
  apiKey: string | null;
  enabled: boolean;
  maxConcurrency: number;
  mappings: PathMapping[];
}

export type ServerState = "live" | "offline" | "unauthorized" | "incompatible" | "disabled";

export interface ServerHealth {
  id: string;
  name: string;
  enabled: boolean;
  state: ServerState;
  live: boolean;
  latencyMs: number | null;
  serverVersion: string | null;
  ffmpegVersion: string | null;
  encoder: string | null;
  capacity: number | null;
  running: number | null;
  queued: number | null;
  diskFreeBytes: number | null;
  error: string | null;
  checkedAt: string;
}

export interface MappingSample {
  covePath: string;
  remotePath: string | null;
  allowed: boolean;
  exists: boolean;
  readable: boolean;
}

export interface MappingCheck {
  covePrefix: string;
  remotePrefix: string;
  samples: MappingSample[];
  error: string | null;
}

export interface ServerTestResult {
  health: ServerHealth;
  mappings: MappingCheck[];
  mediaRoots: string[];
}

export interface GenerateOptions {
  serverIds: string[];
  paths: string[];
  cover: boolean;
  preview: boolean;
  sprite: boolean;
  overwrite: boolean;
}

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
  servers: () => request<ServerView[]>(`${BASE}/servers`),
  saveServers: (servers: ServerInput[]) =>
    request<ServerView[]>(`${BASE}/servers`, { method: "PUT", body: JSON.stringify(servers) }),
  testServer: (server: ServerInput) =>
    request<ServerTestResult>(`${BASE}/servers/test`, { method: "POST", body: JSON.stringify(server) }),
  health: (refresh = false) => request<ServerHealth[]>(`${BASE}/servers/health?refresh=${refresh}`),
  options: () => request<GenerateOptions>(`${BASE}/generate/options`),
  generate: (options: GenerateOptions) =>
    request<{ jobId: string }>(`${BASE}/generate`, { method: "POST", body: JSON.stringify(options) }),
  libraryFolders: (path: string) =>
    request<LibraryFolder[]>(`/api/metadata/library-folders?path=${encodeURIComponent(path)}&probeChildren=true`),
};

export const queryKeys = {
  servers: ["remote-heavylifter", "servers"] as const,
  health: ["remote-heavylifter", "health"] as const,
  options: ["remote-heavylifter", "options"] as const,
  folders: (path: string) => ["remote-heavylifter", "folders", path] as const,
};
