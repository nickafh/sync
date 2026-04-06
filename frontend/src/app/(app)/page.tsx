'use client';

import { useState, useEffect } from 'react';
import { useRouter } from 'next/navigation';
import { toast } from 'sonner';
import { Loader2, Cable, ClipboardList, Play, FlaskConical, ArrowUpDown } from 'lucide-react';
import { useDashboard, useTriggerSync, useSyncRunPolling } from '@/hooks/use-dashboard';
import { useTunnels } from '@/hooks/use-tunnels';
import type { SyncRunDetailDto } from '@/types/sync-run';
import { PageHeader } from '@/components/PageHeader';
import { KPICard } from '@/components/KPICard';
import { StatusBadge } from '@/components/StatusBadge';
import { EmptyState } from '@/components/EmptyState';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';

function formatTimeAgo(dateStr: string | null): string {
  if (!dateStr) return 'N/A';
  const now = Date.now();
  const then = new Date(dateStr).getTime();
  const diffMs = now - then;

  const seconds = Math.floor(diffMs / 1000);
  if (seconds < 60) return 'just now';
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  return `${days}d ago`;
}

function formatRunType(runType: string): string {
  return runType
    .replace(/_/g, ' ')
    .replace(/\b\w/g, (c) => c.toUpperCase());
}

function SyncProgressCard({ run }: { run: SyncRunDetailDto }) {
  const elapsed = run.startedAt
    ? Math.floor((Date.now() - new Date(run.startedAt).getTime()) / 1000)
    : 0;
  const mins = Math.floor(elapsed / 60);
  const secs = elapsed % 60;
  const total = run.contactsCreated + run.contactsUpdated + run.contactsSkipped + run.contactsFailed + run.contactsRemoved;

  return (
    <Card className="mt-6 border-gold/30 bg-gold/5">
      <CardContent className="py-4">
        <div className="flex items-center gap-3 mb-3">
          <Loader2 className="h-5 w-5 animate-spin text-gold" />
          <span className="font-heading text-navy font-bold">
            Sync in progress
          </span>
          <span className="text-xs text-text-muted ml-auto">
            {mins}m {secs.toString().padStart(2, '0')}s elapsed
          </span>
        </div>
        <div className="grid grid-cols-2 sm:grid-cols-4 lg:grid-cols-7 gap-3 text-sm">
          <div>
            <div className="text-text-muted text-xs">Processed</div>
            <div className="font-medium text-navy">{total} contacts</div>
          </div>
          <div>
            <div className="text-text-muted text-xs">Created</div>
            <div className="font-medium text-emerald-600">{run.contactsCreated}</div>
          </div>
          <div>
            <div className="text-text-muted text-xs">Updated</div>
            <div className="font-medium text-blue-600">{run.contactsUpdated}</div>
          </div>
          <div>
            <div className="text-text-muted text-xs">Skipped</div>
            <div className="font-medium text-text-muted">{run.contactsSkipped}</div>
          </div>
          <div>
            <div className="text-text-muted text-xs">Failed</div>
            <div className="font-medium text-red-600">{run.contactsFailed}</div>
          </div>
          <div>
            <div className="text-text-muted text-xs">Removed</div>
            <div className="font-medium text-amber-600">{run.contactsRemoved}</div>
          </div>
          <div>
            <div className="text-text-muted text-xs">Tunnels</div>
            <div className="font-medium text-navy">{run.tunnelsProcessed ?? 0}</div>
          </div>
        </div>
      </CardContent>
    </Card>
  );
}

export default function DashboardPage() {
  const router = useRouter();
  const [activeRunId, setActiveRunId] = useState<number | null>(null);

  const { data: dashboard, isLoading: dashLoading } = useDashboard();
  const { data: tunnels, isLoading: tunnelsLoading } = useTunnels();
  const triggerSync = useTriggerSync();
  const { data: pollingRun } = useSyncRunPolling(activeRunId);

  // Auto-detect a running sync from dashboard data (e.g. after page refresh mid-sync)
  useEffect(() => {
    if (activeRunId === null && dashboard?.recentRuns) {
      const running = dashboard.recentRuns.find((r) => r.status === 'running');
      if (running) setActiveRunId(running.id);
    }
  }, [activeRunId, dashboard]);

  // Clear activeRunId when polling run transitions to a terminal status
  useEffect(() => {
    if (activeRunId !== null && pollingRun && pollingRun.status !== 'running') {
      setActiveRunId(null);
    }
  }, [activeRunId, pollingRun]);

  const isSyncing = triggerSync.isPending || (activeRunId !== null && pollingRun?.status === 'running');

  function handleRunSync() {
    triggerSync.mutate(
      { runType: 'manual', isDryRun: false, tunnelIds: null },
      {
        onSuccess: (data) => {
          toast.success('Sync run started successfully.');
          setActiveRunId(data.runId);
        },
        onError: (error) => {
          if (error.message.includes('409')) {
            toast.warning('A sync run is already in progress.');
          } else {
            toast.error('Something went wrong. Please try again.');
          }
        },
      },
    );
  }

  function handleDryRun() {
    triggerSync.mutate(
      { runType: 'dry_run', isDryRun: true, tunnelIds: null },
      {
        onSuccess: (data) => {
          toast.success('Dry run started. No changes will be written.');
          setActiveRunId(data.runId);
        },
        onError: (error) => {
          if (error.message.includes('409')) {
            toast.warning('A sync run is already in progress.');
          } else {
            toast.error('Something went wrong. Please try again.');
          }
        },
      },
    );
  }

  // Loading state
  if (dashLoading) {
    return (
      <div>
        <div className="flex items-start justify-between mb-8">
          <div>
            <Skeleton className="h-10 w-40" />
            <Skeleton className="h-4 w-80 mt-2" />
          </div>
          <div className="flex items-center gap-3">
            <Skeleton className="h-8 w-32" />
            <Skeleton className="h-8 w-24" />
          </div>
        </div>
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4 mt-6">
          {[1, 2, 3, 4].map((i) => (
            <Skeleton key={i} className="h-20 rounded-lg" />
          ))}
        </div>
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-6 mt-6">
          <Skeleton className="h-48 rounded-lg" />
          <Skeleton className="h-48 rounded-lg" />
        </div>
      </div>
    );
  }

  const activeTunnels = tunnels?.filter((t) => t.status === 'active') ?? [];

  return (
    <div>
      <PageHeader
        title="Dashboard"
        description="Monitor tunnels, phone-visible lists, and sync activity."
      >
        <Button
          className="bg-gold text-white hover:bg-gold/90"
          onClick={handleRunSync}
          disabled={isSyncing}
        >
          {isSyncing ? (
            <>
              <Loader2 className="mr-2 h-4 w-4 animate-spin" />
              Syncing...
            </>
          ) : (
            <>
              <Play className="mr-2 h-4 w-4" />
              Run Sync Now
            </>
          )}
        </Button>
        <Button
          variant="outline"
          className="border-gold text-gold hover:bg-gold/10"
          onClick={handleDryRun}
          disabled={isSyncing}
        >
          <FlaskConical className="mr-2 h-4 w-4" />
          Dry Run
        </Button>
      </PageHeader>

      {/* KPI Grid */}
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4 mt-6">
        <KPICard label="Active Tunnels" value={dashboard?.activeTunnels ?? 0} />
        <KPICard label="Targets" value={dashboard?.totalPhoneLists ?? 0} />
        <KPICard label="Target Users" value={dashboard?.totalTargetUsers ?? 0} />
        <KPICard
          label="Last Sync"
          value={dashboard?.lastSync ? '' : 'No runs yet'}
          className={dashboard?.lastSync ? 'relative' : undefined}
        >
          {dashboard?.lastSync && (
            <StatusBadge status={dashboard.lastSync.status} />
          )}
        </KPICard>
      </div>

      {/* Live sync progress */}
      {isSyncing && pollingRun && (
        <SyncProgressCard run={pollingRun} />
      )}

      {/* Two-column grid */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6 mt-6">
        {/* Active Tunnels section */}
        <Card>
          <CardHeader>
            <CardTitle className="text-lg font-bold font-heading text-navy">
              Active Tunnels
            </CardTitle>
          </CardHeader>
          <CardContent>
            {tunnelsLoading ? (
              <div className="space-y-3">
                {[1, 2, 3].map((i) => (
                  <Skeleton key={i} className="h-10 w-full" />
                ))}
              </div>
            ) : activeTunnels.length === 0 ? (
              <EmptyState
                icon={Cable}
                heading="No tunnels configured"
                body="Create your first tunnel to start syncing contacts to targets."
                ctaLabel="Go to Tunnels"
                ctaHref="/tunnels"
              />
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full text-sm">
                  <thead>
                    <tr className="border-b border-border-default text-left text-xs font-normal uppercase tracking-wide text-text-muted">
                      <th className="pb-2 pr-4">Name</th>
                      <th className="pb-2 pr-4">Source</th>
                      <th className="pb-2 pr-4">Contacts</th>
                      <th className="pb-2">Status</th>
                    </tr>
                  </thead>
                  <tbody>
                    {activeTunnels.map((tunnel) => (
                      <tr
                        key={tunnel.id}
                        className="border-b border-border-default last:border-0 hover:bg-muted/50 cursor-pointer"
                        onClick={() => router.push(`/tunnels/${tunnel.id}`)}
                      >
                        <td className="py-2.5 pr-4 font-medium text-navy">
                          {tunnel.name}
                        </td>
                        <td className="py-2.5 pr-4 text-text-muted">
                          {tunnel.sources.map((s) => s.sourceDisplayName || s.sourceIdentifier).join(', ') || 'No sources'}
                        </td>
                        <td className="py-2.5 pr-4 text-text-muted">
                          {tunnel.estimatedContacts}
                        </td>
                        <td className="py-2.5">
                          <StatusBadge status={tunnel.status} />
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </CardContent>
        </Card>

        {/* Recent Runs section */}
        <Card>
          <CardHeader>
            <CardTitle className="text-lg font-bold font-heading text-navy">
              Recent Runs
            </CardTitle>
          </CardHeader>
          <CardContent>
            {!dashboard?.recentRuns || dashboard.recentRuns.length === 0 ? (
              <EmptyState
                icon={ClipboardList}
                heading="No sync runs yet"
                body="Run your first sync after configuring a tunnel."
              />
            ) : (
              <div className="space-y-1">
                {dashboard.recentRuns.slice(0, 5).map((run) => (
                  <div
                    key={run.id}
                    className="flex items-center gap-3 py-2.5 border-b border-border-default last:border-0 hover:bg-muted/50 cursor-pointer rounded-sm px-1"
                    onClick={() => router.push(`/runs/${run.id}`)}
                  >
                    <StatusBadge status={run.status} />
                    <span className="text-sm text-navy font-medium">
                      {formatRunType(run.runType)}
                    </span>
                    <span className="text-xs text-text-muted ml-auto">
                      {formatTimeAgo(run.startedAt)}
                    </span>
                    <span className="text-xs text-text-muted">
                      {run.contactsUpdated} updated
                    </span>
                  </div>
                ))}
              </div>
            )}
          </CardContent>
        </Card>
      </div>
    </div>
  );
}
