'use client';

import React, { useEffect, useRef, useState } from 'react';
import { toast } from 'sonner';
import { api } from '@/lib/api';
import { Button } from '@/components/ui/button';

/**
 * Type local to this modal — duplicated from cleanup/page.tsx on purpose so this
 * component stays self-contained for a one-off internal feature (quick-260417-48z).
 */
export interface SelectedFolder {
  entraId: string;
  email: string;
  folderId: string;
  folderName: string;
}

interface Props {
  jobId: string;
  total: number;
  selectedFolders: SelectedFolder[];
  /**
   * Called when the user dismisses the modal. The Set contains
   * `${entraId}|${folderId}` keys for folders the cleanup attempted (so the
   * page can prune them from scanResults). When the user clicks "Hide" while
   * the job is still running, the Set is empty — the work continues in the
   * background and scanResults is left untouched.
   */
  onClose: (deletedFolderKeys: Set<string>) => void;
}

type JobStatus = 'Queued' | 'Running' | 'Completed' | 'Failed' | 'Cancelled';

const POLL_INTERVAL_MS = 2000;
const TICK_INTERVAL_MS = 1000;
const ETA_MIN_PROCESSED = 50;

function isTerminal(s: JobStatus): boolean {
  return s === 'Completed' || s === 'Failed' || s === 'Cancelled';
}

function formatElapsed(ms: number): string {
  if (ms < 0) return '—';
  const totalSec = Math.floor(ms / 1000);
  const m = Math.floor(totalSec / 60);
  const s = totalSec % 60;
  return `${m}:${s.toString().padStart(2, '0')}`;
}

function formatEta(remainingSec: number): string {
  if (!isFinite(remainingSec) || remainingSec <= 0) return '—';
  if (remainingSec < 60) return `~${Math.ceil(remainingSec)}s remaining`;
  const m = Math.floor(remainingSec / 60);
  const s = Math.round(remainingSec % 60);
  if (m < 60) return s > 0 ? `~${m}m ${s}s remaining` : `~${m} min remaining`;
  const h = Math.floor(m / 60);
  const mm = m % 60;
  return `~${h}h ${mm}m remaining`;
}

export function CleanupProgressModal({ jobId, total, selectedFolders, onClose }: Props) {
  const [status, setStatus] = useState<JobStatus>('Queued');
  const [deleted, setDeleted] = useState(0);
  const [failed, setFailed] = useState(0);
  const [lastError, setLastError] = useState<string | null>(null);
  const [startedAtMs, setStartedAtMs] = useState<number | null>(null);
  const [elapsedMs, setElapsedMs] = useState(0);
  const toastFiredRef = useRef(false);

  // ── Poll the job status endpoint every 2s until terminal ────────────────
  useEffect(() => {
    let cancelled = false;
    let intervalId: ReturnType<typeof setInterval> | null = null;

    const fetchOnce = async () => {
      try {
        const data = await api.cleanup.jobStatus(jobId);
        if (cancelled) return;
        setStatus(data.status);
        setDeleted(data.deleted);
        setFailed(data.failed);
        setLastError(data.lastError);
        if (data.startedAt) {
          setStartedAtMs(new Date(data.startedAt).getTime());
        }
        if (isTerminal(data.status) && intervalId) {
          clearInterval(intervalId);
          intervalId = null;
          if (!toastFiredRef.current) {
            toastFiredRef.current = true;
            if (data.status === 'Completed') {
              toast.success(`Deleted ${data.deleted} / ${total} folders, ${data.failed} failed.`);
            } else if (data.status === 'Failed') {
              toast.error(`Cleanup failed after ${data.deleted} deleted, ${data.failed} failed.`);
            } else {
              toast.info(`Cleanup ${data.status.toLowerCase()}.`);
            }
          }
        }
      } catch {
        // Network blip — keep polling. Modal stays open with last known state.
      }
    };

    // Kick off the first fetch immediately so the modal doesn't sit blank for 2s.
    fetchOnce();
    intervalId = setInterval(fetchOnce, POLL_INTERVAL_MS);

    return () => {
      cancelled = true;
      if (intervalId) clearInterval(intervalId);
    };
  }, [jobId, total]);

  // ── Tick elapsed clock once per second while we have a startedAt ─────────
  useEffect(() => {
    if (startedAtMs == null) return;
    const tick = () => setElapsedMs(Date.now() - startedAtMs);
    tick();
    const id = setInterval(tick, TICK_INTERVAL_MS);
    return () => clearInterval(id);
  }, [startedAtMs]);

  const processed = deleted + failed;
  const pct = total > 0 ? Math.min(100, Math.round((processed / total) * 100)) : 0;
  const terminal = isTerminal(status);

  // ETA: only meaningful once we have a usable rate (>50 items processed) and a
  // live elapsed clock. Computed in seconds.
  const elapsedSec = elapsedMs / 1000;
  const rate = elapsedSec > 0 ? processed / elapsedSec : 0;
  const remainingSec = rate > 0 ? (total - processed) / rate : Infinity;
  const showEta = !terminal && processed > ETA_MIN_PROCESSED && rate > 0;

  const handleClose = () => {
    if (terminal) {
      // Pass the full set of attempted folder keys so the page can prune them
      // from scanResults — matches the legacy optimistic-removal behaviour.
      const keys = new Set(selectedFolders.map((f) => `${f.entraId}|${f.folderId}`));
      onClose(keys);
    } else {
      // "Hide" — work continues in the background, scanResults left intact.
      onClose(new Set());
    }
  };

  return (
    <div
      className="fixed inset-0 z-50 bg-black/50 flex items-center justify-center"
      role="dialog"
      aria-modal="true"
      aria-labelledby="cleanup-progress-title"
    >
      <div className="bg-card border rounded-lg p-6 w-[480px] max-w-[90vw] shadow-xl">
        <h2 id="cleanup-progress-title" className="text-lg font-semibold mb-1">
          Deleting folders
        </h2>
        <p className="text-xs text-text-muted mb-4">Job {jobId.slice(0, 8)}…</p>

        {/* Progress bar */}
        <div className="w-full h-2 bg-muted rounded mb-2 overflow-hidden">
          <div
            className="h-2 bg-gold rounded transition-all duration-300 ease-out"
            style={{ width: `${pct}%` }}
            aria-valuenow={pct}
            aria-valuemin={0}
            aria-valuemax={100}
            role="progressbar"
          />
        </div>

        <div className="flex items-center justify-between text-sm mb-1">
          <span>
            Deleted <span className="font-medium">{deleted}</span> / {total}
            {failed > 0 && (
              <>
                {' · '}
                <span className="text-red-600">{failed} failed</span>
              </>
            )}
          </span>
          <span className="text-text-muted text-xs">{pct}%</span>
        </div>

        <div className="flex items-center justify-between text-xs text-text-muted mb-4">
          <span>
            Status: <span className="font-medium">{status}</span>
          </span>
          <span>
            Elapsed {startedAtMs == null ? '—' : formatElapsed(elapsedMs)}
            {showEta && <span className="ml-2">· {formatEta(remainingSec)}</span>}
          </span>
        </div>

        {failed > 0 && lastError && (
          <div className="text-xs text-red-700 bg-red-50 border border-red-200 rounded p-2 mb-3 max-h-20 overflow-y-auto">
            <span className="font-medium">Last error:</span> {lastError}
          </div>
        )}

        <div className="flex justify-end">
          <Button
            type="button"
            variant={terminal ? 'default' : 'outline'}
            size="sm"
            className={terminal ? 'bg-gold text-white hover:bg-gold/90' : ''}
            onClick={handleClose}
          >
            {terminal ? 'Close' : 'Hide'}
          </Button>
        </div>
      </div>
    </div>
  );
}
