import type { ButtonHTMLAttributes, ReactNode } from "react";
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

export function Card({ children }: { children: ReactNode }) {
  return <div className="rounded-xl border border-border bg-card p-4">{children}</div>;
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
