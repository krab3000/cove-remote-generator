import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Loader2, Plus, RefreshCw, Save, Trash2, Zap } from "lucide-react";
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
import { Button, Checkbox, ExpandableCard, Message, Section, StateBadge, describeLoad, inputClass } from "./ui";

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

  const intro =
    "Generation servers run the heavy ffmpeg work. Each one must see the same media files as Cove; map every Cove library folder to the path the server uses for it.";

  if (serversQuery.isLoading || serversQuery.error) {
    return (
      <Section title="Generation servers" description={intro}>
        {serversQuery.error ? (
          <Message tone="error">{(serversQuery.error as Error).message}</Message>
        ) : (
          <Message tone="muted">Loading generation servers…</Message>
        )}
      </Section>
    );
  }

  return (
    <Section title="Generation servers" description={intro}>
      <div className="space-y-3">
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
    </Section>
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
  const [testMessage, setTestMessage] = useState<StatusMessage | null>(null);
  const testMutation = useMutation({
    mutationFn: () => api.testServer(toInput(draft)),
    onMutate: () => setTestMessage(null),
    onSuccess: (result) => setTestMessage(summarizeTest(result)),
    onError: (error) => setTestMessage({ type: "error", text: `Test failed: ${(error as Error).message}` }),
  });
  // A result only describes the settings it ran with.
  const change = (patch: Partial<Draft>) => {
    setTestMessage(null);
    onChange(patch);
  };
  const load = describeLoad(health);
  // Saved servers start collapsed; a freshly added one opens so it can be filled in.
  const [expanded, setExpanded] = useState(draft.id === null);

  const setMapping = (index: number, patch: Partial<PathMapping>) =>
    change({ mappings: draft.mappings.map((m, i) => (i === index ? { ...m, ...patch } : m)) });

  return (
    <ExpandableCard
      label={draft.name || "New server"}
      description={
        <span className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1">
          {draft.id ? <StateBadge health={health} /> : <span className="text-muted">Not saved</span>}
          {load ? <span className="text-muted">{load}</span> : null}
          {!expanded && draft.baseUrl ? <span className="truncate text-muted">{draft.baseUrl}</span> : null}
          {!expanded && !draft.enabled ? <span className="text-muted">disabled</span> : null}
        </span>
      }
      actions={
        <div className="ml-3 flex shrink-0 items-center gap-2">
          <Button onClick={() => testMutation.mutate()} disabled={testMutation.isPending} title="Connect and check the path mappings">
            {testMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Zap className="h-4 w-4" />}
            Test
          </Button>
          <Button variant="danger" onClick={onRemove} aria-label={`Remove ${draft.name || "server"}`}>
            <Trash2 className="h-4 w-4" />
          </Button>
        </div>
      }
      expanded={expanded}
      onToggleExpand={() => setExpanded((open) => !open)}
      statusMessage={testMessage ?? (health?.error && draft.id ? { type: "error", text: health.error } : null)}
    >
      <div className="space-y-3">
        <div className="grid gap-3 sm:grid-cols-2">
          <label className="block space-y-1">
            <span className="text-xs text-secondary">Name</span>
            <input className={inputClass} value={draft.name} onChange={(e) => change({ name: e.target.value })} placeholder="gpu-box" />
          </label>
          <label className="block space-y-1">
            <span className="text-xs text-secondary">URL</span>
            <input
              className={inputClass}
              value={draft.baseUrl}
              onChange={(e) => change({ baseUrl: e.target.value })}
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
              onChange={(e) => change({ apiKey: e.target.value })}
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
              onChange={(e) => change({ maxConcurrency: Math.max(1, Math.min(64, Number(e.target.value) || 1)) })}
            />
          </label>
        </div>
        <Checkbox label="Enabled" checked={draft.enabled} onChange={(enabled) => change({ enabled })} />

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
                onClick={() => change({ mappings: draft.mappings.filter((_, i) => i !== index) })}
                aria-label="Remove mapping"
              >
                <Trash2 className="h-4 w-4" />
              </button>
            </div>
          ))}
          <button
            type="button"
            onClick={() => change({ mappings: [...draft.mappings, { covePrefix: "", remotePrefix: "" }] })}
            className="text-[11px] text-accent hover:text-accent-hover"
          >
            + Add mapping
          </button>
        </div>
      </div>
    </ExpandableCard>
  );
}

type StatusMessage = { type: "success" | "error"; text: string };

function summarizeTest(result: ServerTestResult): StatusMessage {
  const { health } = result;
  if (!health.live) return { type: "error", text: `Test failed: ${health.error ?? `server is ${health.state}`}` };

  for (const check of result.mappings) {
    const where = `${check.covePrefix} → ${check.remotePrefix}`;
    if (check.error) return { type: "error", text: `Test failed: ${where}: ${check.error}` };
    const bad = check.samples.filter((s) => !(s.remotePath && s.allowed && s.exists && s.readable));
    if (bad.length > 0) {
      const first = bad[0];
      const why = !first.remotePath
        ? "not mapped"
        : !first.allowed
          ? "outside the server's media roots"
          : !first.exists
            ? "not found on the server"
            : "not readable";
      return {
        type: "error",
        text: `Test failed: ${where}: ${bad.length} of ${check.samples.length} sample videos ${why} (e.g. ${first.remotePath ?? first.covePath}).`,
      };
    }
  }

  const latency = health.latencyMs != null ? ` in ${health.latencyMs} ms` : "";
  const mappings = result.mappings.length === 1 ? "1 path mapping" : `${result.mappings.length} path mappings`;
  return { type: "success", text: `Test passed: connected${latency}, ${mappings} checked.` };
}
