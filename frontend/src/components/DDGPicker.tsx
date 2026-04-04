'use client';

import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
} from '@/components/ui/dialog';
import { DDGSearchList } from '@/components/DDGSearchList';
import type { DdgDto } from '@/types/ddg';

interface DDGPickerProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onSelect: (ddg: DdgDto) => void;
}

export function DDGPicker({ open, onOpenChange, onSelect }: DDGPickerProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-2xl p-0 gap-0">
        <DialogHeader className="px-4 pt-4 pb-0">
          <DialogTitle>Select Source DDG</DialogTitle>
          <DialogDescription>
            Choose a Dynamic Distribution Group as the contact source.
          </DialogDescription>
        </DialogHeader>

        <div className="px-4 pt-3 pb-2">
          <DDGSearchList
            onSelect={(ddg) => {
              onSelect(ddg);
              onOpenChange(false);
            }}
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}
