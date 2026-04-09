'use client';

import { Card, CardContent } from '@/components/ui/card';
import { Separator } from '@/components/ui/separator';
import { usePhoneLists } from '@/hooks/use-phone-lists';
import type { DdgDto } from '@/types/ddg';

interface StepReviewProps {
  name: string;
  sourceType: 'ddg' | 'mailbox_contacts' | 'org_contacts';
  ddg: DdgDto | null;
  mailboxEmail: string;
  targetListIds: number[];
  onEdit: (step: number) => void;
}

export function StepReview({
  name,
  sourceType,
  ddg,
  mailboxEmail,
  targetListIds,
  onEdit,
}: StepReviewProps) {
  const { data: phoneLists } = usePhoneLists();

  const selectedListNames = (phoneLists ?? [])
    .filter((l) => targetListIds.includes(l.id))
    .map((l) => l.name);

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

          {/* Source */}
          <div className="flex items-start justify-between px-4 py-3">
            <div>
              <p className="text-xs text-text-muted">Source</p>
              {sourceType === 'ddg' && ddg && (
                <>
                  <p className="text-sm font-medium mt-0.5">
                    {ddg.displayName} ({ddg.memberCount} members)
                  </p>
                  <p className="text-xs text-text-muted">{ddg.primarySmtpAddress}</p>
                </>
              )}
              {sourceType === 'mailbox_contacts' && mailboxEmail && (
                <>
                  <p className="text-sm font-medium mt-0.5">
                    Shared Mailbox
                  </p>
                  <p className="text-xs text-text-muted">{mailboxEmail}</p>
                </>
              )}
              {sourceType === 'org_contacts' && (
                <>
                  <p className="text-sm font-medium mt-0.5">
                    Organization Contacts
                  </p>
                  <p className="text-xs text-text-muted">All tenant external contacts from Exchange Admin Center</p>
                </>
              )}
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
                    className="inline-flex items-center rounded-full bg-gray-100 px-3 py-1 text-sm"
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
