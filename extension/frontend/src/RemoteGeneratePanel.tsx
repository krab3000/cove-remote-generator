import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Loader2, PlayCircle, RefreshCw } from "lucide-react";
import { useAppConfig } from "@cove/runtime/components";
import { ApiError, api, queryKeys, type GenerateOptions } from "./api";
import { FolderTree } from "./FolderTree";
import { Button, Card, Checkbox, Message, StateBadge, describeLoad } from "./ui";

type Artifacts = Pick<GenerateOptions, "cover" | "preview" | "sprite" | "overwrite">;

export function RemoteGeneratePanel() {
  const queryClient = useQueryClient();
  const { config } = useAppConfig();
  const optionsQuery = useQuery({ queryKey: queryKeys.options, queryFn: api.options });
  const healthQuery = useQuery({
    queryKey: queryKeys.health,
    queryFn: () => api.health(false),
    refetchInterval: 10_000,
  });

  const [artifacts, setArtifacts] = useState<Artifacts>({ cover: true, preview: true, sprite: true, overwrite: false });
  const [paths, setPaths] = useState<string[]>([]);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [initialized, setInitialized] = useState(false);
  const [status, setStatus] = useState<{ tone: "success" | "error"; text: string } | null>(null);

  const servers = useMemo(() => (healthQuery.data ?? []).filter((s) => s.enabled), [healthQuery.data]);
  const liveIds = useMemo(() => new Set(servers.filter((s) => s.live).map((s) => s.id)), [servers]);

  // Seed the form from the last run once both the options and the first health check are in.
  useEffect(() => {
    if (initialized || !optionsQuery.data || !healthQuery.data) return;
    const last = optionsQuery.data;
    setArtifacts({ cover: last.cover, preview: last.preview, sprite: last.sprite, overwrite: last.overwrite });
    setPaths(last.paths ?? []);
    const remembered = (last.serverIds ?? []).filter((id) => liveIds.has(id));
    setSelected(new Set(remembered.length > 0 ? remembered : liveIds));
    setInitialized(true);
  }, [initialized, optionsQuery.data, healthQuery.data, liveIds]);

  const selectedLive = [...selected].filter((id) => liveIds.has(id));
  const anyArtifact = artifacts.cover || artifacts.preview || artifacts.sprite;
  const roots = (config?.covePaths ?? [])
    .filter((p) => !p.excludeVideo)
    .map((p) => p.path.trim())
    .filter(Boolean)
    .map((path) => ({ name: path, path, hasChildren: true }));

  const run = useMutation({
    mutationFn: () => api.generate({ ...artifacts, paths, serverIds: selectedLive }),
    onSuccess: ({ jobId }) => {
      setStatus({ tone: "success", text: `Job ${jobId} started. Follow its progress in the Jobs drawer.` });
      void queryClient.invalidateQueries({ queryKey: queryKeys.options });
    },
    onError: (error) => {
      if (error instanceof ApiError && error.code === "NO_LIVE_SERVERS") {
        void queryClient.invalidateQueries({ queryKey: queryKeys.health });
      }
      setStatus({ tone: "error", text: (error as Error).message });
    },
  });

  const refresh = () => void api.health(true).then((data) => queryClient.setQueryData(queryKeys.health, data));
  const toggleServer = (id: string, checked: boolean) =>
    setSelected((current) => {
      const next = new Set(current);
      if (checked) next.add(id);
      else next.delete(id);
      return next;
    });
  const togglePath = (path: string, checked: boolean) =>
    setPaths((current) => (checked ? [...new Set([...current, path])] : current.filter((p) => p !== path)));

  const blocker = !healthQuery.data
    ? "Checking servers…"
    : servers.length === 0
      ? "Add and enable a generation server above first."
      : liveIds.size === 0
        ? "No generation server is reachable right now, so a job cannot start."
        : selectedLive.length === 0
          ? "Select at least one live server."
          : !anyArtifact
            ? "Select at least one thing to generate."
            : null;

  return (
    <Card>
      <div className="space-y-4">
        <div className="flex items-center justify-between gap-3">
          <div>
            <h4 className="text-sm font-medium text-foreground">Remote generate</h4>
            <p className="mt-0.5 text-xs text-secondary">
              Generate video covers, previews and sprite sheets on the selected servers. Files land in Cove's generated
              folder exactly where Cove's own generate task puts them.
            </p>
          </div>
          <button
            type="button"
            onClick={() => run.mutate()}
            disabled={blocker !== null || run.isPending}
            className="ml-3 inline-flex shrink-0 items-center gap-2 rounded-lg bg-accent px-4 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-60"
          >
            {run.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <PlayCircle className="h-4 w-4" />}
            Run
          </button>
        </div>
        {blocker && healthQuery.data ? <Message tone="error">{blocker}</Message> : null}
        {status ? <Message tone={status.tone}>{status.text}</Message> : null}

        <div className="space-y-2 border-t border-border/50 pt-3">
          <p className="text-xs font-medium uppercase tracking-wide text-muted">Video options</p>
          <div className="grid gap-2 sm:grid-cols-3">
            <Checkbox label="Covers / screenshots" checked={artifacts.cover} onChange={(cover) => setArtifacts({ ...artifacts, cover })} />
            <Checkbox label="Video previews" checked={artifacts.preview} onChange={(preview) => setArtifacts({ ...artifacts, preview })} />
            <Checkbox label="Sprite sheets" checked={artifacts.sprite} onChange={(sprite) => setArtifacts({ ...artifacts, sprite })} />
          </div>
          <Checkbox
            label="Overwrite existing generated files"
            checked={artifacts.overwrite}
            onChange={(overwrite) => setArtifacts({ ...artifacts, overwrite })}
          />
        </div>

        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <p className="text-xs font-medium uppercase tracking-wide text-muted">Servers</p>
            <Button onClick={refresh} title="Check the servers now">
              <RefreshCw className={`h-3.5 w-3.5 ${healthQuery.isFetching ? "animate-spin" : ""}`} />
              Refresh
            </Button>
          </div>
          {servers.length === 0 && healthQuery.data ? (
            <Message tone="muted">No enabled generation servers.</Message>
          ) : null}
          {servers.map((server) => (
            <div key={server.id} className="flex flex-wrap items-center gap-3">
              <Checkbox
                label={server.name}
                checked={server.live && selected.has(server.id)}
                disabled={!server.live}
                onChange={(checked) => toggleServer(server.id, checked)}
                title={server.error ?? undefined}
              />
              <StateBadge health={server} />
              {describeLoad(server) ? <span className="text-xs text-muted">{describeLoad(server)}</span> : null}
              {!server.live && server.error ? <span className="text-[11px] text-red-300">{server.error}</span> : null}
            </div>
          ))}
        </div>

        <div className="space-y-2 rounded-xl border border-border/60 bg-surface/60 p-3">
          <div className="flex items-center justify-between gap-3">
            <div>
              <p className="text-xs font-medium text-foreground">Selective generate</p>
              <p className="text-[11px] text-muted">
                Pick folders to generate for, or leave everything unselected to cover the whole library. Expand a
                library path to drill into a specific subfolder.
              </p>
            </div>
            {paths.length > 0 ? (
              <button type="button" onClick={() => setPaths([])} className="text-[11px] text-accent hover:text-accent-hover">
                Clear
              </button>
            ) : null}
          </div>
          <FolderTree roots={roots} selected={paths} onToggle={togglePath} emptyHint="No video library paths configured." />
        </div>
      </div>
    </Card>
  );
}
