'use client';

import { useState, useEffect } from 'react';
import { useParams, useRouter } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import { toast } from 'sonner';
import {
  useTunnel,
  useTunnels,
  useUpdateTunnel,
  useUpdateTunnelStatus,
  useDeleteTunnel,
} from '@/hooks/use-tunnels';
import { api } from '@/lib/api';
import { PageHeader } from '@/components/PageHeader';
import { KPICard } from '@/components/KPICard';
import { StatusBadge } from '@/components/StatusBadge';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { DDGPicker } from '@/components/DDGPicker';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { Separator } from '@/components/ui/separator';
import type { DdgDto } from '@/types/ddg';
import type { UpdateTunnelRequest } from '@/types/tunnel';

const stalePolicyOptions = [
  { value: 'auto_remove', label: 'Auto Remove' },
  { value: 'flag_hold', label: 'Flag & Hold' },
  { value: 'leave', label: 'Leave in Place' },
];

function formatStalePolicy(policy: string): string {
  const found = stalePolicyOptions.find((o) => o.value === policy);
  return found?.label ?? policy;
}

export default function TunnelDetailPage() {
  const { id } = useParams();
  const router = useRouter();
  const tunnelId = Number(id);

  const { data: tunnel, isLoading } = useTunnel(tunnelId);
  const { data: tunnels } = useTunnels();
  const tunnelListData = tunnels?.find((t) => t.id === tunnelId);

  const updateTunnel = useUpdateTunnel();
  const updateStatus = useUpdateTunnelStatus();
  const deleteTunnel = useDeleteTunnel();

  const { data: phoneLists } = useQuery({
    queryKey: ['phone-lists'],
    queryFn: () => api.phoneLists.list(),
    staleTime: 5 * 60 * 1000,
  });

  const { data: fieldProfiles } = useQuery({
    queryKey: ['field-profiles'],
    queryFn: () => api.fieldProfiles.list(),
    staleTime: 5 * 60 * 1000,
  });

  const [isEditing, setIsEditing] = useState(false);
  const [ddgPickerOpen, setDdgPickerOpen] = useState(false);
  const [deleteDialogOpen, setDeleteDialogOpen] = useState(false);
  const [deactivateDialogOpen, setDeactivateDialogOpen] = useState(false);

  const [editForm, setEditForm] = useState<UpdateTunnelRequest>({
    name: '',
    sourceType: '',
    sourceIdentifier: '',
    sourceDisplayName: null,
    sourceSmtpAddress: null,
    targetScope: '',
    targetListIds: [],
    fieldProfileId: null,
    stalePolicy: '',
    staleDays: 14,
  });

  useEffect(() => {
    if (tunnel && !isEditing) {
      setEditForm({
        name: tunnel.name,
        sourceType: tunnel.sourceType,
        sourceIdentifier: tunnel.sourceIdentifier,
        sourceDisplayName: tunnel.sourceDisplayName,
        sourceSmtpAddress: tunnel.sourceSmtpAddress,
        targetScope: tunnel.targetScope,
        targetListIds: tunnel.targetLists.map((l) => l.id),
        fieldProfileId: tunnel.fieldProfileId,
        stalePolicy: tunnel.stalePolicy,
        staleDays: tunnel.staleHoldDays,
      });
    }
  }, [tunnel, isEditing]);

  const enterEditMode = () => {
    if (!tunnel) return;
    setEditForm({
      name: tunnel.name,
      sourceType: tunnel.sourceType,
      sourceIdentifier: tunnel.sourceIdentifier,
      sourceDisplayName: tunnel.sourceDisplayName,
      sourceSmtpAddress: tunnel.sourceSmtpAddress,
      targetScope: tunnel.targetScope,
      targetListIds: tunnel.targetLists.map((l) => l.id),
      fieldProfileId: tunnel.fieldProfileId,
      stalePolicy: tunnel.stalePolicy,
      staleDays: tunnel.staleHoldDays,
    });
    setIsEditing(true);
  };

  const discardChanges = () => {
    if (!tunnel) return;
    setEditForm({
      name: tunnel.name,
      sourceType: tunnel.sourceType,
      sourceIdentifier: tunnel.sourceIdentifier,
      sourceDisplayName: tunnel.sourceDisplayName,
      sourceSmtpAddress: tunnel.sourceSmtpAddress,
      targetScope: tunnel.targetScope,
      targetListIds: tunnel.targetLists.map((l) => l.id),
      fieldProfileId: tunnel.fieldProfileId,
      stalePolicy: tunnel.stalePolicy,
      staleDays: tunnel.staleHoldDays,
    });
    setIsEditing(false);
  };

  const handleSave = () => {
    updateTunnel.mutate(
      { id: tunnelId, data: editForm },
      {
        onSuccess: () => {
          toast.success('Tunnel updated successfully.');
          setIsEditing(false);
        },
        onError: () => {
          toast.error('Something went wrong. Please try again.');
        },
      },
    );
  };

  const handleDdgSelect = (ddg: DdgDto) => {
    setEditForm((prev) => ({
      ...prev,
      sourceIdentifier: ddg.graphFilter ?? ddg.recipientFilter,
      sourceDisplayName: ddg.displayName,
      sourceSmtpAddress: ddg.primarySmtpAddress,
    }));
  };

  const handleActivate = () => {
    updateStatus.mutate(
      { id: tunnelId, status: 'active' },
      {
        onSuccess: () => toast.success('Tunnel activated.'),
        onError: () => toast.error('Something went wrong. Please try again.'),
      },
    );
  };

  const handleDeactivate = () => {
    updateStatus.mutate(
      { id: tunnelId, status: 'inactive' },
      {
        onSuccess: () => {
          toast.success('Tunnel deactivated.');
          setDeactivateDialogOpen(false);
        },
        onError: () => {
          toast.error('Something went wrong. Please try again.');
          setDeactivateDialogOpen(false);
        },
      },
    );
  };

  const handleDelete = () => {
    deleteTunnel.mutate(tunnelId, {
      onSuccess: () => {
        toast.success('Tunnel deleted.');
        router.push('/tunnels');
      },
      onError: () => {
        toast.error('Something went wrong. Please try again.');
        setDeleteDialogOpen(false);
      },
    });
  };

  const toggleTargetList = (listId: number) => {
    setEditForm((prev) => ({
      ...prev,
      targetListIds: prev.targetListIds.includes(listId)
        ? prev.targetListIds.filter((id) => id !== listId)
        : [...prev.targetListIds, listId],
    }));
  };

  if (isLoading) {
    return (
      <div>
        <div className="flex items-start justify-between mb-8">
          <div>
            <Skeleton className="h-9 w-48" />
            <Skeleton className="h-4 w-32 mt-2" />
          </div>
          <div className="flex gap-3">
            <Skeleton className="h-8 w-16" />
            <Skeleton className="h-8 w-24" />
            <Skeleton className="h-8 w-16" />
          </div>
        </div>
        <div className="grid grid-cols-1 md:grid-cols-3 gap-4 mt-6">
          <Skeleton className="h-20" />
          <Skeleton className="h-20" />
          <Skeleton className="h-20" />
        </div>
        <div className="space-y-6 mt-6">
          <Skeleton className="h-32" />
          <Skeleton className="h-32" />
          <Skeleton className="h-32" />
        </div>
      </div>
    );
  }

  if (!tunnel) {
    return (
      <div className="text-center py-16">
        <p className="text-text-muted">Tunnel not found.</p>
      </div>
    );
  }

  return (
    <div>
      <PageHeader
        title={isEditing ? editForm.name : tunnel.name}
        description=""
      >
        <StatusBadge status={tunnel.status} />
        {isEditing ? (
          <>
            <Button
              className="bg-gold text-white hover:bg-gold/90"
              onClick={handleSave}
              disabled={updateTunnel.isPending}
            >
              {updateTunnel.isPending ? 'Saving...' : 'Save Changes'}
            </Button>
            <Button variant="outline" onClick={discardChanges}>
              Discard Changes
            </Button>
          </>
        ) : (
          <>
            <Button variant="outline" onClick={enterEditMode}>
              Edit
            </Button>
            {tunnel.status === 'active' ? (
              <Button
                variant="outline"
                onClick={() => setDeactivateDialogOpen(true)}
              >
                Deactivate
              </Button>
            ) : (
              <Button variant="outline" onClick={handleActivate}>
                Activate
              </Button>
            )}
            <Button
              variant="destructive"
              onClick={() => setDeleteDialogOpen(true)}
            >
              Delete
            </Button>
          </>
        )}
      </PageHeader>

      {/* KPI Grid */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4 mt-6">
        <KPICard
          label="Source Contacts"
          value={tunnelListData?.estimatedContacts ?? '--'}
        />
        <KPICard
          label="Target Lists"
          value={tunnel.targetLists.length ?? '--'}
        />
        <KPICard
          label="Target Users"
          value={tunnelListData?.estimatedTargetUsers ?? '--'}
        />
      </div>

      {/* Detail Sections */}
      <div className="space-y-6 mt-6">
        {/* Source Card */}
        <Card>
          <CardHeader>
            <CardTitle>Source</CardTitle>
          </CardHeader>
          <CardContent>
            {isEditing ? (
              <div className="space-y-4">
                <div>
                  <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                    Name
                  </label>
                  <Input
                    value={editForm.name}
                    onChange={(e) =>
                      setEditForm((prev) => ({ ...prev, name: e.target.value }))
                    }
                    className="mt-1"
                  />
                </div>
                <Separator />
                <div>
                  <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                    DDG Source
                  </label>
                  <div className="mt-1 flex items-center gap-3">
                    <div className="flex-1">
                      <p className="font-medium">
                        {editForm.sourceDisplayName || editForm.sourceIdentifier}
                      </p>
                      {editForm.sourceSmtpAddress && (
                        <p className="text-sm text-text-muted">
                          {editForm.sourceSmtpAddress}
                        </p>
                      )}
                    </div>
                    <Button
                      variant="outline"
                      onClick={() => setDdgPickerOpen(true)}
                    >
                      Change Source
                    </Button>
                  </div>
                </div>
                <div>
                  <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                    Graph Filter
                  </label>
                  <p className="text-sm text-text-muted mt-1 font-mono text-xs break-all">
                    {editForm.sourceIdentifier}
                  </p>
                </div>
              </div>
            ) : (
              <div className="space-y-3">
                <div>
                  <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                    DDG Name
                  </label>
                  <p className="mt-1 font-medium">
                    {tunnel.sourceDisplayName || tunnel.sourceIdentifier}
                  </p>
                </div>
                {tunnel.sourceSmtpAddress && (
                  <div>
                    <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                      SMTP Address
                    </label>
                    <p className="mt-1 text-sm">{tunnel.sourceSmtpAddress}</p>
                  </div>
                )}
                <div>
                  <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                    Graph Filter
                  </label>
                  <p className="mt-1 text-sm font-mono text-xs text-text-muted break-all">
                    {tunnel.sourceIdentifier}
                  </p>
                </div>
              </div>
            )}
          </CardContent>
        </Card>

        {/* Targets Card */}
        <Card>
          <CardHeader>
            <CardTitle>Targets</CardTitle>
          </CardHeader>
          <CardContent>
            {isEditing ? (
              <div className="space-y-2">
                <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                  Target Phone Lists
                </label>
                <div className="space-y-2 mt-1">
                  {phoneLists?.map((list) => (
                    <label
                      key={list.id}
                      className="flex items-center gap-2 cursor-pointer"
                    >
                      <input
                        type="checkbox"
                        checked={editForm.targetListIds.includes(list.id)}
                        onChange={() => toggleTargetList(list.id)}
                        className="rounded border-gray-300"
                      />
                      <span className="text-sm">{list.name}</span>
                    </label>
                  ))}
                  {!phoneLists?.length && (
                    <p className="text-sm text-text-muted">
                      No phone lists available.
                    </p>
                  )}
                </div>
              </div>
            ) : (
              <div>
                <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                  Target Phone Lists
                </label>
                <div className="flex flex-wrap gap-2 mt-2">
                  {tunnel.targetLists.length > 0 ? (
                    tunnel.targetLists.map((list) => (
                      <span
                        key={list.id}
                        className="inline-flex items-center rounded-full bg-gray-100 px-3 py-1 text-sm text-gray-700"
                      >
                        {list.name}
                      </span>
                    ))
                  ) : (
                    <p className="text-sm text-text-muted">
                      No target lists assigned.
                    </p>
                  )}
                </div>
              </div>
            )}
          </CardContent>
        </Card>

        {/* Configuration Card */}
        <Card>
          <CardHeader>
            <CardTitle>Configuration</CardTitle>
          </CardHeader>
          <CardContent>
            {isEditing ? (
              <div className="space-y-4">
                <div>
                  <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                    Field Profile
                  </label>
                  <Select
                    value={editForm.fieldProfileId ?? undefined}
                    onValueChange={(val: number | null) =>
                      setEditForm((prev) => ({
                        ...prev,
                        fieldProfileId: val,
                      }))
                    }
                  >
                    <SelectTrigger className="w-full mt-1">
                      <SelectValue placeholder="Select a field profile" />
                    </SelectTrigger>
                    <SelectContent>
                      {fieldProfiles?.map((fp) => (
                        <SelectItem key={fp.id} value={fp.id}>
                          {fp.name}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                </div>
                <div>
                  <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                    Stale Policy
                  </label>
                  <Select
                    value={editForm.stalePolicy}
                    onValueChange={(val: string | null) =>
                      setEditForm((prev) => ({
                        ...prev,
                        stalePolicy: val ?? prev.stalePolicy,
                      }))
                    }
                  >
                    <SelectTrigger className="w-full mt-1">
                      <SelectValue placeholder="Select stale policy" />
                    </SelectTrigger>
                    <SelectContent>
                      {stalePolicyOptions.map((opt) => (
                        <SelectItem key={opt.value} value={opt.value}>
                          {opt.label}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                </div>
                <div>
                  <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                    Hold Days
                  </label>
                  <Input
                    type="number"
                    value={editForm.staleDays}
                    onChange={(e) =>
                      setEditForm((prev) => ({
                        ...prev,
                        staleDays: parseInt(e.target.value, 10) || 0,
                      }))
                    }
                    disabled={editForm.stalePolicy !== 'flag_hold'}
                    className="mt-1 w-32"
                  />
                </div>
              </div>
            ) : (
              <div className="space-y-3">
                <div>
                  <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                    Field Profile
                  </label>
                  <p className="mt-1 text-sm">
                    {tunnel.fieldProfileName ?? 'Not assigned'}
                  </p>
                </div>
                <div>
                  <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                    Stale Policy
                  </label>
                  <p className="mt-1 text-sm">
                    {formatStalePolicy(tunnel.stalePolicy)}
                  </p>
                </div>
                <div>
                  <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                    Hold Days
                  </label>
                  <p className="mt-1 text-sm">{tunnel.staleHoldDays}</p>
                </div>
              </div>
            )}
          </CardContent>
        </Card>
      </div>

      {/* DDG Picker Dialog */}
      <DDGPicker
        open={ddgPickerOpen}
        onOpenChange={setDdgPickerOpen}
        onSelect={handleDdgSelect}
      />

      {/* Deactivate Confirmation Dialog */}
      <ConfirmDialog
        open={deactivateDialogOpen}
        onOpenChange={setDeactivateDialogOpen}
        title="Deactivate tunnel"
        description={
          <>
            Are you sure you want to deactivate{' '}
            <strong>{tunnel.name}</strong>? It will stop syncing until
            reactivated.
          </>
        }
        confirmLabel="Deactivate Tunnel"
        dismissLabel="Keep Active"
        variant="destructive"
        onConfirm={handleDeactivate}
        isLoading={updateStatus.isPending}
      />

      {/* Delete Confirmation Dialog */}
      <ConfirmDialog
        open={deleteDialogOpen}
        onOpenChange={setDeleteDialogOpen}
        title="Delete tunnel"
        description={
          <>
            Are you sure you want to delete{' '}
            <strong>{tunnel.name}</strong>? This action cannot be undone.
          </>
        }
        confirmLabel="Delete Tunnel"
        dismissLabel="Keep Tunnel"
        variant="destructive"
        onConfirm={handleDelete}
        isLoading={deleteTunnel.isPending}
      />
    </div>
  );
}
