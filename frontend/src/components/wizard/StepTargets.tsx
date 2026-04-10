'use client';

import { useState } from 'react';
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
import { Separator } from '@/components/ui/separator';
import { usePhoneLists, useCreatePhoneList } from '@/hooks/use-phone-lists';
import { api } from '@/lib/api';
import { toast } from 'sonner';
import { Plus } from 'lucide-react';

interface StepTargetsProps {
  selectedIds: number[];
  onToggle: (id: number) => void;
  onSelectAll: () => void;
  onDeselectAll: () => void;
  error: string | null;
  targetUserEmails: string | null;
  onTargetUserEmailsChange: (value: string | null) => void;
}

export function StepTargets({
  selectedIds,
  onToggle,
  onSelectAll,
  onDeselectAll,
  error,
  targetUserEmails,
  onTargetUserEmailsChange,
}: StepTargetsProps) {
  const { data: phoneLists, isLoading } = usePhoneLists();
  const createPhoneList = useCreatePhoneList();
  const [showCreate, setShowCreate] = useState(false);
  const [newListName, setNewListName] = useState('');
  const [mailboxSearch, setMailboxSearch] = useState('');
  const { data: targetMailboxes } = useQuery({
    queryKey: ['target-mailboxes'],
    queryFn: () => api.tunnels.targetMailboxes(),
    staleTime: 5 * 60 * 1000,
  });

  const allSelected =
    phoneLists && phoneLists.length > 0 && selectedIds.length === phoneLists.length;

  const handleCreateList = () => {
    if (!newListName.trim()) return;
    createPhoneList.mutate(
      { name: newListName.trim(), description: null },
      {
        onSuccess: (data) => {
          toast.success(`Target "${data.name}" created.`);
          onToggle(data.id);
          setNewListName('');
          setShowCreate(false);
        },
        onError: () => toast.error('Failed to create target.'),
      },
    );
  };

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between">
        <div>
          <Label>Target</Label>
          <p className="text-sm text-text-muted mt-1">
            Select which target receives contacts from this tunnel.
          </p>
        </div>
        <div className="flex items-center gap-3">
          <button
            type="button"
            onClick={() => setShowCreate(true)}
            className="text-sm text-navy underline cursor-pointer flex items-center gap-1"
          >
            <Plus className="h-3 w-3" />
            New
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
            placeholder="Target name"
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

      <Separator />

      {/* Target Scope */}
      <div>
        <Label>Target Scope</Label>
        <p className="text-sm text-text-muted mt-1">
          Choose whether contacts sync to all users or specific mailboxes.
        </p>
        <Select
          value={targetUserEmails !== null ? 'specific' : 'all'}
          onValueChange={(val) => {
            onTargetUserEmailsChange(val === 'all' ? null : '[]');
          }}
        >
          <SelectTrigger className="mt-2">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All Users</SelectItem>
            <SelectItem value="specific">Specific Users</SelectItem>
          </SelectContent>
        </Select>
      </div>

      {targetUserEmails !== null && (
        <div className="space-y-2">
          <p className="text-xs text-text-muted">
            {(() => {
              const selected: string[] = JSON.parse(targetUserEmails || '[]');
              return `${selected.length} user(s) selected`;
            })()}
          </p>
          <Input
            placeholder="Search mailboxes..."
            value={mailboxSearch}
            onChange={(e) => setMailboxSearch(e.target.value)}
          />
          <div className="border rounded-lg divide-y max-h-[250px] overflow-y-auto">
            {targetMailboxes
              ?.filter((m) => {
                if (!mailboxSearch.trim()) return true;
                const q = mailboxSearch.toLowerCase();
                return (
                  m.email.toLowerCase().includes(q) ||
                  m.displayName?.toLowerCase().includes(q)
                );
              })
              .map((mailbox) => {
                const selected: string[] = JSON.parse(targetUserEmails || '[]');
                const isSelected = selected.some(
                  (e) => e.toLowerCase() === mailbox.email.toLowerCase()
                );
                return (
                  <label
                    key={mailbox.id}
                    className={`flex items-center gap-3 px-3 py-2 cursor-pointer hover:bg-muted/50 transition-colors ${
                      !isSelected ? 'opacity-50' : ''
                    }`}
                  >
                    <input
                      type="checkbox"
                      checked={isSelected}
                      onChange={() => {
                        const current: string[] = JSON.parse(targetUserEmails || '[]');
                        const next = isSelected
                          ? current.filter((e) => e.toLowerCase() !== mailbox.email.toLowerCase())
                          : [...current, mailbox.email];
                        onTargetUserEmailsChange(JSON.stringify(next));
                      }}
                      className="rounded"
                    />
                    <div className="min-w-0">
                      <p className="text-sm font-medium truncate">
                        {mailbox.displayName || mailbox.email}
                      </p>
                      {mailbox.displayName && (
                        <p className="text-xs text-text-muted truncate">{mailbox.email}</p>
                      )}
                    </div>
                  </label>
                );
              })}
          </div>
        </div>
      )}

      {error && (
        <p className="text-destructive text-xs">{error}</p>
      )}
    </div>
  );
}
