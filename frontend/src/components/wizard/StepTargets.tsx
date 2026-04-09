'use client';

import { useState, useEffect, useRef } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Label } from '@/components/ui/label';
import { Checkbox } from '@/components/ui/checkbox';
import { Skeleton } from '@/components/ui/skeleton';
import { Input } from '@/components/ui/input';
import { Button } from '@/components/ui/button';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { usePhoneLists, useCreatePhoneList } from '@/hooks/use-phone-lists';
import { api } from '@/lib/api';
import { toast } from 'sonner';
import { Plus, X } from 'lucide-react';
import type { UserSearchResult } from '@/types/tunnel';

type TargetScopeMode = 'all' | 'group' | 'specific';

interface StepTargetsProps {
  selectedIds: number[];
  onToggle: (id: number) => void;
  onSelectAll: () => void;
  onDeselectAll: () => void;
  error: string | null;
  targetGroupId?: string | null;
  targetGroupName?: string | null;
  onTargetScopeChange?: (groupId: string | null, groupName: string | null) => void;
  targetUserEmails?: string[];
  onTargetUserEmailsChange?: (emails: string[]) => void;
}

export function StepTargets({
  selectedIds,
  onToggle,
  onSelectAll,
  onDeselectAll,
  error,
  targetGroupId,
  targetGroupName,
  onTargetScopeChange,
  targetUserEmails = [],
  onTargetUserEmailsChange,
}: StepTargetsProps) {
  const { data: phoneLists, isLoading } = usePhoneLists();
  const { data: securityGroups, isLoading: groupsLoading } = useQuery({
    queryKey: ['security-groups'],
    queryFn: () => api.securityGroups.list(),
    staleTime: 10 * 60 * 1000,
    enabled: !!onTargetScopeChange,
  });
  const createPhoneList = useCreatePhoneList();
  const [showCreate, setShowCreate] = useState(false);
  const [newListName, setNewListName] = useState('');

  // User search state for "Specific users" mode
  const [userSearchQuery, setUserSearchQuery] = useState('');
  const [userSearchResults, setUserSearchResults] = useState<UserSearchResult[]>([]);
  const [userSearching, setUserSearching] = useState(false);
  const searchTimeout = useRef<ReturnType<typeof setTimeout> | null>(null);

  const scopeMode: TargetScopeMode =
    (targetUserEmails?.length ?? 0) > 0 ? 'specific' :
    targetGroupId ? 'group' : 'all';

  // Debounced user search
  useEffect(() => {
    if (userSearchQuery.length < 2) {
      setUserSearchResults([]);
      return;
    }
    if (searchTimeout.current) clearTimeout(searchTimeout.current);
    searchTimeout.current = setTimeout(async () => {
      setUserSearching(true);
      try {
        const results = await api.users.search(userSearchQuery);
        // Filter out already-added emails
        setUserSearchResults(
          results.filter((u) => !targetUserEmails?.includes(u.email))
        );
      } catch {
        setUserSearchResults([]);
      } finally {
        setUserSearching(false);
      }
    }, 300);
    return () => { if (searchTimeout.current) clearTimeout(searchTimeout.current); };
  }, [userSearchQuery, targetUserEmails]);

  const allSelected =
    phoneLists && phoneLists.length > 0 && selectedIds.length === phoneLists.length;

  const handleCreateList = () => {
    if (!newListName.trim()) return;
    createPhoneList.mutate(
      { name: newListName.trim(), description: null },
      {
        onSuccess: (data) => {
          toast.success(`Phone list "${data.name}" created.`);
          onToggle(data.id);
          setNewListName('');
          setShowCreate(false);
        },
        onError: () => toast.error('Failed to create target.'),
      },
    );
  };

  return (
    <div className="space-y-6">
      {/* Targets - Primary */}
      <div className="space-y-3">
        <div className="flex items-center justify-between">
          <div>
            <Label>Targets</Label>
            <p className="text-sm text-text-muted mt-1">
              Choose which contact folders will appear on users&apos; phones.
            </p>
          </div>
          <div className="flex items-center gap-3">
            <button
              type="button"
              onClick={() => setShowCreate(true)}
              className="text-sm text-navy underline cursor-pointer flex items-center gap-1"
            >
              <Plus className="h-3 w-3" />
              New List
            </button>
            {phoneLists && phoneLists.length > 0 && (
              <button
                type="button"
                onClick={allSelected ? onDeselectAll : onSelectAll}
                className="text-sm text-navy underline cursor-pointer"
              >
                {allSelected ? 'Deselect All' : 'Select All'}
              </button>
            )}
          </div>
        </div>

        {showCreate && (
          <div className="flex items-center gap-2 p-3 border rounded-md bg-muted/30">
            <Input
              placeholder="Phone list name"
              value={newListName}
              onChange={(e) => setNewListName(e.target.value)}
              onKeyDown={(e) => e.key === 'Enter' && handleCreateList()}
              className="flex-1"
              autoFocus
            />
            <Button
              size="sm"
              className="bg-gold text-white hover:bg-gold/90"
              onClick={handleCreateList}
              disabled={createPhoneList.isPending || !newListName.trim()}
            >
              {createPhoneList.isPending ? 'Creating...' : 'Create'}
            </Button>
            <Button
              size="sm"
              variant="outline"
              onClick={() => { setShowCreate(false); setNewListName(''); }}
            >
              Cancel
            </Button>
          </div>
        )}

        {isLoading ? (
          <div className="space-y-3">
            {Array.from({ length: 4 }).map((_, i) => (
              <div key={i} className="flex items-center gap-3">
                <Skeleton className="h-4 w-4" />
                <Skeleton className="h-4 flex-1" />
              </div>
            ))}
          </div>
        ) : !phoneLists || phoneLists.length === 0 ? (
          <p className="text-sm text-text-muted py-4">No targets available. Create one above.</p>
        ) : (
          <div className="space-y-3">
            {phoneLists.map((list) => (
              <label
                key={list.id}
                className="flex items-center gap-3 cursor-pointer"
              >
                <Checkbox
                  checked={selectedIds.includes(list.id)}
                  onCheckedChange={() => onToggle(list.id)}
                />
                <span className="text-sm font-medium">{list.name}</span>
                <span className="text-xs text-text-muted">
                  {list.userCount} users
                </span>
              </label>
            ))}
          </div>
        )}

        {error && (
          <p className="text-destructive text-xs">{error}</p>
        )}
      </div>

      {/* Target Users Scope */}
      {onTargetScopeChange && (
        <div className="space-y-3">
          <div>
            <Label>Target Users</Label>
            <p className="text-sm text-text-muted mt-1">
              Choose which users receive contacts from this tunnel.
            </p>
          </div>
          <div className="space-y-3">
            <label className="flex items-start gap-3 cursor-pointer rounded-lg border p-4 has-[:checked]:border-gold has-[:checked]:bg-gold/5">
              <input
                type="radio"
                name="wizardTargetScope"
                checked={scopeMode === 'all'}
                onChange={() => {
                  onTargetScopeChange(null, null);
                  onTargetUserEmailsChange?.([]);
                }}
                className="mt-0.5"
              />
              <div>
                <p className="font-medium text-sm">All users</p>
                <p className="text-xs text-text-muted">
                  Contacts sync to every active mailbox.
                </p>
              </div>
            </label>
            <label className="flex items-start gap-3 cursor-pointer rounded-lg border p-4 has-[:checked]:border-gold has-[:checked]:bg-gold/5">
              <input
                type="radio"
                name="wizardTargetScope"
                checked={scopeMode === 'group'}
                onChange={() => {
                  onTargetUserEmailsChange?.([]);
                  const first = securityGroups?.[0];
                  if (first) {
                    onTargetScopeChange(first.id, first.displayName);
                  }
                }}
                className="mt-0.5"
              />
              <div>
                <p className="font-medium text-sm">Members of a security group</p>
                <p className="text-xs text-text-muted">
                  Only members of the selected group receive contacts.
                </p>
              </div>
            </label>
            <label className="flex items-start gap-3 cursor-pointer rounded-lg border p-4 has-[:checked]:border-gold has-[:checked]:bg-gold/5">
              <input
                type="radio"
                name="wizardTargetScope"
                checked={scopeMode === 'specific'}
                onChange={() => {
                  onTargetScopeChange(null, null);
                  onTargetUserEmailsChange?.(targetUserEmails?.length ? targetUserEmails : []);
                }}
                className="mt-0.5"
              />
              <div>
                <p className="font-medium text-sm">Specific users</p>
                <p className="text-xs text-text-muted">
                  Contacts sync only to the users you specify. Useful for assistant or executive patterns.
                </p>
              </div>
            </label>
          </div>
          {scopeMode === 'group' && (
            <div className="ml-7">
              <Select
                value={targetGroupId ?? undefined}
                onValueChange={(val: string | null) => {
                  const group = securityGroups?.find((g) => g.id === val);
                  onTargetScopeChange(val, group?.displayName ?? null);
                }}
              >
                <SelectTrigger className="w-full">
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
            </div>
          )}
          {scopeMode === 'specific' && (
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
                          onTargetUserEmailsChange?.([...(targetUserEmails ?? []), user.email]);
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
              {(targetUserEmails?.length ?? 0) > 0 && (
                <div className="space-y-1">
                  {targetUserEmails?.map((email) => (
                    <div
                      key={email}
                      className="flex items-center justify-between rounded-md bg-gray-50 px-3 py-2"
                    >
                      <span className="text-sm">{email}</span>
                      <button
                        type="button"
                        onClick={() =>
                          onTargetUserEmailsChange?.(
                            targetUserEmails.filter((e) => e !== email)
                          )
                        }
                        className="text-text-muted hover:text-destructive"
                      >
                        <X className="h-4 w-4" />
                      </button>
                    </div>
                  ))}
                </div>
              )}
              {(targetUserEmails?.length ?? 0) === 0 && !userSearchQuery && (
                <p className="text-xs text-text-muted">
                  Search and add at least one user above.
                </p>
              )}
            </div>
          )}
        </div>
      )}

    </div>
  );
}
