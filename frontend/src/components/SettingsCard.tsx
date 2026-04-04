'use client';

import type { ReactNode } from 'react';
import {
  Card,
  CardHeader,
  CardTitle,
  CardDescription,
  CardContent,
  CardFooter,
} from '@/components/ui/card';
import { Button } from '@/components/ui/button';

interface SettingsCardProps {
  title: string;
  description: string;
  onSave: () => void;
  isSaving?: boolean;
  children: ReactNode;
}

export function SettingsCard({
  title,
  description,
  onSave,
  isSaving = false,
  children,
}: SettingsCardProps) {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-lg font-bold font-heading text-navy">
          {title}
        </CardTitle>
        <CardDescription className="text-sm text-text-muted">
          {description}
        </CardDescription>
      </CardHeader>
      <CardContent>{children}</CardContent>
      <CardFooter className="justify-end">
        <Button
          className="bg-gold text-white hover:bg-gold/90"
          onClick={onSave}
          disabled={isSaving}
        >
          {isSaving ? 'Saving...' : 'Save Settings'}
        </Button>
      </CardFooter>
    </Card>
  );
}
