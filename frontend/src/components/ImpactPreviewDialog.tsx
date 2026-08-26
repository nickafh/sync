'use client';

import type { ImpactPreviewResponse } from '@/types/tunnel';
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
  DialogFooter,
} from '@/components/ui/dialog';
import { Button } from '@/components/ui/button';
import { Plus, RefreshCw, Minus, AlertCircle } from 'lucide-react';

interface ImpactPreviewDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  impact: ImpactPreviewResponse | null;
  onConfirm: () => void;
  isLoading?: boolean;
  notes?: string[];
}

export function ImpactPreviewDialog({
  open,
  onOpenChange,
  impact,
  onConfirm,
  isLoading = false,
  notes,
}: ImpactPreviewDialogProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Review Changes</DialogTitle>
          <DialogDescription>
            These changes will affect synced contacts on the next run.
          </DialogDescription>
        </DialogHeader>
        {impact && (
          <div className="bg-warm-white rounded-lg p-4 space-y-3">
            <div className="flex items-center gap-3">
              <Plus className="size-4 text-emerald-600" />
              <span className="text-lg font-bold text-emerald-600">
                +{impact.estimatedCreates}
              </span>
              <span className="text-sm">contacts will be added</span>
            </div>
            <div className="flex items-center gap-3">
              <RefreshCw className="size-4 text-amber-600" />
              <span className="text-lg font-bold text-amber-600">
                ~{impact.estimatedUpdates}
              </span>
              <span className="text-sm">contacts will be updated</span>
            </div>
            <div className="flex items-center gap-3">
              <Minus className="size-4 text-red-600" />
              <span className="text-lg font-bold text-red-600">
                -{impact.estimatedRemovals}
              </span>
              <span className="text-sm">contacts will be removed</span>
            </div>
          </div>
        )}
        {notes && notes.length > 0 && (
          <ul className="space-y-2">
            {notes.map((note) => (
              <li key={note} className="flex items-start gap-2 text-sm text-amber-800">
                <AlertCircle className="size-4 mt-0.5 shrink-0" strokeWidth={1.5} />
                <span>{note}</span>
              </li>
            ))}
          </ul>
        )}
        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button
            className="bg-gold text-white hover:bg-gold/90"
            onClick={onConfirm}
            disabled={isLoading}
          >
            {isLoading ? 'Saving...' : 'Save Changes'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
