'use client';

import { useState } from 'react';
import {
  Command,
  CommandInput,
  CommandList,
  CommandEmpty,
  CommandGroup,
  CommandItem,
} from '@/components/ui/command';
import { ScrollArea } from '@/components/ui/scroll-area';
import { Skeleton } from '@/components/ui/skeleton';
import { cn } from '@/lib/utils';
import { useDdgs } from '@/hooks/use-ddgs';
import type { DdgDto } from '@/types/ddg';

interface DDGSearchListProps {
  onSelect: (ddg: DdgDto) => void;
  selectedId?: string;
  selectedIds?: string[];
}

const typeFilters = ['all', 'Office', 'Role', 'Brand'] as const;

export function DDGSearchList({ onSelect, selectedId, selectedIds }: DDGSearchListProps) {
  const { data: ddgs, isLoading } = useDdgs();
  const [typeFilter, setTypeFilter] = useState<string>('all');
  const [search, setSearch] = useState('');

  const filtered = (ddgs ?? []).filter((ddg) => {
    if (typeFilter !== 'all' && ddg.type !== typeFilter) return false;
    if (search) {
      const term = search.toLowerCase();
      return (
        ddg.displayName.toLowerCase().includes(term) ||
        ddg.primarySmtpAddress.toLowerCase().includes(term)
      );
    }
    return true;
  });

  return (
    <div>
      <div className="flex items-center gap-2 pb-2">
        {typeFilters.map((type) => (
          <button
            key={type}
            type="button"
            onClick={() => setTypeFilter(type)}
            className={cn(
              'px-3 py-1 text-xs font-medium rounded-full transition-colors',
              typeFilter === type
                ? 'bg-navy text-white'
                : 'bg-muted text-text-muted hover:bg-gray-200',
            )}
          >
            {type === 'all' ? 'All' : type}
          </button>
        ))}
      </div>

      <Command shouldFilter={false}>
        <CommandInput
          placeholder="Search DDGs..."
          value={search}
          onValueChange={setSearch}
        />
        <CommandList className="max-h-[360px]">
          <ScrollArea className="max-h-[360px]">
            {isLoading ? (
              <div className="p-4 space-y-3">
                {Array.from({ length: 5 }).map((_, i) => (
                  <div key={i} className="flex items-center gap-3">
                    <Skeleton className="h-5 flex-1" />
                    <Skeleton className="h-5 w-16" />
                  </div>
                ))}
              </div>
            ) : filtered.length === 0 ? (
              <CommandEmpty>No DDGs found.</CommandEmpty>
            ) : (
              <CommandGroup>
                {filtered.map((ddg) => (
                  <CommandItem
                    key={ddg.id}
                    value={ddg.id}
                    onSelect={() => onSelect(ddg)}
                    className={cn(
                      'flex items-center justify-between gap-3 py-2.5 px-3 cursor-pointer',
                      (selectedId === ddg.id || selectedIds?.includes(ddg.id)) && 'bg-gold/10 border-l-2 border-gold',
                    )}
                  >
                    <div className="flex flex-col min-w-0">
                      <span className="font-medium text-sm truncate">
                        {ddg.displayName}
                      </span>
                      <span className="text-xs text-text-muted truncate">
                        {ddg.primarySmtpAddress}
                      </span>
                    </div>
                    <div className="flex items-center gap-2 shrink-0">
                      <span className="text-xs bg-muted rounded-full px-2 py-0.5">
                        {ddg.memberCount} members
                      </span>
                      <span className="text-xs text-text-muted">
                        {ddg.type}
                      </span>
                    </div>
                  </CommandItem>
                ))}
              </CommandGroup>
            )}
          </ScrollArea>
        </CommandList>
      </Command>
    </div>
  );
}
