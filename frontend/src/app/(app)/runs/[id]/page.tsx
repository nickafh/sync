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

const actionStatusMap: Record<string, string> = {
  created: 'success',
  updated: 'active',
  failed: 'failed',
  removed: 'warning',
  skipped: 'inactive',
};

const itemColumns: ColumnDef<SyncRunItemDto, unknown>[] = [
  {
    accessorKey: 'tunnelId',
    header: 'Tunnel',
    cell: ({ getValue }) => getValue<number | null>() ?? '--',
  },
  {
    accessorKey: 'sourceUserId',
    header: 'Source User',
    cell: ({ getValue }) => getValue<number | null>() ?? '--',
  },
  {
    accessorKey: 'action',
    header: 'Action',
    cell: ({ getValue }) => {
      const action = getValue<string>();
      const status = actionStatusMap[action.toLowerCase()] ?? action;
      return <StatusBadge status={status} />;
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
        const count = Array.isArray(parsed) ? parsed.length : Object.keys(parsed).length;
        return (
          <span title={value} className="cursor-help">
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
  { label: 'Failed', value: 'failed' },
  { label: 'Removed', value: 'removed' },
  { label: 'Skipped', value: 'skipped' },
] as const;

export default function RunDetailPage() {
  const { id } = useParams();
  const runId = Number(id);
  const { data: run, isLoading: runLoading } = useSyncRun(runId);
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
      <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-6 gap-4 mt-6">
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
