// Translates raw backend/Graph/EF error strings stored on sync runs into
// plain-language, admin-friendly categories. Pure functions — no UI, no I/O —
// so the taxonomy can be unit-tested and reused across the run-detail views.
//
// The raw messages still live in the database; this only changes presentation.

export type SyncErrorSeverity = 'error' | 'warning' | 'info';

export type SyncErrorCategory =
  | 'mailbox-removed'
  | 'user-invalid'
  | 'internal'
  | 'rate-limit'
  | 'unknown';

export interface ClassifiedError {
  category: SyncErrorCategory;
  severity: SyncErrorSeverity;
  /** Short plain-language headline. */
  title: string;
  /** One line of "what this means / what to do". */
  guidance: string;
  /** The original message, preserved for the "Technical details" disclosure. */
  raw: string;
}

export interface SyncErrorGroup {
  category: SyncErrorCategory;
  severity: SyncErrorSeverity;
  title: string;
  guidance: string;
  /** How many raw errors fell into this group. */
  count: number;
  /** A few representative raw messages for the technical-details toggle. */
  rawSamples: string[];
}

interface Rule {
  category: SyncErrorCategory;
  severity: SyncErrorSeverity;
  title: string;
  guidance: string;
  test: RegExp;
}

// First matching rule wins. Order matters: put the more specific patterns first.
const RULES: Rule[] = [
  {
    category: 'mailbox-removed',
    severity: 'info',
    title: 'Mailbox no longer active',
    guidance:
      'This mailbox was deactivated or deleted in Microsoft 365. It is automatically removed from syncing — no action needed.',
    test: /inactive,?\s*soft-deleted,?\s*or\s*is\s*hosted\s*on-premise/i,
  },
  {
    category: 'user-invalid',
    severity: 'info',
    title: 'User account not found',
    guidance:
      'This user account no longer exists in Microsoft 365. It is skipped automatically — no action needed.',
    test: /requested user .* is invalid/i,
  },
  {
    category: 'rate-limit',
    severity: 'info',
    title: 'Microsoft rate limit',
    guidance:
      'Microsoft temporarily throttled requests. These retry automatically on the next sync — no action needed.',
    test: /throttl|\b429\b|retry-?after|rate limit/i,
  },
  {
    category: 'internal',
    severity: 'error',
    title: 'Internal sync error',
    guidance:
      'A sync-engine error prevented this contact from saving. This has been logged for IT to investigate.',
    test: /duplicate key|saving the entity changes|inner exception|DbUpdate/i,
  },
];

const UNKNOWN: Omit<ClassifiedError, 'raw'> = {
  category: 'unknown',
  severity: 'error',
  title: 'Unexpected error',
  guidance: 'An unexpected error occurred. See technical details below.',
};

export function classifySyncError(raw: string): ClassifiedError {
  const text = raw ?? '';
  for (const rule of RULES) {
    if (rule.test.test(text)) {
      return {
        category: rule.category,
        severity: rule.severity,
        title: rule.title,
        guidance: rule.guidance,
        raw: text,
      };
    }
  }
  return { ...UNKNOWN, raw: text };
}

const SEVERITY_RANK: Record<SyncErrorSeverity, number> = {
  error: 0,
  warning: 1,
  info: 2,
};

const MAX_RAW_SAMPLES = 3;

/**
 * Groups raw error strings by category, counting duplicates and keeping a few
 * raw samples per group. Groups are sorted so actionable errors (red) come
 * before auto-handled, informational ones (amber).
 */
export function groupSyncErrors(rawErrors: string[]): SyncErrorGroup[] {
  const byCategory = new Map<SyncErrorCategory, SyncErrorGroup>();

  for (const raw of rawErrors) {
    const c = classifySyncError(raw);
    const existing = byCategory.get(c.category);
    if (existing) {
      existing.count += 1;
      if (existing.rawSamples.length < MAX_RAW_SAMPLES) {
        existing.rawSamples.push(c.raw);
      }
    } else {
      byCategory.set(c.category, {
        category: c.category,
        severity: c.severity,
        title: c.title,
        guidance: c.guidance,
        count: 1,
        rawSamples: [c.raw],
      });
    }
  }

  return [...byCategory.values()].sort((a, b) => {
    const sev = SEVERITY_RANK[a.severity] - SEVERITY_RANK[b.severity];
    if (sev !== 0) return sev;
    return b.count - a.count;
  });
}

/** True for categories that are auto-handled and shouldn't read as red "failures". */
export function isAutoSkipped(category: SyncErrorCategory): boolean {
  return category === 'mailbox-removed' || category === 'user-invalid' || category === 'rate-limit';
}
