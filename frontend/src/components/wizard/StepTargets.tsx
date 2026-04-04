'use client';

import { Label } from '@/components/ui/label';
import { Checkbox } from '@/components/ui/checkbox';
import { Skeleton } from '@/components/ui/skeleton';
import { usePhoneLists } from '@/hooks/use-phone-lists';

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
  const allSelected =
    phoneLists && phoneLists.length > 0 && selectedIds.length === phoneLists.length;

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between">
        <div>
          <Label>Target Phone Lists</Label>
          <p className="text-sm text-text-muted mt-1">
            Select which phone lists this tunnel will deliver contacts to.
          </p>
        </div>
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
        <p className="text-sm text-text-muted py-4">No phone lists available</p>
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
  );
}
