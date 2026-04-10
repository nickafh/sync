'use client';

import { Card, CardContent } from '@/components/ui/card';
import { Separator } from '@/components/ui/separator';
import { usePhoneLists } from '@/hooks/use-phone-lists';
import type { SourceEntry } from '@/components/wizard/StepSource';
import { Cable, Mail, Building2 } from 'lucide-react';

interface StepReviewProps {
  name: string;
  sources: SourceEntry[];
  targetListIds: number[];
  onEdit: (step: number) => void;
}

export function StepReview({
  name,
  sources,
  targetListIds,
  onEdit,
}: StepReviewProps) {
  const { data: phoneLists } = usePhoneLists();

  const selectedListNames = (phoneLists ?? [])
    .filter((l) => targetListIds.includes(l.id))
    .map((l) => l.name);

  const sourceIcon = (type: string) => {
    switch (type) {
      case 'ddg': return <Cable className="h-3.5 w-3.5 text-text-muted" />;
      case 'mailbox_contacts': return <Mail className="h-3.5 w-3.5 text-text-muted" />;
      case 'org_contacts': return <Building2 className="h-3.5 w-3.5 text-text-muted" />;
      default: return null;
    }
  };

  return (
    <div className="space-y-4">
      <h2 className="text-lg font-bold font-heading text-navy">Review & Create</h2>

      <Card>
        <CardContent className="space-y-0 p-0">
          {/* Name */}
          <div className="flex items-start justify-between px-4 py-3">
            <div>
              <p className="text-xs text-text-muted">Name</p>
              <p className="text-sm font-medium mt-0.5">{name}</p>
            </div>
            <button
              type="button"
              onClick={() => onEdit(0)}
              className="text-sm text-gold hover:underline cursor-pointer"
            >
              Edit
            </button>
          </div>

          <Separator />

          {/* Sources */}
          <div className="flex items-start justify-between px-4 py-3">
            <div className="flex-1">
              <p className="text-xs text-text-muted">
                {sources.length === 1 ? 'Source' : `Sources (${sources.length})`}
              </p>
              <div className="mt-1 space-y-1.5">
                {sources.map((source, i) => (
                  <div key={i} className="flex items-center gap-2">
                    {sourceIcon(source.type)}
                    <div>
                      <p className="text-sm font-medium">{source.label}</p>
                      {source.sublabel && (
                        <p className="text-xs text-text-muted">{source.sublabel}</p>
                      )}
                    </div>
                  </div>
                ))}
              </div>
            </div>
            <button
              type="button"
              onClick={() => onEdit(1)}
              className="text-sm text-gold hover:underline cursor-pointer"
            >
              Edit
            </button>
          </div>

          <Separator />

          {/* Target */}
          <div className="flex items-start justify-between px-4 py-3">
            <div>
              <p className="text-xs text-text-muted">Target</p>
              <div className="flex flex-wrap gap-2 mt-1.5">
                {selectedListNames.map((listName) => (
                  <span
                    key={listName}
                    className="inline-flex items-center rounded-full bg-muted px-3 py-1 text-sm"
                  >
                    {listName}
                  </span>
                ))}
              </div>
            </div>
            <button
              type="button"
              onClick={() => onEdit(2)}
              className="text-sm text-gold hover:underline cursor-pointer"
            >
              Edit
            </button>
          </div>

          <Separator />

          {/* Configuration */}
          <div className="px-4 py-3">
            <p className="text-xs text-text-muted">Configuration</p>
            <p className="text-sm font-medium mt-0.5">
              Default field profile, Auto Remove stale policy
            </p>
            <p className="text-xs text-text-muted mt-1">
              Configuration uses defaults. Edit after creation.
            </p>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}
