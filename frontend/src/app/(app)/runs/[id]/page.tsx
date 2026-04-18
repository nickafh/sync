'use client';

import { useState } from 'react';
import { useParams } from 'next/navigation';
import { useSyncRun, useSyncRunItems } from '@/hooks/use-sync-runs';
import { PageHeader } from '@/components/PageHeader';
import { KPICard } from '@/components/KPICard';
import { StatusBadge } from '@/components/StatusBadge';
import { DataTable } from '@/components/DataTable';
import { EmptyState } from '@/components/EmptyState';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { Skeleton } from '@/components/ui/skeleton';
import { Separator } from '@/components/ui/separator';
import { Search, ClipboardList } from 'lucide-react';
import type { ColumnDef } from '@tanstack/react-table';
import type { SyncRunItemDto } from '@/types/sync-run';

function formatDuration(ms: number | null): string {
  if (ms === null || ms === undefined) return '--';
  if (ms < 1000) return '<1s';
  const totalSeconds = Math.floor(ms / 1000);
  if (totalSeconds < 60) return `${totalSeconds}s`;
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}m ${seconds}s`;
}

const actionColorMap: Record<string, string> = {
  created: 'bg-green-100 text-green-800',
  updated: 'bg-blue-100 text-blue-800',
  failed: 'bg-red-100 text-red-800',
  removed: 'bg-orange-100 text-orange-800',
  skipped: 'bg-gray-100 text-gray-600',
  stale_detected: 'bg-yellow-100 text-yellow-800',
  photo_updated: 'bg-green-100 text-green-800',
  photo_failed: 'bg-red-100 text-red-800',
};

const actionLabelMap: Record<string, string> = {
  created: 'Created',
  updated: 'Updated',
  failed: 'Failed',
  removed: 'Removed',
  skipped: 'Skipped',
  stale_detected: 'Stale',
  photo_updated: 'Photo Updated',
  photo_failed: 'Photo Failed',
};

const itemColumns: ColumnDef<SyncRunItemDto, unknown>[] = [
  {
    accessorKey: 'tunnelId',
    header: 'Tunnel',
    cell: ({ getValue }) => getValue<number | null>() ?? '--',
  },
  {
    accessorKey: 'sourceUserName',
    header: 'Source User',
    cell: ({ row }) => row.original.sourceUserName ?? row.original.sourceUserId ?? '--',
  },
  {
    accessorKey: 'targetMailboxEmail',
    header: 'Mailbox',
    cell: ({ row }) => row.original.targetMailboxEmail ?? '--',
  },
  {
    accessorKey: 'action',
    header: 'Action',
    cell: ({ getValue }) => {
      const action = getValue<string>();
      const color = actionColorMap[action.toLowerCase()] ?? 'bg-gray-100 text-gray-600';
      const label = actionLabelMap[action.toLowerCase()] ?? action;
      return (
        <span className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${color}`}>
          {label}
        </span>
      );
    },
  },
  {
    accessorKey: 'fieldChanges',
    header: 'Field Changes',
    cell: ({ getValue }) => {
      const value = getValue<string | null>();
      if (!value) return '--';
      try {
        const parsed = JSON.parse(value);
        // Backend wraps changes in a { previousHash, fields } envelope — unwrap so
        // the count reflects real field changes instead of the two envelope keys.
        // Falls through cleanly if the payload is already flat.
        const isEnvelope =
          parsed && typeof parsed === 'object' && !Array.isArray(parsed) && 'fields' in parsed;
        const fields = isEnvelope ? parsed.fields : parsed;
        const keys =
          fields && typeof fields === 'object' && !Array.isArray(fields)
            ? Object.keys(fields)
            : [];
        const count = Array.isArray(fields) ? fields.length : keys.length;
        const title = keys.join(', ') || value;
        return (
          <span title={title} className="cursor-help">
            {count} field{count !== 1 ? 's' : ''} changed
          </span>
        );
      } catch {
        return (
          <span title={value} className="cursor-help">
            {value}
          </span>
        );
      }
    },
  },
  {
    accessorKey: 'errorMessage',
    header: 'Error',
    cell: ({ getValue }) => {
      const value = getValue<string | null>();
      if (!value) return '--';
      const truncated = value.length > 60 ? value.slice(0, 60) + '...' : value;
      return (
        <span className="text-red-600" title={value}>
          {truncated}
        </span>
      );
    },
  },
  {
    accessorKey: 'createdAt',
    header: 'Timestamp',
    cell: ({ getValue }) => {
      const value = getValue<string>();
      return new Date(value).toLocaleTimeString();
    },
  },
];

const ACTION_TABS = [
  { label: 'All', value: 'all' },
  { label: 'Created', value: 'created' },
  { label: 'Updated', value: 'updated' },
  { label: 'Failed', value: 'failed,photo_failed' },
  { label: 'Removed', value: 'removed' },
  { label: 'Skipped', value: 'skipped' },
  { label: 'Photo Updated', value: 'photo_updated' },
  { label: 'Photo Failed', value: 'photo_failed' },
] as const;

export default function RunDetailPage() {
  const { id } = useParams();
  const runId = Number(id);
  const isValidId = !!id && Number.isFinite(runId) && runId > 0;
  const { data: run, isLoading: runLoading } = useSyncRun(isValidId ? runId : 0);
  const [itemPage, setItemPage] = useState(0);
  const [actionFilter, setActionFilter] = useState<string | undefined>(undefined);
  const pageSize = 25;
  const { data: rawItems, isLoading: itemsLoading } = useSyncRunItems(
    runId,
    itemPage + 1,
    pageSize,
    actionFilter,
  );

  const hasNextPage = (rawItems?.length ?? 0) > pageSize;
  const items = rawItems?.slice(0, pageSize) ?? [];

  function handleActionFilterChange(value: string | number | null) {
    const stringVal = String(value);
    setActionFilter(stringVal === 'all' ? undefined : stringVal);
    setItemPage(0);
  }

  if (!isValidId) {
    return <div className="p-8 text-text-muted">Invalid run ID</div>;
  }

  if (runLoading) {
    return (
      <div>
        <div className="flex items-start justify-between mb-8">
          <div>
            <Skeleton className="h-10 w-48" />
            <Skeleton className="h-4 w-32 mt-2" />
          </div>
        </div>
        <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-6 gap-4 mt-6">
          {Array.from({ length: 6 }).map((_, i) => (
            <Skeleton key={i} className="h-20 rounded-xl" />
          ))}
        </div>
        <Skeleton className="h-40 rounded-xl mt-6" />
      </div>
    );
  }

  if (!run) {
    return (
      <div>
        <PageHeader title={`Run #${id}`} description="Run not found." />
      </div>
    );
  }

  return (
    <div>
      <PageHeader
        title={`Run #${run.id}`}
        description=""
      >
        <StatusBadge status={run.status} />
        {run.isDryRun && <StatusBadge status="dry_run" />}
      </PageHeader>

      {/* KPI Grid */}
      <div className="grid grid-cols-2 md:grid-cols-4 lg:grid-cols-7 gap-4 mt-6">
        <KPICard label="Created" value={run.contactsCreated} />
        <KPICard label="Updated" value={run.contactsUpdated} />
        <KPICard label="Removed" value={run.contactsRemoved} />
        <KPICard label="Skipped" value={run.contactsSkipped} />
        <KPICard
          label="Failed"
          value={run.contactsFailed}
          className={run.contactsFailed > 0 ? 'text-red-700' : ''}
        />
        <KPICard label="Photos" value={run.photosUpdated} />
        <KPICard
          label="Photos Failed"
          value={run.photosFailed}
          className={run.photosFailed > 0 ? 'text-red-700' : ''}
        />
      </div>

      {/* Summary Card */}
      <Card className="mt-6">
        <CardHeader>
          <CardTitle>Run Summary</CardTitle>
        </CardHeader>
        <CardContent>
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
            <div className="flex justify-between py-2">
              <span className="text-sm text-text-muted">Started</span>
              <span className="text-sm">
                {run.startedAt
                  ? new Date(run.startedAt).toLocaleString()
                  : 'Pending'}
              </span>
            </div>
            <div className="flex justify-between py-2">
              <span className="text-sm text-text-muted">Duration</span>
              <span className="text-sm">{formatDuration(run.durationMs)}</span>
            </div>
            <div className="flex justify-between py-2">
              <span className="text-sm text-text-muted">Tunnels Processed</span>
              <span className="text-sm">{run.tunnelsProcessed}</span>
            </div>
            <div className="flex justify-between py-2">
              <span className="text-sm text-text-muted">Tunnels with Warnings</span>
              <span
                className={`text-sm ${run.tunnelsWarned > 0 ? 'text-amber-700' : ''}`}
              >
                {run.tunnelsWarned}
              </span>
            </div>
            <div className="flex justify-between py-2">
              <span className="text-sm text-text-muted">Throttle Events</span>
              <span
                className={`text-sm ${run.throttleEvents > 0 ? 'text-amber-700' : ''}`}
              >
                {run.throttleEvents}
              </span>
            </div>
            <div className="flex justify-between items-center py-2">
              <span className="text-sm text-text-muted">Status</span>
              <StatusBadge status={run.status} />
            </div>
          </div>

          {/* Error Summary */}
          {run.errorSummary && (
            <>
              <Separator className="my-4" />
              <div className="bg-red-50 border border-red-200 rounded-lg p-4">
                <h4 className="text-red-700 font-bold text-sm">Error Summary</h4>
                <p className="text-red-600 text-sm mt-1">{run.errorSummary}</p>
              </div>
            </>
          )}
        </CardContent>
      </Card>

      {/* Per-Tunnel Breakdown */}
      {run.tunnelSummaries && run.tunnelSummaries.length > 0 && (
        <Card className="mt-6">
          <CardHeader>
            <CardTitle>Per-Tunnel Breakdown</CardTitle>
          </CardHeader>
          <CardContent>
            <div className="space-y-4">
              {run.tunnelSummaries.map((ts, idx) => (
                <div key={ts.tunnelId ?? idx}>
                  <div className="flex items-center justify-between">
                    <h4 className="font-medium text-sm">{ts.tunnelName}</h4>
                    {ts.contactsFailed > 0 && (
                      <span className="text-xs text-red-600 font-medium">
                        {ts.contactsFailed} failed
                      </span>
                    )}
                  </div>
                  <div className="grid grid-cols-3 md:grid-cols-6 gap-2 mt-2">
                    <div className="text-center">
                      <p className="text-xs text-text-muted">Created</p>
                      <p className="text-sm font-medium">{ts.contactsCreated}</p>
                    </div>
                    <div className="text-center">
                      <p className="text-xs text-text-muted">Updated</p>
                      <p className="text-sm font-medium">{ts.contactsUpdated}</p>
                    </div>
                    <div className="text-center">
                      <p className="text-xs text-text-muted">Removed</p>
                      <p className="text-sm font-medium">{ts.contactsRemoved}</p>
                    </div>
                    <div className="text-center">
                      <p className="text-xs text-text-muted">Skipped</p>
                      <p className="text-sm font-medium">{ts.contactsSkipped}</p>
                    </div>
                    <div className="text-center">
                      <p className="text-xs text-text-muted">Failed</p>
                      <p className={`text-sm font-medium ${ts.contactsFailed > 0 ? 'text-red-600' : ''}`}>
                        {ts.contactsFailed}
                      </p>
                    </div>
                    <div className="text-center">
                      <p className="text-xs text-text-muted">Photos</p>
                      <p className="text-sm font-medium">{ts.photosUpdated}</p>
                    </div>
                  </div>
                  {ts.errors.length > 0 && (
                    <div className="mt-2 bg-red-50 border border-red-200 rounded-lg p-3">
                      <p className="text-xs font-medium text-red-700 mb-1">Errors:</p>
                      <ul className="text-xs text-red-600 space-y-1">
                        {ts.errors.slice(0, 5).map((err, errIdx) => (
                          <li key={errIdx}>{err}</li>
                        ))}
                        {ts.errors.length > 5 && (
                          <li className="text-red-400">
                            ...and {ts.errors.length - 5} more
                          </li>
                        )}
                      </ul>
                    </div>
                  )}
                  {idx < run.tunnelSummaries.length - 1 && (
                    <Separator className="mt-4" />
                  )}
                </div>
              ))}
            </div>
          </CardContent>
        </Card>
      )}

      {/* Photo Sync Stats (D-10) */}
      {(run.photosUpdated > 0 || run.photosFailed > 0) && (
        <Card className="mt-6">
          <CardHeader>
            <CardTitle>Photo Sync</CardTitle>
          </CardHeader>
          <CardContent>
            <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
              <div className="text-center">
                <p className="text-xs text-text-muted">Photos Updated</p>
                <p className="text-lg font-bold text-navy">{run.photosUpdated}</p>
              </div>
              <div className="text-center">
                <p className="text-xs text-text-muted">Photos Failed</p>
                <p className={`text-lg font-bold ${run.photosFailed > 0 ? 'text-red-600' : 'text-navy'}`}>
                  {run.photosFailed}
                </p>
              </div>
            </div>
            {/* Per-tunnel photo breakdown */}
            {run.tunnelSummaries && run.tunnelSummaries.some(ts => ts.photosUpdated > 0 || ts.photosFailed > 0) && (
              <>
                <Separator className="my-4" />
                <h4 className="text-sm font-medium mb-3">Per-Tunnel Photos</h4>
                <div className="space-y-2">
                  {run.tunnelSummaries
                    .filter(ts => ts.photosUpdated > 0 || ts.photosFailed > 0)
                    .map((ts, idx) => (
                      <div key={ts.tunnelId ?? idx} className="flex items-center justify-between py-1">
                        <span className="text-sm">{ts.tunnelName}</span>
                        <div className="flex gap-4 text-sm">
                          <span className="text-green-700">{ts.photosUpdated} updated</span>
                          {ts.photosFailed > 0 && (
                            <span className="text-red-600">{ts.photosFailed} failed</span>
                          )}
                        </div>
                      </div>
                    ))}
                </div>
              </>
            )}
          </CardContent>
        </Card>
      )}

      {/* Action Filter Tabs */}
      <div className="mt-6">
        <Tabs
          value={actionFilter ?? 'all'}
          onValueChange={handleActionFilterChange}
        >
          <TabsList variant="line">
            {ACTION_TABS.map((tab) => (
              <TabsTrigger
                key={tab.value}
                value={tab.value}
                className="data-[active]:border-b-2 data-[active]:border-gold"
              >
                {tab.label}
              </TabsTrigger>
            ))}
          </TabsList>
        </Tabs>
      </div>

      {/* Items DataTable */}
      <div className="mt-4">
        <DataTable
          columns={itemColumns}
          data={items}
          isLoading={itemsLoading}
          pageIndex={itemPage}
          pageSize={pageSize}
          hasNextPage={hasNextPage}
          onPageChange={setItemPage}
          emptyState={
            actionFilter ? (
              <EmptyState
                icon={Search}
                heading="No matching items"
                body="No items match the selected filter. Try a different action type."
              />
            ) : (
              <EmptyState
                icon={ClipboardList}
                heading="No items recorded"
                body="This run has no individual item records."
              />
            )
          }
        />
      </div>
    </div>
  );
}
