'use client';

import { Label } from '@/components/ui/label';
import { Input } from '@/components/ui/input';

interface StepNameProps {
  name: string;
  onChange: (name: string) => void;
  error: string | null;
}

export function StepName({ name, onChange, error }: StepNameProps) {
  return (
    <div className="space-y-2">
      <Label htmlFor="tunnel-name">Tunnel Name</Label>
      <Input
        id="tunnel-name"
        value={name}
        onChange={(e) => onChange(e.target.value)}
        placeholder="e.g., Atlanta Office Contacts"
      />
      {error && (
        <p className="text-destructive text-xs">{error}</p>
      )}
    </div>
  );
}
