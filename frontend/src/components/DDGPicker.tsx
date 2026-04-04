'use client';

import { useState } from 'react';
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
} from '@/components/ui/dialog';
import {
  Command,
  CommandInput,
  CommandList,
  CommandEmpty,
  CommandGroup,
  CommandItem,
} from '@/components/ui/command';
import { Skeleton } from '@/components/ui/skeleton';
import { cn } from '@/lib/utils';
import { useDdgs } from '@/hooks/use-ddgs';
import type { DdgDto } from '@/types/ddg';

interface DDGPickerProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onSelect: (ddg: DdgDto) => void;
}

const typeFilters = ['all', 'Office', 'Role', 'Brand'] as const;

export function DDGPicker({ open, onOpenChange, onSelect }: DDGPickerProps) {
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
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-2xl p-0 gap-0">
        <DialogHeader className="px-4 pt-4 pb-0">
          <DialogTitle>Select Source DDG</DialogTitle>
          <DialogDescription>
            Choose a Dynamic Distribution Group as the contact source.
          </DialogDescription>
        </DialogHeader>

        <div className="flex items-center gap-2 px-4 pt-3 pb-2">
          {typeFilters.map((type) => (
            <button
              key={type}
              type="button"
              onClick={() => setTypeFilter(type)}
              className={cn(
                'px-3 py-1 text-xs font-medium rounded-full transition-colors',
                typeFilter === type
                  ? 'bg-navy text-white'
                  : 'bg-gray-100 text-gray-600 hover:bg-gray-200',
              )}
            >
              {type === 'all' ? 'All' : type}
            </button>
          ))}
        </div>

        <Command shouldFilter={false} className="rounded-none border-t">
          <CommandInput
            placeholder="Search DDGs..."
            value={search}
            onValueChange={setSearch}
          />
          <CommandList className="max-h-[400px]">
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
                    onSelect={() => {
                      onSelect(ddg);
                      onOpenChange(false);
                    }}
                    className="flex items-center justify-between gap-3 py-2.5 px-3 cursor-pointer"
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
                      <span className="text-xs bg-gray-100 rounded-full px-2 py-0.5">
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
          </CommandList>
        </Command>
      </DialogContent>
    </Dialog>
  );
}
