import type { ButtonHTMLAttributes, ReactNode } from "react";
import { ChevronDown, ChevronUp, Loader2, PlayCircle } from "lucide-react";
import type { ServerHealth, ServerState } from "./api";

// Class names are copied from Cove's own settings primitives so the host stylesheet already has them.

export const inputClass =
  "w-full bg-card border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent";

export function Button(props: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: "primary" | "danger" | "ghost" }) {
  const { variant = "ghost", className = "", type = "button", ...rest } = props;
  const base =
    "inline-flex min-h-10 items-center justify-center gap-1.5 rounded-lg px-3 py-2 text-sm font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-60 sm:min-h-0 sm:py-1.5";
  const variantClass =
    variant === "primary"
      ? "bg-accent text-white hover:bg-accent-hover"
      : variant === "danger"
        ? "bg-red-600 text-white hover:bg-red-500"
        : "border border-border bg-card text-secondary hover:border-accent/50 hover:bg-card-hover hover:text-foreground";
  return <button {...rest} type={type} className={`${base} ${variantClass} ${className}`} />;
}

export function Checkbox({
  label,
  checked,
  onChange,
  disabled = false,
  title,
}: {
  label: ReactNode;
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
  title?: string;
}) {
  return (
    <label className="flex items-center gap-2 text-sm text-secondary" title={title}>
      <input
        type="checkbox"
        checked={checked}
        disabled={disabled}
        onChange={(event) => onChange(event.target.checked)}
        className="h-4 w-4 rounded border-border bg-card text-accent focus:ring-0 disabled:cursor-not-allowed disabled:opacity-60"
      />
      <span>{label}</span>
    </label>
  );
}

// Markup copied from Cove's SettingsSection (ui/src/components/SettingsPrimitives.tsx). The settings tab uses the
// page layout, so the host adds no card of its own; keep the class names identical for the same reason as below.
export function Section({
  title,
  description,
  actions,
  children,
}: {
  title: ReactNode;
  description?: ReactNode;
  actions?: ReactNode;
  children: ReactNode;
}) {
  return (
    <section className="rounded-2xl border border-border bg-surface p-5 shadow-[0_12px_30px_-20px_rgba(0,0,0,0.7)]">
      <header className="mb-4 flex items-start justify-between gap-4">
        <div>
          <h3 className="text-base font-semibold text-foreground">{title}</h3>
          {description ? <p className="mt-1 text-sm text-secondary">{description}</p> : null}
        </div>
        {actions ? <div className="shrink-0">{actions}</div> : null}
      </header>
      {children}
    </section>
  );
}

// Markup copied from Cove's TaskCard (ui/src/components/SettingsPrimitives.tsx), which extensions cannot import.
// Keep the class names identical: component styles such as "glass" match on them.
export function ExpandableCard({
  label,
  description,
  actions,
  expanded,
  onToggleExpand,
  statusMessage,
  children,
}: {
  label: ReactNode;
  description: ReactNode;
  actions?: ReactNode;
  expanded: boolean;
  onToggleExpand: () => void;
  statusMessage?: { type: "success" | "error"; text: string } | null;
  children?: ReactNode;
}) {
  return (
    <div className="rounded-xl border border-border bg-card p-4">
      <div className="flex items-center justify-between">
        <div className="flex min-w-0 flex-1 items-center gap-2">
          <button
            type="button"
            onClick={onToggleExpand}
            aria-expanded={expanded}
            aria-label={expanded ? "Collapse" : "Expand"}
            className="shrink-0 text-muted hover:text-foreground"
          >
            {expanded ? <ChevronUp className="h-4 w-4" /> : <ChevronDown className="h-4 w-4" />}
          </button>
          <div className="min-w-0">
            <h4 className="text-sm font-medium text-foreground">{label}</h4>
            <div className="mt-0.5 text-xs text-secondary">{description}</div>
          </div>
        </div>
        {actions}
      </div>
      {statusMessage ? (
        <p className={`mt-2 text-xs ${statusMessage.type === "success" ? "text-green-400" : "text-red-400"}`}>
          {statusMessage.text}
        </p>
      ) : null}
      {children && expanded ? <div className="mt-3">{children}</div> : null}
    </div>
  );
}

export function RunButton({ onRun, isPending, disabled = false, label = "Run" }: {
  onRun: () => void;
  isPending: boolean;
  disabled?: boolean;
  label?: string;
}) {
  return (
    <button
      type="button"
      onClick={onRun}
      disabled={isPending || disabled}
      className="ml-3 inline-flex shrink-0 items-center gap-2 rounded-lg bg-accent px-4 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-60"
    >
      {isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <PlayCircle className="h-4 w-4" />}
      {label}
    </button>
  );
}

export function Message({ tone, children }: { tone: "success" | "error" | "muted"; children: ReactNode }) {
  const color = tone === "success" ? "text-green-400" : tone === "error" ? "text-red-400" : "text-muted";
  return <p className={`text-xs ${color}`}>{children}</p>;
}

const STATE_COLORS: Record<ServerState, string> = {
  live: "#22c55e",
  offline: "#ef4444",
  unauthorized: "#f59e0b",
  incompatible: "#f59e0b",
  disabled: "#6b7280",
};

const STATE_LABELS: Record<ServerState, string> = {
  live: "Live",
  offline: "Offline",
  unauthorized: "Key rejected",
  incompatible: "Incompatible",
  disabled: "Disabled",
};

export function StateBadge({ health }: { health?: ServerHealth }) {
  if (!health) {
    return <span className="text-xs text-muted">Checking…</span>;
  }
  return (
    <span className="inline-flex items-center gap-1.5 text-xs text-secondary" title={health.error ?? undefined}>
      <span
        aria-hidden
        style={{ width: 8, height: 8, borderRadius: 9999, background: STATE_COLORS[health.state], display: "inline-block" }}
      />
      {STATE_LABELS[health.state]}
      {health.live && health.latencyMs != null ? <span className="text-muted">· {health.latencyMs} ms</span> : null}
    </span>
  );
}

export function describeLoad(health?: ServerHealth): string | null {
  if (!health?.live) return null;
  const parts = [`${health.running ?? 0}/${health.capacity ?? "?"} busy`];
  if (health.queued) parts.push(`${health.queued} queued`);
  if (health.encoder) parts.push(health.encoder);
  return parts.join(" · ");
}
