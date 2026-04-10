'use client';

import { useState, useEffect, useRef } from 'react';
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
import { Plus, X } from 'lucide-react';
import type { UserSearchResult } from '@/types/tunnel';

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
        <TargetUserPicker
          targetUserEmails={targetUserEmails}
          onTargetUserEmailsChange={onTargetUserEmailsChange}
        />
      )}

      {error && (
        <p className="text-destructive text-xs">{error}</p>
      )}
    </div>
  );
}

function TargetUserPicker({
  targetUserEmails,
  onTargetUserEmailsChange,
}: {
  targetUserEmails: string;
  onTargetUserEmailsChange: (value: string) => void;
}) {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<UserSearchResult[]>([]);
  const [searching, setSearching] = useState(false);
  const debounceRef = useRef<ReturnType<typeof setTimeout>>(undefined);

  const selected: string[] = JSON.parse(targetUserEmails || '[]');

  useEffect(() => {
    if (query.length < 2) {
      setResults([]);
      return;
    }
    clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(async () => {
      setSearching(true);
      try {
        const data = await api.users.search(query);
        setResults(data);
      } catch {
        setResults([]);
      } finally {
        setSearching(false);
      }
    }, 300);
    return () => clearTimeout(debounceRef.current);
  }, [query]);

  const addEmail = (email: string) => {
    if (selected.some((e) => e.toLowerCase() === email.toLowerCase())) return;
    onTargetUserEmailsChange(JSON.stringify([...selected, email]));
    setQuery('');
    setResults([]);
  };

  const removeEmail = (email: string) => {
    onTargetUserEmailsChange(
      JSON.stringify(selected.filter((e) => e.toLowerCase() !== email.toLowerCase()))
    );
  };

  return (
    <div className="space-y-2">
      {selected.length > 0 && (
        <div className="flex flex-wrap gap-1">
          {selected.map((email) => (
            <span
              key={email}
              className="inline-flex items-center gap-1 rounded-full bg-gold/10 px-2.5 py-0.5 text-xs text-gold"
            >
              {email}
              <button
                type="button"
                onClick={() => removeEmail(email)}
                className="hover:text-gold/70 cursor-pointer"
              >
                <X className="h-3 w-3" />
              </button>
            </span>
          ))}
        </div>
      )}
      <p className="text-xs text-text-muted">{selected.length} user(s) selected</p>
      <Input
        placeholder="Search by name or email..."
        value={query}
        onChange={(e) => setQuery(e.target.value)}
      />
      {searching && <p className="text-xs text-text-muted">Searching...</p>}
      {results.length > 0 && (
        <div className="border rounded-lg divide-y max-h-[200px] overflow-y-auto">
          {results
            .filter((u) => !selected.some((e) => e.toLowerCase() === u.email.toLowerCase()))
            .map((user) => (
              <button
                key={user.id}
                type="button"
                className="flex items-center gap-3 px-3 py-2 w-full text-left hover:bg-muted/50 transition-colors cursor-pointer"
                onClick={() => addEmail(user.email)}
              >
                <Plus className="h-4 w-4 text-text-muted shrink-0" />
                <div className="min-w-0">
                  <p className="text-sm font-medium truncate">{user.displayName}</p>
                  <p className="text-xs text-text-muted truncate">{user.email}</p>
                </div>
              </button>
            ))}
        </div>
      )}
    </div>
  );
}
