'use client';

import { useState } from 'react';
import { useRouter } from 'next/navigation';
import { useSyncRuns } from '@/hooks/use-sync-runs';
import { PageHeader } from '@/components/PageHeader';
import { DataTable } from '@/components/DataTable';
import { StatusBadge } from '@/components/StatusBadge';
import { EmptyState } from '@/components/EmptyState';
import { ClipboardList } from 'lucide-react';
import type { ColumnDef } from '@tanstack/react-table';
import type { SyncRunDto } from '@/types/sync-run';

function formatDuration(ms: number | null): string {
  if (ms === null || ms === undefined) return '--';
  if (ms < 1000) return '<1s';
  const totalSeconds = Math.floor(ms / 1000);
  if (totalSeconds < 60) return `${totalSeconds}s`;
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}m ${seconds}s`;
}

const columns: ColumnDef<SyncRunDto, unknown>[] = [
  {
    accessorKey: 'startedAt',
    header: 'Timestamp',
    cell: ({ getValue }) => {
      const value = getValue<string | null>();
      return value ? new Date(value).toLocaleString() : 'Pending';
    },
  },
  {
    accessorKey: 'runType',
    header: 'Type',
    cell: ({ row }) => {
      if (row.original.isDryRun) return 'Dry Run';
      const value = row.original.runType;
      return value.charAt(0).toUpperCase() + value.slice(1).toLowerCase();
    },
  },
  {
    accessorKey: 'durationMs',
    header: 'Duration',
    cell: ({ getValue }) => formatDuration(getValue<number | null>()),
  },
  {
    accessorKey: 'contactsCreated',
    header: () => <div className="text-right">Created</div>,
    cell: ({ getValue }) => {
      const value = getValue<number>();
      return (
        <div className={`text-right ${value > 0 ? 'text-emerald-700' : ''}`}>
          {value}
        </div>
      );
    },
  },
  {
    accessorKey: 'contactsUpdated',
    header: () => <div className="text-right">Updated</div>,
    cell: ({ getValue }) => (
      <div className="text-right">{getValue<number>()}</div>
    ),
  },
  {
    accessorKey: 'contactsRemoved',
    header: () => <div className="text-right">Removed</div>,
    cell: ({ getValue }) => (
      <div className="text-right">{getValue<number>()}</div>
    ),
  },
  {
    accessorKey: 'contactsSkipped',
    header: () => <div className="text-right">Skipped</div>,
    cell: ({ getValue }) => (
      <div className="text-right text-text-muted">{getValue<number>()}</div>
    ),
  },
  {
    accessorKey: 'contactsFailed',
    header: () => <div className="text-right">Failed</div>,
    cell: ({ getValue }) => {
      const value = getValue<number>();
      return (
        <div className={`text-right ${value > 0 ? 'text-red-700' : ''}`}>
          {value}
        </div>
      );
    },
  },
  {
    id: 'status',
    header: 'Status',
    cell: ({ row }) => (
      <div className="flex items-center gap-2">
        <StatusBadge status={row.original.status} />
        {row.original.isDryRun && <StatusBadge status="dry_run" />}
      </div>
    ),
  },
];

export default function RunsPage() {
  const [page, setPage] = useState(0);
  const pageSize = 25;
  const router = useRouter();
  const { data: rawData, isLoading } = useSyncRuns(page + 1, pageSize);

  const hasNextPage = (rawData?.length ?? 0) > pageSize;
  const data = rawData?.slice(0, pageSize) ?? [];

  return (
    <div>
      <PageHeader
        title="Runs & Logs"
        description="Review sync run history and per-item results."
      />
      <DataTable
        columns={columns}
        data={data}
        isLoading={isLoading}
        pageIndex={page}
        pageSize={pageSize}
        hasNextPage={hasNextPage}
        onPageChange={setPage}
        onRowClick={(row) => router.push(`/runs/${row.id}`)}
        emptyState={
          <EmptyState
            icon={ClipboardList}
            heading="No sync runs recorded"
            body="Sync runs will appear here after the first scheduled or manual run."
          />
        }
      />
    </div>
  );
}
