import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { CheckCircle2, Loader2, Plus, RefreshCw, Save, Trash2, XCircle, Zap } from "lucide-react";
import { useAppConfig } from "@cove/runtime/components";
import {
  ApiError,
  api,
  queryKeys,
  type PathMapping,
  type ServerHealth,
  type ServerInput,
  type ServerTestResult,
  type ServerView,
} from "./api";
import { Button, Card, Checkbox, Message, StateBadge, describeLoad, inputClass } from "./ui";

interface Draft {
  key: string;
  id: string | null;
  name: string;
  baseUrl: string;
  apiKey: string;
  hasApiKey: boolean;
  apiKeyHint: string | null;
  enabled: boolean;
  maxConcurrency: number;
  mappings: PathMapping[];
}

let draftCounter = 0;
const newKey = () => `draft-${++draftCounter}`;

function toDraft(server: ServerView): Draft {
  return { ...server, key: server.id, apiKey: "" };
}

function toInput(draft: Draft): ServerInput {
  return {
    id: draft.id,
    name: draft.name,
    baseUrl: draft.baseUrl,
    // Empty means "keep the stored key" for a saved server.
    apiKey: draft.apiKey.length > 0 || !draft.hasApiKey ? draft.apiKey : null,
    enabled: draft.enabled,
    maxConcurrency: draft.maxConcurrency,
    mappings: draft.mappings,
  };
}

export function RemoteServersPanel() {
  const queryClient = useQueryClient();
  const serversQuery = useQuery({ queryKey: queryKeys.servers, queryFn: api.servers });
  const healthQuery = useQuery({
    queryKey: queryKeys.health,
    queryFn: () => api.health(false),
    refetchInterval: 10_000,
  });
  const [drafts, setDrafts] = useState<Draft[]>([]);
  const [dirty, setDirty] = useState(false);
  const [saveErrors, setSaveErrors] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    if (serversQuery.data && !dirty) setDrafts(serversQuery.data.map(toDraft));
  }, [serversQuery.data, dirty]);

  const healthById = useMemo(
    () => new Map((healthQuery.data ?? []).map((health) => [health.id, health] as const)),
    [healthQuery.data],
  );

  const save = useMutation({
    mutationFn: () => api.saveServers(drafts.map(toInput)),
    onSuccess: (servers) => {
      queryClient.setQueryData(queryKeys.servers, servers);
      setDrafts(servers.map(toDraft));
      setDirty(false);
      setSaveErrors([]);
      setSaved(true);
      void queryClient.invalidateQueries({ queryKey: queryKeys.health });
    },
    onError: (error) => {
      setSaved(false);
      setSaveErrors(error instanceof ApiError && error.errors?.length ? error.errors : [String((error as Error).message)]);
    },
  });

  const update = (key: string, patch: Partial<Draft>) => {
    setDrafts((current) => current.map((draft) => (draft.key === key ? { ...draft, ...patch } : draft)));
    setDirty(true);
    setSaved(false);
  };

  const add = () => {
    setDrafts((current) => [
      ...current,
      {
        key: newKey(),
        id: null,
        name: "",
        baseUrl: "http://",
        apiKey: "",
        hasApiKey: false,
        apiKeyHint: null,
        enabled: true,
        maxConcurrency: 2,
        mappings: [{ covePrefix: "", remotePrefix: "" }],
      },
    ]);
    setDirty(true);
  };

  const remove = (key: string) => {
    setDrafts((current) => current.filter((draft) => draft.key !== key));
    setDirty(true);
  };

  if (serversQuery.isLoading) {
    return <Message tone="muted">Loading generation servers…</Message>;
  }
  if (serversQuery.error) {
    return <Message tone="error">{(serversQuery.error as Error).message}</Message>;
  }

  return (
    <div className="space-y-3">
      <p className="text-xs text-secondary">
        Generation servers run the heavy ffmpeg work. Each one must see the same media files as Cove; map every Cove
        library folder to the path the server uses for it.
      </p>

      {drafts.length === 0 ? <Message tone="muted">No generation servers yet.</Message> : null}

      {drafts.map((draft) => (
        <ServerCard
          key={draft.key}
          draft={draft}
          health={draft.id ? healthById.get(draft.id) : undefined}
          onChange={(patch) => update(draft.key, patch)}
          onRemove={() => remove(draft.key)}
        />
      ))}

      <div className="flex flex-wrap items-center gap-2">
        <Button onClick={add}>
          <Plus className="h-4 w-4" />
          Add server
        </Button>
        <Button variant="primary" onClick={() => save.mutate()} disabled={!dirty || save.isPending}>
          {save.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
          Save servers
        </Button>
        <Button
          onClick={() => void api.health(true).then((data) => queryClient.setQueryData(queryKeys.health, data))}
          title="Check every server now"
        >
          <RefreshCw className="h-4 w-4" />
          Refresh status
        </Button>
        {dirty ? <Message tone="muted">Unsaved changes</Message> : null}
        {saved && !dirty ? <Message tone="success">Saved.</Message> : null}
      </div>
      {saveErrors.length > 0 ? (
        <ul className="list-disc space-y-0.5 pl-5">
          {saveErrors.map((error) => (
            <li key={error} className="text-xs text-red-400">
              {error}
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}

function ServerCard({
  draft,
  health,
  onChange,
  onRemove,
}: {
  draft: Draft;
  health?: ServerHealth;
  onChange: (patch: Partial<Draft>) => void;
  onRemove: () => void;
}) {
  const { config } = useAppConfig();
  const libraryRoots = (config?.covePaths ?? []).filter((p) => !p.excludeVideo).map((p) => p.path.trim()).filter(Boolean);
  const listId = `rh-roots-${draft.key}`;
  const [test, setTest] = useState<ServerTestResult | null>(null);
  const testMutation = useMutation({
    mutationFn: () => api.testServer(toInput(draft)),
    onSuccess: setTest,
    onError: () => setTest(null),
  });
  const load = describeLoad(health);

  const setMapping = (index: number, patch: Partial<PathMapping>) =>
    onChange({ mappings: draft.mappings.map((m, i) => (i === index ? { ...m, ...patch } : m)) });

  return (
    <Card>
      <div className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div className="flex min-w-0 items-center gap-3">
            <h4 className="truncate text-sm font-medium text-foreground">{draft.name || "New server"}</h4>
            {draft.id ? <StateBadge health={health} /> : <span className="text-xs text-muted">Not saved</span>}
            {load ? <span className="text-xs text-muted">{load}</span> : null}
          </div>
          <div className="flex items-center gap-2">
            <Button onClick={() => testMutation.mutate()} disabled={testMutation.isPending} title="Connect and check the path mappings">
              {testMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Zap className="h-4 w-4" />}
              Test
            </Button>
            <Button variant="danger" onClick={onRemove} aria-label={`Remove ${draft.name || "server"}`}>
              <Trash2 className="h-4 w-4" />
            </Button>
          </div>
        </div>
        {health?.error && draft.id ? <Message tone="error">{health.error}</Message> : null}

        <div className="grid gap-3 sm:grid-cols-2">
          <label className="block space-y-1">
            <span className="text-xs text-secondary">Name</span>
            <input className={inputClass} value={draft.name} onChange={(e) => onChange({ name: e.target.value })} placeholder="gpu-box" />
          </label>
          <label className="block space-y-1">
            <span className="text-xs text-secondary">URL</span>
            <input
              className={inputClass}
              value={draft.baseUrl}
              onChange={(e) => onChange({ baseUrl: e.target.value })}
              placeholder="http://192.168.1.20:8750"
            />
          </label>
          <label className="block space-y-1">
            <span className="text-xs text-secondary">API key</span>
            <input
              className={inputClass}
              type="password"
              autoComplete="new-password"
              value={draft.apiKey}
              onChange={(e) => onChange({ apiKey: e.target.value })}
              placeholder={draft.hasApiKey ? `Unchanged (${draft.apiKeyHint ?? "set"})` : "HL_API_KEYS value"}
            />
          </label>
          <label className="block space-y-1">
            <span className="text-xs text-secondary">Max parallel videos</span>
            <input
              className={inputClass}
              type="number"
              min={1}
              max={64}
              value={draft.maxConcurrency}
              onChange={(e) => onChange({ maxConcurrency: Math.max(1, Math.min(64, Number(e.target.value) || 1)) })}
            />
          </label>
        </div>
        <Checkbox label="Enabled" checked={draft.enabled} onChange={(enabled) => onChange({ enabled })} />

        <div className="space-y-2 rounded-xl border border-border/60 bg-surface/60 p-3">
          <p className="text-xs font-medium text-foreground">Path mappings</p>
          <p className="text-[11px] text-muted">
            Cove path → the same folder as this server sees it. The longest matching Cove path wins.
          </p>
          <datalist id={listId}>
            {libraryRoots.map((root) => (
              <option key={root} value={root} />
            ))}
          </datalist>
          {draft.mappings.map((mapping, index) => (
            <div key={index} className="flex items-center gap-2">
              <input
                className={inputClass}
                list={listId}
                value={mapping.covePrefix}
                onChange={(e) => setMapping(index, { covePrefix: e.target.value })}
                placeholder="Cove path, e.g. D:/media"
              />
              <span className="text-muted">→</span>
              <input
                className={inputClass}
                value={mapping.remotePrefix}
                onChange={(e) => setMapping(index, { remotePrefix: e.target.value })}
                placeholder="Server path, e.g. /mnt/media"
              />
              <button
                type="button"
                className="text-muted hover:text-foreground"
                onClick={() => onChange({ mappings: draft.mappings.filter((_, i) => i !== index) })}
                aria-label="Remove mapping"
              >
                <Trash2 className="h-4 w-4" />
              </button>
            </div>
          ))}
          <button
            type="button"
            onClick={() => onChange({ mappings: [...draft.mappings, { covePrefix: "", remotePrefix: "" }] })}
            className="text-[11px] text-accent hover:text-accent-hover"
          >
            + Add mapping
          </button>
        </div>

        {testMutation.error ? <Message tone="error">{(testMutation.error as Error).message}</Message> : null}
        {test ? <TestResult result={test} /> : null}
      </div>
    </Card>
  );
}

function TestResult({ result }: { result: ServerTestResult }) {
  const { health } = result;
  return (
    <div className="space-y-2 rounded-xl border border-border/60 bg-surface/60 p-3">
      <div className="flex flex-wrap items-center gap-3">
        <StateBadge health={health} />
        {health.ffmpegVersion ? <span className="truncate text-xs text-muted">{health.ffmpegVersion}</span> : null}
      </div>
      {health.error ? <Message tone="error">{health.error}</Message> : null}
      {result.mediaRoots.length > 0 ? (
        <p className="text-[11px] text-muted">Server media roots: {result.mediaRoots.join(", ")}</p>
      ) : null}
      {result.mappings.map((check) => (
        <div key={`${check.covePrefix}→${check.remotePrefix}`} className="space-y-1">
          <p className="text-xs text-foreground">
            {check.covePrefix} → {check.remotePrefix}
          </p>
          {check.error ? <Message tone="error">{check.error}</Message> : null}
          {check.samples.map((sample) => {
            const ok = sample.allowed && sample.exists && sample.readable;
            const why = !sample.remotePath
              ? "not mapped"
              : !sample.allowed
                ? "outside the server's media roots"
                : !sample.exists
                  ? "not found on the server"
                  : !sample.readable
                    ? "not readable"
                    : "ok";
            return (
              <div key={sample.covePath} className="flex items-start gap-1.5 pl-2 text-[11px]">
                {ok ? (
                  <CheckCircle2 className="mt-0.5 h-3.5 w-3.5 shrink-0 text-green-400" />
                ) : (
                  <XCircle className="mt-0.5 h-3.5 w-3.5 shrink-0 text-red-400" />
                )}
                <span className="break-all text-secondary" title={sample.covePath}>
                  {sample.remotePath ?? sample.covePath}
                  {ok ? null : <span className="text-red-400"> — {why}</span>}
                </span>
              </div>
            );
          })}
        </div>
      ))}
    </div>
  );
}
