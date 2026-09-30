import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, ChevronRight } from "lucide-react";
import { ApiError, api, queryKeys, type LibraryFolder } from "./api";

/** Cove's LibraryFolderTree (not exported to extensions), reading the same /api/metadata/library-folders. */
export function FolderTree({
  roots,
  selected,
  onToggle,
  emptyHint,
}: {
  roots: LibraryFolder[];
  selected: string[];
  onToggle: (path: string, checked: boolean) => void;
  emptyHint: string;
}) {
  const selectedSet = useMemo(() => new Set(selected), [selected]);
  if (roots.length === 0) {
    return <p className="text-[11px] text-muted">{emptyHint}</p>;
  }
  return (
    <div className="max-h-72 space-y-0.5 overflow-auto rounded-lg border border-border/60 bg-surface/40 p-1.5">
      {roots.map((root) => (
        <FolderNode key={root.path} folder={root} depth={0} selectedSet={selectedSet} onToggle={onToggle} />
      ))}
    </div>
  );
}

function FolderNode({
  folder,
  depth,
  selectedSet,
  onToggle,
}: {
  folder: LibraryFolder;
  depth: number;
  selectedSet: Set<string>;
  onToggle: (path: string, checked: boolean) => void;
}) {
  const [expanded, setExpanded] = useState(false);
  const { data: children, isLoading, error } = useQuery({
    queryKey: queryKeys.folders(folder.path),
    queryFn: () => api.libraryFolders(folder.path),
    enabled: expanded && folder.hasChildren,
    staleTime: 60_000,
  });
  const indent = depth * 16 + 4;
  const hint = { paddingLeft: indent + 38 };

  return (
    <div>
      <div className="flex items-center gap-1.5 rounded px-1 py-0.5 hover:bg-surface/70" style={{ paddingLeft: indent }}>
        {folder.hasChildren ? (
          <button
            type="button"
            onClick={() => setExpanded((current) => !current)}
            className="text-muted hover:text-foreground"
            aria-label={`${expanded ? "Collapse" : "Expand"} folder ${folder.name}`}
          >
            {expanded ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
          </button>
        ) : (
          <span className="inline-block w-3.5" />
        )}
        <label className="flex min-w-0 cursor-pointer items-center gap-1.5">
          <input
            type="checkbox"
            checked={selectedSet.has(folder.path)}
            onChange={(event) => onToggle(folder.path, event.target.checked)}
            className="border-border"
          />
          <span className="truncate text-xs text-foreground" title={folder.path}>
            {folder.name}
          </span>
        </label>
      </div>
      {expanded && folder.hasChildren ? (
        <div>
          {isLoading ? (
            <p className="text-[11px] text-muted" style={hint}>
              Loading…
            </p>
          ) : error ? (
            <p className="text-[11px] text-red-300" style={hint}>
              {error instanceof ApiError && error.status === 403
                ? "You need library-scan or file-read permission to browse folders."
                : "Unable to list subfolders"}
            </p>
          ) : (children ?? []).length === 0 ? (
            <p className="text-[11px] text-muted" style={hint}>
              No subfolders
            </p>
          ) : (
            (children ?? []).map((child) => (
              <FolderNode key={child.path} folder={child} depth={depth + 1} selectedSet={selectedSet} onToggle={onToggle} />
            ))
          )}
        </div>
      ) : null}
    </div>
  );
}
