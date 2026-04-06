'use client';

import { useState } from 'react';
import { Label } from '@/components/ui/label';
import { Checkbox } from '@/components/ui/checkbox';
import { Skeleton } from '@/components/ui/skeleton';
import { Input } from '@/components/ui/input';
import { Button } from '@/components/ui/button';
import { usePhoneLists, useCreatePhoneList } from '@/hooks/use-phone-lists';
import { toast } from 'sonner';
import { Plus } from 'lucide-react';

interface StepTargetsProps {
  selectedIds: number[];
  onToggle: (id: number) => void;
  onSelectAll: () => void;
  onDeselectAll: () => void;
  error: string | null;
}

export function StepTargets({
  selectedIds,
  onToggle,
  onSelectAll,
  onDeselectAll,
  error,
}: StepTargetsProps) {
  const { data: phoneLists, isLoading } = usePhoneLists();
  const createPhoneList = useCreatePhoneList();
  const [showCreate, setShowCreate] = useState(false);
  const [newListName, setNewListName] = useState('');

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

    </div>
  );
}
