'use client';

import { useState } from 'react';
import { useRouter } from 'next/navigation';
import { toast } from 'sonner';
import { Cable, MoreHorizontal, Pencil, Power, Trash2 } from 'lucide-react';
import { useTunnels, useUpdateTunnelStatus, useDeleteTunnel } from '@/hooks/use-tunnels';
import { PageHeader } from '@/components/PageHeader';
import { DataTable } from '@/components/DataTable';
import { StatusBadge } from '@/components/StatusBadge';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { EmptyState } from '@/components/EmptyState';
import { Button } from '@/components/ui/button';
import {
  DropdownMenu,
  DropdownMenuTrigger,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
} from '@/components/ui/dropdown-menu';
import type { ColumnDef } from '@tanstack/react-table';
import type { TunnelDto } from '@/types/tunnel';

function formatRelativeTime(dateStr: string | null | undefined): string {
  if (!dateStr) return 'Never';
  const date = new Date(dateStr);
  const now = new Date();
  const diffMs = now.getTime() - date.getTime();
  const diffMins = Math.floor(diffMs / 60000);
  if (diffMins < 1) return 'Just now';
  if (diffMins < 60) return `${diffMins}m ago`;
  const diffHours = Math.floor(diffMins / 60);
  if (diffHours < 24) return `${diffHours}h ago`;
  const diffDays = Math.floor(diffHours / 24);
  return `${diffDays}d ago`;
}

function truncate(str: string, maxLen: number): string {
  return str.length > maxLen ? str.slice(0, maxLen) + '...' : str;
}

export default function TunnelsPage() {
  const router = useRouter();
  const { data: tunnels, isLoading } = useTunnels();
  const updateStatus = useUpdateTunnelStatus();
  const deleteT = useDeleteTunnel();
  const [deleteTarget, setDeleteTarget] = useState<{ id: number; name: string } | null>(null);

  const handleActivateDeactivate = (tunnel: TunnelDto) => {
    const newStatus = tunnel.status === 'active' ? 'inactive' : 'active';
    updateStatus.mutate(
      { id: tunnel.id, status: newStatus },
      {
        onSuccess: () => {
          toast.success(newStatus === 'active' ? 'Tunnel activated.' : 'Tunnel deactivated.');
        },
        onError: () => {
          toast.error('Something went wrong. Please try again.');
        },
      },
    );
  };

  const handleDelete = () => {
    if (!deleteTarget) return;
    deleteT.mutate(deleteTarget.id, {
      onSuccess: () => {
        toast.success('Tunnel deleted.');
        setDeleteTarget(null);
      },
      onError: () => {
        toast.error('Something went wrong. Please try again.');
        setDeleteTarget(null);
      },
    });
  };

  const columns: ColumnDef<TunnelDto, unknown>[] = [
    {
      accessorKey: 'name',
      header: 'Name',
      cell: ({ row }) => (
        <span className="font-medium">{row.original.name}</span>
      ),
    },
    {
      id: 'source',
      header: 'Source',
      accessorFn: (row) => row.sourceDisplayName || row.sourceIdentifier,
      cell: ({ getValue }) => {
        const val = getValue() as string;
        return (
          <span title={val} className="text-sm">
            {truncate(val, 30)}
          </span>
        );
      },
    },
    {
      accessorKey: 'estimatedContacts',
      header: () => <span className="text-right block">Contacts</span>,
      cell: ({ row }) => (
        <span className="text-right block">{row.original.estimatedContacts}</span>
      ),
    },
    {
      id: 'targetLists',
      header: 'Target Lists',
      accessorFn: (row) => row.targetLists.map((l) => l.name).join(', '),
      cell: ({ getValue }) => {
        const val = getValue() as string;
        return (
          <span title={val} className="text-sm">
            {truncate(val, 30)}
          </span>
        );
      },
    },
    {
      accessorKey: 'estimatedTargetUsers',
      header: () => <span className="text-right block">Users</span>,
      cell: ({ row }) => (
        <span className="text-right block">{row.original.estimatedTargetUsers}</span>
      ),
    },
    {
      id: 'lastRun',
      header: 'Last Run',
      accessorFn: (row) => row.lastSync?.completedAt,
      cell: ({ getValue }) => (
        <span className="text-sm text-text-muted">
          {formatRelativeTime(getValue() as string | null)}
        </span>
      ),
    },
    {
      id: 'status',
      header: 'Status',
      cell: ({ row }) => <StatusBadge status={row.original.status} />,
    },
    {
      id: 'actions',
      header: '',
      cell: ({ row }) => {
        const tunnel = row.original;
        return (
          <div onClick={(e) => e.stopPropagation()}>
            <DropdownMenu>
              <DropdownMenuTrigger
                render={<Button variant="ghost" size="icon" />}
              >
                <MoreHorizontal className="size-4" />
                <span className="sr-only">Actions</span>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                <DropdownMenuItem
                  onClick={() => router.push(`/tunnels/${tunnel.id}`)}
                >
                  <Pencil className="size-4 mr-2" />
                  Edit
                </DropdownMenuItem>
                <DropdownMenuItem onClick={() => handleActivateDeactivate(tunnel)}>
                  <Power className="size-4 mr-2" />
                  {tunnel.status === 'active' ? 'Deactivate' : 'Activate'}
                </DropdownMenuItem>
                <DropdownMenuSeparator />
                <DropdownMenuItem
                  onClick={() => setDeleteTarget({ id: tunnel.id, name: tunnel.name })}
                  className="text-destructive focus:text-destructive"
                >
                  <Trash2 className="size-4 mr-2" />
                  Delete
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          </div>
        );
      },
    },
  ];

  return (
    <div>
      <PageHeader
        title="Tunnels"
        description="Manage contact sync tunnels between DDGs and phone lists."
      />

      <DataTable
        columns={columns}
        data={tunnels ?? []}
        isLoading={isLoading}
        pageIndex={0}
        hasNextPage={false}
        onPageChange={() => {}}
        onRowClick={(row) => router.push(`/tunnels/${row.id}`)}
        emptyState={
          <EmptyState
            icon={Cable}
            heading="No tunnels yet"
            body="Tunnels connect Exchange DDGs to phone-visible contact folders."
          />
        }
      />

      <ConfirmDialog
        open={deleteTarget !== null}
        onOpenChange={(open) => {
          if (!open) setDeleteTarget(null);
        }}
        title="Delete tunnel"
        description={
          <>
            Are you sure you want to delete <strong>{deleteTarget?.name}</strong>? This action cannot be undone.
          </>
        }
        confirmLabel="Delete Tunnel"
        dismissLabel="Keep Tunnel"
        variant="destructive"
        onConfirm={handleDelete}
        isLoading={deleteT.isPending}
      />
    </div>
  );
}
