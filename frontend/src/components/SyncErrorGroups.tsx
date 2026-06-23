import {
  groupSyncErrors,
  isAutoSkipped,
  type SyncErrorSeverity,
} from '@/lib/sync-error-classifier';

const SEVERITY_STYLES: Record<
  SyncErrorSeverity,
  { wrap: string; title: string; body: string; badge: string }
> = {
  error: {
    wrap: 'bg-red-50 border-red-200',
    title: 'text-red-700',
    body: 'text-red-600',
    badge: 'bg-red-100 text-red-700',
  },
  warning: {
    wrap: 'bg-amber-50 border-amber-200',
    title: 'text-amber-700',
    body: 'text-amber-600',
    badge: 'bg-amber-100 text-amber-700',
  },
  info: {
    wrap: 'bg-amber-50 border-amber-200',
    title: 'text-amber-700',
    body: 'text-amber-600',
    badge: 'bg-amber-100 text-amber-700',
  },
};

/**
 * Splits a raw error list into actionable failures vs auto-handled conditions,
 * for the per-tunnel header chips.
 */
export function summarizeErrorCounts(errors: string[]): {
  failed: number;
  autoSkipped: number;
} {
  let failed = 0;
  let autoSkipped = 0;
  for (const g of groupSyncErrors(errors)) {
    if (isAutoSkipped(g.category)) autoSkipped += g.count;
    else failed += g.count;
  }
  return { failed, autoSkipped };
}

/**
 * Renders raw sync error strings as grouped, plain-language cards: one card per
 * category with a count badge, a "what to do" line, and a collapsible
 * Technical-details disclosure that preserves the original message(s).
 */
export function SyncErrorGroups({ errors }: { errors: string[] }) {
  const groups = groupSyncErrors(errors);
  if (groups.length === 0) return null;

  return (
    <div className="mt-2 space-y-2">
      {groups.map((g) => {
        const s = SEVERITY_STYLES[g.severity];
        return (
          <div key={g.category} className={`border rounded-lg p-3 ${s.wrap}`}>
            <div className="flex items-center justify-between gap-2">
              <p className={`text-xs font-semibold ${s.title}`}>{g.title}</p>
              <span
                className={`text-[11px] font-medium px-1.5 py-0.5 rounded-full ${s.badge}`}
              >
                ×&nbsp;{g.count}
              </span>
            </div>
            <p className={`text-xs mt-1 ${s.body}`}>{g.guidance}</p>
            <details className="mt-1.5">
              <summary className="text-[11px] text-text-muted cursor-pointer select-none">
                Technical details
              </summary>
              <ul className="mt-1 space-y-0.5">
                {g.rawSamples.map((raw, i) => (
                  <li
                    key={i}
                    className="text-[11px] text-text-muted font-mono break-words"
                  >
                    {raw}
                  </li>
                ))}
                {g.count > g.rawSamples.length && (
                  <li className="text-[11px] text-text-muted">
                    …and {g.count - g.rawSamples.length} more identical
                  </li>
                )}
              </ul>
            </details>
          </div>
        );
      })}
    </div>
  );
}
