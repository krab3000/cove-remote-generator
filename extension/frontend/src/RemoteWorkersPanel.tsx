import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Check, Loader2, Plus, RefreshCw, Save, Trash2, X, Zap } from "lucide-react";
import {
  ApiError,
  api,
  queryKeys,
  type PendingWorker,
  type WorkerHealth,
  type WorkerInput,
  type WorkerView,
} from "./api";
import { Button, Checkbox, ExpandableCard, Message, Section, StateBadge, describeLoad, inputClass } from "./ui";

interface Draft {
  key: string;
  id: string | null;
  name: string;
  token: string;
  hasToken: boolean;
  tokenHint: string | null;
  workerTokenId: string | null;
  url: string;
  coveUrlOverride: string;
  enabled: boolean;
  maxConcurrency: number;
}

let draftCounter = 0;
const newKey = () => `draft-${++draftCounter}`;

function toDraft(worker: WorkerView): Draft {
  return {
    key: worker.id,
    id: worker.id,
    name: worker.name,
    token: "",
    hasToken: worker.hasToken,
    tokenHint: worker.tokenHint,
    workerTokenId: worker.workerTokenId,
    url: worker.url ?? "",
    coveUrlOverride: worker.coveUrlOverride ?? "",
    enabled: worker.enabled,
    maxConcurrency: worker.maxConcurrency,
  };
}

function toInput(draft: Draft): WorkerInput {
  return {
    id: draft.id,
    name: draft.name,
    // Empty keeps the stored token (or, for a worker trusted from the pending list, its hash).
    token: draft.token.length > 0 || !draft.id ? draft.token : null,
    url: draft.url.trim() || null,
    coveUrlOverride: draft.coveUrlOverride.trim() || null,
    enabled: draft.enabled,
    maxConcurrency: draft.maxConcurrency,
  };
}

function connectionLabel(url: string): string {
  return url.trim() ? `Cove connects to ${url.trim()}` : "Waits for the worker to connect";
}

function timeAgo(iso: string): string {
  const seconds = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
  if (seconds < 90) return "just now";
  if (seconds < 90 * 60) return `${Math.round(seconds / 60)} min ago`;
  if (seconds < 36 * 3600) return `${Math.round(seconds / 3600)} h ago`;
  return `${Math.round(seconds / 86400)} d ago`;
}

export function RemoteWorkersPanel() {
  const queryClient = useQueryClient();
  const workersQuery = useQuery({ queryKey: queryKeys.workers, queryFn: api.workers });
  const healthQuery = useQuery({
    queryKey: queryKeys.health,
    queryFn: () => api.health(false),
    refetchInterval: 5_000,
  });
  const pendingQuery = useQuery({ queryKey: queryKeys.pending, queryFn: api.pendingWorkers, refetchInterval: 10_000 });
  const [drafts, setDrafts] = useState<Draft[]>([]);
  const [dirty, setDirty] = useState(false);
  const [saveErrors, setSaveErrors] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    if (workersQuery.data && !dirty) setDrafts(workersQuery.data.map(toDraft));
  }, [workersQuery.data, dirty]);

  const healthById = useMemo(
    () => new Map((healthQuery.data ?? []).map((health) => [health.id, health] as const)),
    [healthQuery.data],
  );

  const refreshAll = () => {
    void queryClient.invalidateQueries({ queryKey: queryKeys.health });
    void queryClient.invalidateQueries({ queryKey: queryKeys.pending });
  };

  const save = useMutation({
    mutationFn: () => api.saveWorkers(drafts.map(toInput)),
    onSuccess: (workers) => {
      queryClient.setQueryData(queryKeys.workers, workers);
      setDrafts(workers.map(toDraft));
      setDirty(false);
      setSaveErrors([]);
      setSaved(true);
      refreshAll();
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
        token: "",
        hasToken: false,
        tokenHint: null,
        workerTokenId: null,
        url: "",
        coveUrlOverride: "",
        enabled: true,
        maxConcurrency: 2,
      },
    ]);
    setDirty(true);
  };

  const remove = (key: string) => {
    setDrafts((current) => current.filter((draft) => draft.key !== key));
    setDirty(true);
  };

  if (workersQuery.isLoading || workersQuery.error) {
    return (
      <Section title="Generation workers">
        {workersQuery.error ? (
          <Message tone="error">{(workersQuery.error as Error).message}</Message>
        ) : (
          <Message tone="muted">Loading workers…</Message>
        )}
      </Section>
    );
  }

  return (
    <Section
      title="Generation workers"
      description="Workers run the heavy ffmpeg work. They read videos from Cove and send the results back over HTTP, so they need no access to your media folders. Give a worker a URL if Cove should connect to it; leave the URL empty if the worker connects to Cove."
    >
      <div className="space-y-4">
        <CoveUrlSettings />

        <div className="space-y-3">
          {drafts.length === 0 ? <Message tone="muted">No workers yet.</Message> : null}
          {drafts.map((draft) => (
            <WorkerCard
              key={draft.key}
              draft={draft}
              health={draft.id ? healthById.get(draft.id) : undefined}
              unsaved={dirty}
              onChange={(patch) => update(draft.key, patch)}
              onRemove={() => remove(draft.key)}
            />
          ))}
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <Button onClick={add}>
            <Plus className="h-4 w-4" />
            Add worker
          </Button>
          <Button variant="primary" onClick={() => save.mutate()} disabled={!dirty || save.isPending}>
            {save.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
            Save workers
          </Button>
          <Button
            onClick={() => void api.health(true).then((data) => queryClient.setQueryData(queryKeys.health, data))}
            title="Ask every connected worker for its current status"
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

        <PendingWorkers pending={pendingQuery.data ?? []} dirty={dirty} />
      </div>
    </Section>
  );
}

function CoveUrlSettings() {
  const queryClient = useQueryClient();
  const settingsQuery = useQuery({ queryKey: queryKeys.settings, queryFn: api.settings });
  const [value, setValue] = useState<string | null>(null);
  const [status, setStatus] = useState<{ tone: "success" | "error"; text: string } | null>(null);
  const current = value ?? settingsQuery.data?.coveUrlForWorkers ?? "";

  const save = useMutation({
    mutationFn: () => api.saveSettings({ coveUrlForWorkers: current.trim() || null }),
    onSuccess: (settings) => {
      queryClient.setQueryData(queryKeys.settings, settings);
      setValue(null);
      setStatus({ tone: "success", text: "Saved." });
    },
    onError: (error) => setStatus({ tone: "error", text: (error as Error).message }),
  });

  return (
    <div className="space-y-2 rounded-xl border border-border/60 bg-surface/60 p-3">
      <label className="block space-y-1">
        <span className="text-xs font-medium text-foreground">Cove URL for workers</span>
        <div className="flex flex-wrap items-center gap-2">
          <input
            className={inputClass}
            style={{ maxWidth: "28rem" }}
            value={current}
            placeholder="http://192.168.1.10:5073"
            onChange={(e) => {
              setValue(e.target.value);
              setStatus(null);
            }}
          />
          <Button onClick={() => save.mutate()} disabled={value === null || save.isPending}>
            {save.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
            Save
          </Button>
          {status ? <Message tone={status.tone}>{status.text}</Message> : null}
        </div>
      </label>
      <p className="text-[11px] text-muted">
        The address workers use to read videos from Cove and upload results. Workers that connect to Cove use the
        address they connected to unless this is set; a worker can also override it.
      </p>
      {settingsQuery.data && !settingsQuery.data.coveAuthEnabled ? (
        <p className="text-[11px] text-amber-300">
          Cove's sign-in is off. Workers must then reach Cove from a local network address with a local host name
          (e.g. http://192.168.1.10:5073); a request from anywhere else makes Cove turn sign-in on to protect itself.
        </p>
      ) : null}
    </div>
  );
}

function WorkerCard({
  draft,
  health,
  unsaved,
  onChange,
  onRemove,
}: {
  draft: Draft;
  health?: WorkerHealth;
  unsaved: boolean;
  onChange: (patch: Partial<Draft>) => void;
  onRemove: () => void;
}) {
  const [testMessage, setTestMessage] = useState<{ type: "success" | "error"; text: string } | null>(null);
  const testMutation = useMutation({
    mutationFn: () => api.testWorker(draft.id!),
    onMutate: () => setTestMessage(null),
    onSuccess: (result) =>
      setTestMessage({ type: result.ok ? "success" : "error", text: `${result.ok ? "Test passed" : "Test failed"}: ${result.message}` }),
    onError: (error) => setTestMessage({ type: "error", text: `Test failed: ${(error as Error).message}` }),
  });
  const load = describeLoad(health);
  // Saved workers start collapsed; a freshly added one opens so it can be filled in.
  const [expanded, setExpanded] = useState(draft.id === null);
  // A result only describes the settings it ran with.
  const change = (patch: Partial<Draft>) => {
    setTestMessage(null);
    onChange(patch);
  };

  const statusMessage =
    testMessage ?? (health && !health.live && health.enabled && health.error ? { type: "error" as const, text: health.error } : null);

  return (
    <ExpandableCard
      label={draft.name || "New worker"}
      description={
        <span className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1">
          {draft.id ? <StateBadge health={health} /> : <span className="text-muted">Not saved</span>}
          {load ? <span className="text-muted">{load}</span> : null}
          <span className="truncate text-muted">{connectionLabel(draft.url)}</span>
          {draft.workerTokenId ? <span className="font-mono text-muted">ID {draft.workerTokenId}</span> : null}
        </span>
      }
      actions={
        <div className="ml-3 flex shrink-0 items-center gap-2">
          <Button
            onClick={() => testMutation.mutate()}
            disabled={!draft.id || unsaved || testMutation.isPending}
            title={!draft.id || unsaved ? "Save the workers first" : "Check the worker is connected and can read a video from Cove"}
          >
            {testMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Zap className="h-4 w-4" />}
            Test
          </Button>
          <Button variant="danger" onClick={onRemove} aria-label={`Remove ${draft.name || "worker"}`}>
            <Trash2 className="h-4 w-4" />
          </Button>
        </div>
      }
      expanded={expanded}
      onToggleExpand={() => setExpanded((open) => !open)}
      statusMessage={statusMessage}
    >
      <div className="space-y-3">
        <div className="grid gap-3 sm:grid-cols-2">
          <label className="block space-y-1">
            <span className="text-xs text-secondary">Name</span>
            <input className={inputClass} value={draft.name} onChange={(e) => change({ name: e.target.value })} placeholder="gpu-box" />
          </label>
          <label className="block space-y-1">
            <span className="text-xs text-secondary">Worker token</span>
            <input
              className={inputClass}
              type="password"
              autoComplete="new-password"
              value={draft.token}
              onChange={(e) => change({ token: e.target.value })}
              placeholder={
                draft.hasToken
                  ? `Unchanged (${draft.tokenHint ?? "set"})`
                  : draft.id
                    ? "Unchanged (trusted)"
                    : "HL_WORKER_TOKEN or the worker's data/worker.token"
              }
            />
          </label>
          <label className="block space-y-1">
            <span className="text-xs text-secondary">Worker URL (optional)</span>
            <input
              className={inputClass}
              value={draft.url}
              onChange={(e) => change({ url: e.target.value })}
              placeholder="ws://192.168.1.20:8750/rpc — leave empty if the worker connects to Cove"
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
          <label className="block space-y-1 sm:col-span-2">
            <span className="text-xs text-secondary">Cove URL for this worker (optional)</span>
            <input
              className={inputClass}
              value={draft.coveUrlOverride}
              onChange={(e) => change({ coveUrlOverride: e.target.value })}
              placeholder="Overrides the Cove URL for workers, e.g. when this worker reaches Cove by another address"
            />
          </label>
        </div>
        <Checkbox label="Enabled" checked={draft.enabled} onChange={(enabled) => change({ enabled })} />
      </div>
    </ExpandableCard>
  );
}

function PendingWorkers({ pending, dirty }: { pending: PendingWorker[]; dirty: boolean }) {
  const queryClient = useQueryClient();
  const [error, setError] = useState<string | null>(null);
  const done = () => {
    setError(null);
    void queryClient.invalidateQueries({ queryKey: queryKeys.pending });
    void queryClient.invalidateQueries({ queryKey: queryKeys.workers });
    void queryClient.invalidateQueries({ queryKey: queryKeys.health });
  };
  const trust = useMutation({ mutationFn: api.trustWorker, onSuccess: done, onError: (e) => setError((e as Error).message) });
  const dismiss = useMutation({ mutationFn: api.dismissWorker, onSuccess: done, onError: (e) => setError((e as Error).message) });

  if (pending.length === 0) return null;

  return (
    <div className="space-y-2 rounded-xl border border-border/60 bg-surface/60 p-3">
      <div>
        <p className="text-xs font-medium text-foreground">Pending workers</p>
        <p className="text-[11px] text-muted">
          These workers tried to connect with a token Cove does not know. Trust one only if its ID matches the ID the
          worker printed when it started.
        </p>
      </div>
      {pending.map((worker) => (
        <div key={worker.workerTokenId} className="flex flex-wrap items-center gap-3">
          <span className="text-sm text-foreground">{worker.name ?? "Unnamed worker"}</span>
          <span className="font-mono text-xs text-secondary">ID {worker.workerTokenId}</span>
          <span className="text-xs text-muted">
            {[worker.remoteAddress, worker.version ? `v${worker.version}` : null, `seen ${timeAgo(worker.lastSeen)}`]
              .filter(Boolean)
              .join(" · ")}
          </span>
          <div className="ml-auto flex items-center gap-2">
            <Button
              variant="primary"
              onClick={() => trust.mutate(worker.workerTokenId)}
              disabled={dirty || trust.isPending}
              title={dirty ? "Save or discard your worker changes first" : "Add this worker; it connects within a minute"}
            >
              <Check className="h-4 w-4" />
              Trust
            </Button>
            <Button onClick={() => dismiss.mutate(worker.workerTokenId)} disabled={dismiss.isPending} title="Forget this request">
              <X className="h-4 w-4" />
            </Button>
          </div>
        </div>
      ))}
      {error ? <Message tone="error">{error}</Message> : null}
    </div>
  );
}
