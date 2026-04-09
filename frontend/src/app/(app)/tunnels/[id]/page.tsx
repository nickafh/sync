'use client';

import React, { useState, useEffect, useRef } from 'react';
import { useParams, useRouter } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import { toast } from 'sonner';
import {
  useTunnel,
  useTunnels,
  useUpdateTunnel,
  useUpdateTunnelStatus,
  useDeleteTunnel,
  usePreviewTunnelImpact,
} from '@/hooks/use-tunnels';
import { api } from '@/lib/api';
import { PageHeader } from '@/components/PageHeader';
import { KPICard } from '@/components/KPICard';
import { StatusBadge } from '@/components/StatusBadge';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { ImpactPreviewDialog } from '@/components/ImpactPreviewDialog';
import { DDGRefreshButton } from '@/components/DDGRefreshButton';
import { DDGPicker } from '@/components/DDGPicker';
import { OrgContactManager } from '@/components/OrgContactManager';
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
import { Switch } from '@/components/ui/switch';
import { Label } from '@/components/ui/label';
import type { DdgDto } from '@/types/ddg';
import type {
  UpdateTunnelRequest,
  TunnelDetailDto,
  ImpactPreviewResponse,
  SourceInput,
  SecurityGroupDto,
  UserSearchResult,
} from '@/types/tunnel';
import type { StalePolicy } from '@/types/common';
import { X } from 'lucide-react';

const stalePolicyOptions = [
  { value: 'auto_remove', label: 'Auto Remove' },
  { value: 'flag_hold', label: 'Flag & Hold' },
  { value: 'leave', label: 'Leave in Place' },
];

function formatStalePolicy(policy: string): string {
  const found = stalePolicyOptions.find((o) => o.value === policy);
  return found?.label ?? policy;
}

function isHighImpactChange(
  original: TunnelDetailDto,
  edited: UpdateTunnelRequest,
): boolean {
  // Source change: compare source identifiers
  const origSourceIds = original.sources.map((s) => s.sourceIdentifier).sort();
  const editSourceIds = edited.sources.map((s) => s.sourceIdentifier).sort();
  if (origSourceIds.length !== editSourceIds.length || origSourceIds.some((id, i) => id !== editSourceIds[i])) return true;
  // Target list removal (any original target not in new targets)
  const originalIds = new Set(original.targetLists.map((l) => l.id));
  for (const id of originalIds) {
    if (!edited.targetListIds.includes(id)) return true;
  }
  return false;
}

export default function TunnelDetailPage() {
  const { id } = useParams();
  const router = useRouter();
  const tunnelId = Number(id);
  const isValidId = !!id && Number.isFinite(tunnelId) && tunnelId > 0;

  const { data: tunnel, isLoading } = useTunnel(isValidId ? tunnelId : 0);
  const { data: tunnels } = useTunnels();
  const tunnelListData = tunnels?.find((t) => t.id === tunnelId);

  const updateTunnel = useUpdateTunnel();
  const updateStatus = useUpdateTunnelStatus();
  const deleteTunnel = useDeleteTunnel();
  const previewImpact = usePreviewTunnelImpact();

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

  const { data: securityGroups, isLoading: groupsLoading } = useQuery({
    queryKey: ['security-groups'],
    queryFn: () => api.securityGroups.list(),
    staleTime: 10 * 60 * 1000,
    enabled: isEditing,
  });
  const [ddgPickerOpen, setDdgPickerOpen] = useState(false);
  const [addSourceType, setAddSourceType] = useState<'ddg' | 'mailbox_contacts' | 'org_contacts'>('ddg');
  const [mailboxEmailInput, setMailboxEmailInput] = useState('');
  const [deleteDialogOpen, setDeleteDialogOpen] = useState(false);
  const [deactivateDialogOpen, setDeactivateDialogOpen] = useState(false);
  const [impactDialogOpen, setImpactDialogOpen] = useState(false);
  const [impactData, setImpactData] = useState<ImpactPreviewResponse | null>(
    null,
  );
  const [fallbackConfirmOpen, setFallbackConfirmOpen] = useState(false);
  const [resetHashesDialogOpen, setResetHashesDialogOpen] = useState(false);
  const [resettingHashes, setResettingHashes] = useState(false);

  const [editForm, setEditForm] = useState<UpdateTunnelRequest>({
    name: '',
    sources: [],
    targetListIds: [],
    fieldProfileId: null,
    stalePolicy: 'auto_remove' as const,
    staleDays: 14,
    photoSyncEnabled: true,
    targetGroupId: null,
    targetGroupName: null,
    targetUserEmails: null,
  });

  // User search state for "Specific users" mode
  const [userSearchQuery, setUserSearchQuery] = useState('');
  const [userSearchResults, setUserSearchResults] = useState<UserSearchResult[]>([]);
  const [userSearching, setUserSearching] = useState(false);
  const searchTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const editTargetUserEmails: string[] = editForm.targetUserEmails
    ? (() => { try { return JSON.parse(editForm.targetUserEmails); } catch { return []; } })()
    : [];

  const editScopeMode: 'all' | 'group' | 'specific' =
    editTargetUserEmails.length > 0 ? 'specific' :
    editForm.targetGroupId ? 'group' : 'all';

  // Debounced user search for specific-users mode
  useEffect(() => {
    if (userSearchQuery.length < 2) {
      setUserSearchResults([]);
      return;
    }
    if (searchTimeoutRef.current) clearTimeout(searchTimeoutRef.current);
    searchTimeoutRef.current = setTimeout(async () => {
      setUserSearching(true);
      try {
        const results = await api.users.search(userSearchQuery);
        setUserSearchResults(
          results.filter((u) => !editTargetUserEmails.includes(u.email))
        );
      } catch {
        setUserSearchResults([]);
      } finally {
        setUserSearching(false);
      }
    }, 300);
    return () => { if (searchTimeoutRef.current) clearTimeout(searchTimeoutRef.current); };
  }, [userSearchQuery, editTargetUserEmails]);

  useEffect(() => {
    if (tunnel && !isEditing) {
      setEditForm({
        name: tunnel.name,
        sources: tunnel.sources.map((s) => ({
          sourceType: s.sourceType,
          sourceIdentifier: s.sourceIdentifier,
          sourceDisplayName: s.sourceDisplayName,
          sourceSmtpAddress: s.sourceSmtpAddress,
          sourceFilterPlain: s.sourceFilterPlain,
        })),
        targetListIds: tunnel.targetLists.map((l) => l.id),
        fieldProfileId: tunnel.fieldProfileId,
        stalePolicy: tunnel.stalePolicy,
        staleDays: tunnel.staleHoldDays,
        photoSyncEnabled: tunnel.photoSyncEnabled,
        targetGroupId: tunnel.targetGroupId,
        targetGroupName: tunnel.targetGroupName,
        targetUserEmails: tunnel.targetUserEmails,
      });
    }
  }, [tunnel, isEditing]);

  const enterEditMode = () => {
    if (!tunnel) return;
    setEditForm({
      name: tunnel.name,
      sources: tunnel.sources.map((s) => ({
        sourceType: s.sourceType,
        sourceIdentifier: s.sourceIdentifier,
        sourceDisplayName: s.sourceDisplayName,
        sourceSmtpAddress: s.sourceSmtpAddress,
        sourceFilterPlain: s.sourceFilterPlain,
      })),
      targetListIds: tunnel.targetLists.map((l) => l.id),
      fieldProfileId: tunnel.fieldProfileId,
      stalePolicy: tunnel.stalePolicy,
      staleDays: tunnel.staleHoldDays,
      photoSyncEnabled: tunnel.photoSyncEnabled,
      targetGroupId: tunnel.targetGroupId,
      targetGroupName: tunnel.targetGroupName,
    });
    setIsEditing(true);
  };

  const discardChanges = () => {
    if (!tunnel) return;
    setEditForm({
      name: tunnel.name,
      sources: tunnel.sources.map((s) => ({
        sourceType: s.sourceType,
        sourceIdentifier: s.sourceIdentifier,
        sourceDisplayName: s.sourceDisplayName,
        sourceSmtpAddress: s.sourceSmtpAddress,
        sourceFilterPlain: s.sourceFilterPlain,
      })),
      targetListIds: tunnel.targetLists.map((l) => l.id),
      fieldProfileId: tunnel.fieldProfileId,
      stalePolicy: tunnel.stalePolicy,
      staleDays: tunnel.staleHoldDays,
      photoSyncEnabled: tunnel.photoSyncEnabled,
      targetGroupId: tunnel.targetGroupId,
      targetGroupName: tunnel.targetGroupName,
    });
    setIsEditing(false);
  };

  const doSave = () => {
    updateTunnel.mutate(
      { id: tunnelId, data: editForm },
      {
        onSuccess: () => {
          toast.success('Tunnel updated successfully.');
          setIsEditing(false);
          setImpactDialogOpen(false);
          setFallbackConfirmOpen(false);
        },
        onError: () => {
          toast.error('Something went wrong. Please try again.');
        },
      },
    );
  };

  const handleSave = () => {
    if (!tunnel) return;
    if (isHighImpactChange(tunnel, editForm)) {
      // High-impact: fetch preview first
      previewImpact.mutate(
        { id: tunnelId, data: editForm },
        {
          onSuccess: (data) => {
            setImpactData(data);
            setImpactDialogOpen(true);
          },
          onError: () => {
            // Preview failed, show fallback confirm
            setFallbackConfirmOpen(true);
          },
        },
      );
    } else {
      // Low-impact: save directly (existing behavior)
      doSave();
    }
  };

  const handleResetHashes = async () => {
    setResettingHashes(true);
    try {
      const result = await api.tunnels.resetHashes(tunnelId);
      toast.success(`Reset ${result.count} contact states. Run a sync to apply changes.`);
      setResetHashesDialogOpen(false);
    } catch {
      toast.error('Failed to reset hashes. Please try again.');
    } finally {
      setResettingHashes(false);
    }
  };

  const handleDdgSelect = (ddg: DdgDto) => {
    const newSource: SourceInput = {
      sourceType: 'ddg',
      sourceIdentifier: ddg.graphFilter ?? ddg.recipientFilter,
      sourceDisplayName: ddg.displayName,
      sourceSmtpAddress: ddg.primarySmtpAddress,
      sourceFilterPlain: ddg.recipientFilterPlain,
    };
    setEditForm((prev) => ({
      ...prev,
      sources: [...prev.sources, newSource],
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

  if (!isValidId) {
    return <div className="p-8 text-text-muted">Invalid tunnel ID</div>;
  }

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
              disabled={previewImpact.isPending || updateTunnel.isPending}
            >
              {previewImpact.isPending
                ? 'Checking impact...'
                : updateTunnel.isPending
                  ? 'Saving...'
                  : 'Save Changes'}
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
            <div className="flex items-center justify-between">
              <CardTitle>Sources</CardTitle>
              {isEditing && (
                <div className="flex items-center gap-2">
                  <Select
                    value={addSourceType}
                    onValueChange={(val) => { if (val) setAddSourceType(val as 'ddg' | 'mailbox_contacts' | 'org_contacts'); }}
                  >
                    <SelectTrigger className="w-[180px] h-8 text-sm">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="ddg">DDG</SelectItem>
                      <SelectItem value="mailbox_contacts">Shared Mailbox</SelectItem>
                      <SelectItem value="org_contacts">Org Contacts</SelectItem>
                    </SelectContent>
                  </Select>
                  <Button
                    variant="outline"
                    size="sm"
                    onClick={() => {
                      if (addSourceType === 'org_contacts') {
                        // Org contacts don't need a picker — add directly
                        setEditForm((prev) => ({
                          ...prev,
                          sources: [
                            ...prev.sources,
                            {
                              sourceType: 'org_contacts',
                              sourceIdentifier: 'all',
                              sourceDisplayName: 'Organization Contacts',
                              sourceSmtpAddress: null,
                              sourceFilterPlain: null,
                            },
                          ],
                        }));
                        return;
                      }
                      if (addSourceType === 'mailbox_contacts') {
                        setMailboxEmailInput('');
                      }
                      setDdgPickerOpen(true);
                    }}
                  >
                    Add Source
                  </Button>
                </div>
              )}
            </div>
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
                {editForm.sources.map((src, idx) => (
                  <div key={idx} className="space-y-2 border rounded-md p-3">
                    <div className="flex items-center justify-between">
                      <div>
                        <span className="inline-flex items-center rounded-full bg-gray-100 px-2 py-0.5 text-xs text-gray-600 mr-2">
                          {src.sourceType === 'mailbox_contacts' ? 'Shared Mailbox' : src.sourceType === 'org_contacts' ? 'Org Contacts' : 'DDG'}
                        </span>
                        <span className="font-medium">
                          {src.sourceDisplayName || src.sourceIdentifier}
                        </span>
                      </div>
                      <Button
                        variant="ghost"
                        size="sm"
                        className="text-destructive"
                        onClick={() =>
                          setEditForm((prev) => ({
                            ...prev,
                            sources: prev.sources.filter((_, i) => i !== idx),
                          }))
                        }
                        disabled={editForm.sources.length <= 1}
                      >
                        Remove
                      </Button>
                    </div>
                    {src.sourceType === 'mailbox_contacts' ? (
                      <p className="text-sm text-text-muted">{src.sourceIdentifier}</p>
                    ) : (
                      <>
                        {src.sourceSmtpAddress && (
                          <p className="text-sm text-text-muted">{src.sourceSmtpAddress}</p>
                        )}
                        {src.sourceFilterPlain && (
                          <p className="text-sm text-text-muted">{src.sourceFilterPlain}</p>
                        )}
                        <p className="text-sm text-text-muted font-mono text-xs break-all">
                          {src.sourceIdentifier}
                        </p>
                      </>
                    )}
                  </div>
                ))}
                {editForm.sources.length === 0 && (
                  <p className="text-sm text-text-muted">No sources. Add at least one source.</p>
                )}
              </div>
            ) : (
              <div className="space-y-4">
                {tunnel.sources.map((src) => (
                  <div key={src.id} className="space-y-2">
                    {src.sourceType === 'mailbox_contacts' ? (
                      <>
                        <div>
                          <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                            Shared Mailbox
                          </label>
                          <p className="mt-1 font-medium">
                            {src.sourceDisplayName || src.sourceIdentifier}
                          </p>
                        </div>
                        <div>
                          <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                            Mailbox Address
                          </label>
                          <p className="mt-1 text-sm">{src.sourceIdentifier}</p>
                        </div>
                      </>
                    ) : src.sourceType === 'org_contacts' ? (
                      <div>
                        <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                          Organization Contacts
                        </label>
                        <p className="mt-1 font-medium">
                          Tenant external contacts from Exchange Admin Center
                        </p>
                        <p className="text-xs text-text-muted mt-1">
                          Manage included/excluded contacts below.
                        </p>
                      </div>
                    ) : (
                      <>
                        <div className="flex items-center justify-between">
                          <div>
                            <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                              DDG Name
                            </label>
                            <p className="mt-1 font-medium">
                              {src.sourceDisplayName || src.sourceIdentifier}
                            </p>
                          </div>
                          <DDGRefreshButton tunnelId={tunnelId} sourceId={src.id} />
                        </div>
                        {src.sourceSmtpAddress && (
                          <div>
                            <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                              SMTP Address
                            </label>
                            <p className="mt-1 text-sm">{src.sourceSmtpAddress}</p>
                          </div>
                        )}
                        {src.sourceFilterPlain && (
                          <div>
                            <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                              Filter Description
                            </label>
                            <p className="mt-1 text-sm">{src.sourceFilterPlain}</p>
                          </div>
                        )}
                        <div>
                          <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                            Graph Filter
                          </label>
                          <p className="mt-1 text-sm font-mono text-xs text-text-muted break-all">
                            {src.sourceIdentifier}
                          </p>
                        </div>
                      </>
                    )}
                    {tunnel.sources.length > 1 && <Separator />}
                  </div>
                ))}
                {tunnel.sources.length === 0 && (
                  <p className="text-sm text-text-muted">No sources configured.</p>
                )}
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
                  Targets
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
                      No targets available.
                    </p>
                  )}
                </div>
              </div>
            ) : (
              <div>
                <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                  Targets
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

        {/* Target Users Card */}
        <Card>
          <CardHeader>
            <CardTitle>Target Users</CardTitle>
          </CardHeader>
          <CardContent>
            {isEditing ? (
              <div className="space-y-4">
                <p className="text-sm text-text-muted">
                  Choose which users receive contacts from this tunnel.
                </p>
                <div className="space-y-3">
                  <label className="flex items-start gap-3 cursor-pointer rounded-lg border p-4 has-[:checked]:border-gold has-[:checked]:bg-gold/5">
                    <input
                      type="radio"
                      name="targetScope"
                      checked={editScopeMode === 'all'}
                      onChange={() =>
                        setEditForm((prev) => ({
                          ...prev,
                          targetGroupId: null,
                          targetGroupName: null,
                          targetUserEmails: null,
                        }))
                      }
                      className="mt-0.5"
                    />
                    <div>
                      <p className="font-medium">All users</p>
                      <p className="text-sm text-text-muted">
                        Contacts sync to every active mailbox in the tenant.
                      </p>
                    </div>
                  </label>
                  <label className="flex items-start gap-3 cursor-pointer rounded-lg border p-4 has-[:checked]:border-gold has-[:checked]:bg-gold/5">
                    <input
                      type="radio"
                      name="targetScope"
                      checked={editScopeMode === 'group'}
                      onChange={() => {
                        const first = securityGroups?.[0];
                        if (first) {
                          setEditForm((prev) => ({
                            ...prev,
                            targetGroupId: first.id,
                            targetGroupName: first.displayName,
                            targetUserEmails: null,
                          }));
                        }
                      }}
                      className="mt-0.5"
                    />
                    <div className="flex-1">
                      <p className="font-medium">Members of a security group</p>
                      <p className="text-sm text-text-muted">
                        Contacts only sync to users who are members of the selected Entra security group.
                      </p>
                    </div>
                  </label>
                  <label className="flex items-start gap-3 cursor-pointer rounded-lg border p-4 has-[:checked]:border-gold has-[:checked]:bg-gold/5">
                    <input
                      type="radio"
                      name="targetScope"
                      checked={editScopeMode === 'specific'}
                      onChange={() =>
                        setEditForm((prev) => ({
                          ...prev,
                          targetGroupId: null,
                          targetGroupName: null,
                          targetUserEmails: prev.targetUserEmails || '[]',
                        }))
                      }
                      className="mt-0.5"
                    />
                    <div className="flex-1">
                      <p className="font-medium">Specific users</p>
                      <p className="text-sm text-text-muted">
                        Contacts sync only to the users you specify. Useful for assistant or executive patterns.
                      </p>
                    </div>
                  </label>
                </div>
                {editScopeMode === 'group' && (
                  <div className="ml-7">
                    <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                      Security Group
                    </label>
                    <Select
                      value={editForm.targetGroupId ?? undefined}
                      onValueChange={(val: string | null) => {
                        const group = securityGroups?.find((g) => g.id === val);
                        setEditForm((prev) => ({
                          ...prev,
                          targetGroupId: val,
                          targetGroupName: group?.displayName ?? null,
                        }));
                      }}
                    >
                      <SelectTrigger className="w-full mt-1">
                        <SelectValue placeholder={groupsLoading ? 'Loading groups...' : 'Select a security group'} />
                      </SelectTrigger>
                      <SelectContent>
                        {securityGroups?.map((group) => (
                          <SelectItem key={group.id} value={group.id}>
                            {group.displayName}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                    {securityGroups?.find((g) => g.id === editForm.targetGroupId)?.description && (
                      <p className="text-xs text-text-muted mt-1">
                        {securityGroups.find((g) => g.id === editForm.targetGroupId)?.description}
                      </p>
                    )}
                  </div>
                )}
                {editScopeMode === 'specific' && (
                  <div className="ml-7 space-y-3">
                    <div className="relative">
                      <Input
                        placeholder="Search by name or email..."
                        value={userSearchQuery}
                        onChange={(e) => setUserSearchQuery(e.target.value)}
                      />
                      {userSearching && (
                        <p className="text-xs text-text-muted mt-1">Searching...</p>
                      )}
                      {userSearchResults.length > 0 && (
                        <div className="absolute z-10 w-full mt-1 bg-white border rounded-md shadow-lg max-h-48 overflow-y-auto">
                          {userSearchResults.map((user) => (
                            <button
                              key={user.id}
                              type="button"
                              className="w-full text-left px-3 py-2 hover:bg-gray-50 text-sm"
                              onClick={() => {
                                const updated = [...editTargetUserEmails, user.email];
                                setEditForm((prev) => ({
                                  ...prev,
                                  targetUserEmails: JSON.stringify(updated),
                                }));
                                setUserSearchQuery('');
                                setUserSearchResults([]);
                              }}
                            >
                              <span className="font-medium">{user.displayName}</span>
                              <span className="text-text-muted ml-2">{user.email}</span>
                              {user.jobTitle && (
                                <span className="text-text-muted ml-2 text-xs">({user.jobTitle})</span>
                              )}
                            </button>
                          ))}
                        </div>
                      )}
                    </div>
                    {editTargetUserEmails.length > 0 && (
                      <div className="space-y-1">
                        {editTargetUserEmails.map((email) => (
                          <div
                            key={email}
                            className="flex items-center justify-between rounded-md bg-gray-50 px-3 py-2"
                          >
                            <span className="text-sm">{email}</span>
                            <button
                              type="button"
                              onClick={() => {
                                const updated = editTargetUserEmails.filter((e) => e !== email);
                                setEditForm((prev) => ({
                                  ...prev,
                                  targetUserEmails: updated.length > 0 ? JSON.stringify(updated) : null,
                                }));
                              }}
                              className="text-text-muted hover:text-destructive"
                            >
                              <X className="h-4 w-4" />
                            </button>
                          </div>
                        ))}
                      </div>
                    )}
                    {editTargetUserEmails.length === 0 && !userSearchQuery && (
                      <p className="text-xs text-text-muted">
                        Search and add at least one user above.
                      </p>
                    )}
                  </div>
                )}
              </div>
            ) : (
              <div>
                <label className="text-sm font-normal uppercase tracking-wide text-text-muted">
                  Scope
                </label>
                {tunnel.targetUserEmails ? (
                  <div className="mt-1">
                    <p className="text-sm font-medium">Specific users</p>
                    <div className="flex flex-wrap gap-1 mt-1">
                      {(() => { try { return JSON.parse(tunnel.targetUserEmails) as string[]; } catch { return []; } })().map((email: string) => (
                        <span key={email} className="inline-flex items-center rounded-full bg-gray-100 px-2 py-0.5 text-xs">
                          {email}
                        </span>
                      ))}
                    </div>
                  </div>
                ) : tunnel.targetGroupId ? (
                  <div className="mt-1">
                    <p className="text-sm font-medium">{tunnel.targetGroupName}</p>
                    <p className="text-xs text-text-muted">Security group — only members receive contacts</p>
                  </div>
                ) : (
                  <p className="mt-1 text-sm">All users</p>
                )}
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
                        stalePolicy: (val as StalePolicy) ?? prev.stalePolicy,
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

        {/* Photo Sync Toggle (D-13) */}
        <Card>
          <CardHeader>
            <CardTitle>Photo Sync</CardTitle>
          </CardHeader>
          <CardContent>
            <div className="flex items-center justify-between">
              <div>
                <Label htmlFor="photo-sync-toggle">Enable Photo Sync</Label>
                <p className="text-sm text-text-muted">
                  Sync contact photos for this tunnel. Disable to exclude this tunnel from photo sync.
                </p>
              </div>
              <Switch
                id="photo-sync-toggle"
                checked={editForm.photoSyncEnabled ?? true}
                onCheckedChange={(checked) =>
                  setEditForm((prev) => ({ ...prev, photoSyncEnabled: checked }))
                }
                disabled={!isEditing}
              />
            </div>
          </CardContent>
        </Card>
      </div>

      {/* Org Contact Filters (only for org_contacts tunnels) */}
      {tunnel.sources.some((s) => s.sourceType === 'org_contacts') && (
        <div className="mt-6">
          <Card>
            <CardHeader>
              <CardTitle>Contact Filters</CardTitle>
            </CardHeader>
            <CardContent>
              <OrgContactManager tunnelId={tunnelId} />
            </CardContent>
          </Card>
        </div>
      )}

      {/* Maintenance */}
      <div className="mt-6">
        <Card>
          <CardHeader>
            <CardTitle>Maintenance</CardTitle>
          </CardHeader>
          <CardContent>
            <div className="space-y-2">
              <Label>Force Full Re-Sync</Label>
              <p className="text-sm text-text-muted">
                Clears cached data for this tunnel so the next sync re-writes every contact.
              </p>
              <Button
                variant="destructive"
                onClick={() => setResetHashesDialogOpen(true)}
                className="mt-2"
              >
                Force Full Re-Sync
              </Button>
            </div>
          </CardContent>
        </Card>
      </div>

      {/* Reset Hashes Confirmation Dialog */}
      <ConfirmDialog
        open={resetHashesDialogOpen}
        onOpenChange={setResetHashesDialogOpen}
        title="Force full re-sync"
        description={<>This will cause the next sync run to re-write all contacts for <strong>{tunnel.name}</strong>. This may take longer than usual. Continue?</>}
        confirmLabel="Reset Hashes"
        dismissLabel="Cancel"
        variant="destructive"
        onConfirm={handleResetHashes}
        isLoading={resettingHashes}
      />

      {/* DDG Picker Dialog */}
      {addSourceType === 'ddg' ? (
        <DDGPicker
          open={ddgPickerOpen}
          onOpenChange={setDdgPickerOpen}
          onSelect={handleDdgSelect}
        />
      ) : (
        <ConfirmDialog
          open={ddgPickerOpen}
          onOpenChange={setDdgPickerOpen}
          title="Add Shared Mailbox Source"
          description={
            <div className="space-y-3 mt-2">
              <p className="text-sm text-text-muted">
                Enter the email address of the shared mailbox whose contacts will be synced.
              </p>
              <Input
                placeholder="e.g. afhstaffgal@atlantafinehomes.com"
                value={mailboxEmailInput}
                onChange={(e) => setMailboxEmailInput(e.target.value)}
              />
            </div>
          }
          confirmLabel="Add Mailbox"
          dismissLabel="Cancel"
          variant="default"
          onConfirm={() => {
            const email = mailboxEmailInput.trim();
            if (!email) return;
            setEditForm((prev) => ({
              ...prev,
              sources: [
                ...prev.sources,
                {
                  sourceType: 'mailbox_contacts',
                  sourceIdentifier: email,
                  sourceDisplayName: email,
                  sourceSmtpAddress: email,
                  sourceFilterPlain: null,
                },
              ],
            }));
            setMailboxEmailInput('');
            setDdgPickerOpen(false);
          }}
        />
      )}

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

      {/* Impact Preview Dialog */}
      <ImpactPreviewDialog
        open={impactDialogOpen}
        onOpenChange={setImpactDialogOpen}
        impact={impactData}
        onConfirm={doSave}
        isLoading={updateTunnel.isPending}
      />

      {/* Fallback Confirmation Dialog (when preview API fails) */}
      <ConfirmDialog
        open={fallbackConfirmOpen}
        onOpenChange={setFallbackConfirmOpen}
        title="Save changes"
        description="Unable to estimate impact. Save changes anyway?"
        confirmLabel="Save Anyway"
        dismissLabel="Cancel"
        variant="default"
        onConfirm={doSave}
        isLoading={updateTunnel.isPending}
      />
    </div>
  );
}
