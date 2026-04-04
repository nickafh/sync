'use client';

import { DDGSearchList } from '@/components/DDGSearchList';
import type { DdgDto } from '@/types/ddg';

interface StepSourceProps {
  selectedDdg: DdgDto | null;
  onSelect: (ddg: DdgDto) => void;
  error: string | null;
}

export function StepSource({ selectedDdg, onSelect, error }: StepSourceProps) {
  return (
    <div className="space-y-4">
      <p className="text-sm text-text-muted">
        Choose a Dynamic Distribution Group as the contact source.
      </p>

      <DDGSearchList onSelect={onSelect} selectedId={selectedDdg?.id} />

      {selectedDdg && (
        <div className="mt-3 p-3 bg-gray-50 rounded-lg">
          <p className="font-medium">
            Selected: {selectedDdg.displayName} - {selectedDdg.memberCount} members
          </p>
          <p className="text-sm text-text-muted">
            SMTP: {selectedDdg.primarySmtpAddress}
          </p>
        </div>
      )}

      {error && (
        <p className="text-destructive text-xs">{error}</p>
      )}
    </div>
  );
}
