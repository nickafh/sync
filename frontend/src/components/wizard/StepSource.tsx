'use client';

import { DDGSearchList } from '@/components/DDGSearchList';
import { Input } from '@/components/ui/input';
import type { DdgDto } from '@/types/ddg';

interface StepSourceProps {
  sourceType: 'ddg' | 'mailbox_contacts' | 'org_contacts';
  onSourceTypeChange: (type: 'ddg' | 'mailbox_contacts' | 'org_contacts') => void;
  selectedDdg: DdgDto | null;
  onSelect: (ddg: DdgDto) => void;
  mailboxEmail: string;
  onMailboxEmailChange: (email: string) => void;
  error: string | null;
}

export function StepSource({
  sourceType,
  onSourceTypeChange,
  selectedDdg,
  onSelect,
  mailboxEmail,
  onMailboxEmailChange,
  error,
}: StepSourceProps) {
  return (
    <div className="space-y-4">
      <p className="text-sm text-text-muted">
        Choose a contact source for this tunnel.
      </p>

      {/* Source type selector */}
      <div className="space-y-3">
        <label className="flex items-start gap-3 cursor-pointer rounded-lg border p-4 has-[:checked]:border-gold has-[:checked]:bg-gold/5">
          <input
            type="radio"
            name="sourceType"
            checked={sourceType === 'ddg'}
            onChange={() => onSourceTypeChange('ddg')}
            className="mt-0.5"
          />
          <div>
            <p className="font-medium">Dynamic Distribution Group</p>
            <p className="text-sm text-text-muted">
              Sync members of an Exchange DDG. Best for syncing internal employee contacts.
            </p>
          </div>
        </label>
        <label className="flex items-start gap-3 cursor-pointer rounded-lg border p-4 has-[:checked]:border-gold has-[:checked]:bg-gold/5">
          <input
            type="radio"
            name="sourceType"
            checked={sourceType === 'mailbox_contacts'}
            onChange={() => onSourceTypeChange('mailbox_contacts')}
            className="mt-0.5"
          />
          <div>
            <p className="font-medium">Shared Mailbox</p>
            <p className="text-sm text-text-muted">
              Sync contacts from a shared mailbox&apos;s Contacts folder. Best for external contacts like vendors and service providers.
            </p>
          </div>
        </label>
        <label className="flex items-start gap-3 cursor-pointer rounded-lg border p-4 has-[:checked]:border-gold has-[:checked]:bg-gold/5">
          <input
            type="radio"
            name="sourceType"
            checked={sourceType === 'org_contacts'}
            onChange={() => onSourceTypeChange('org_contacts')}
            className="mt-0.5"
          />
          <div>
            <p className="font-medium">Organization Contacts</p>
            <p className="text-sm text-text-muted">
              Sync tenant-level external contacts from Exchange Admin Center. These are mail contacts like vendors and attorneys managed at the organization level.
            </p>
          </div>
        </label>
      </div>

      {/* DDG source */}
      {sourceType === 'ddg' && (
        <>
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
        </>
      )}

      {/* Shared Mailbox source */}
      {sourceType === 'mailbox_contacts' && (
        <div className="space-y-2">
          <label className="text-sm font-medium">Mailbox Email Address</label>
          <Input
            placeholder="e.g. afhstaffgal@atlantafinehomes.com"
            value={mailboxEmail}
            onChange={(e) => onMailboxEmailChange(e.target.value)}
          />
          <p className="text-xs text-text-muted">
            The contacts in this mailbox&apos;s Contacts folder will be synced to target users.
          </p>
        </div>
      )}

      {/* Organization Contacts source */}
      {sourceType === 'org_contacts' && (
        <div className="mt-3 p-3 bg-gray-50 rounded-lg space-y-2">
          <p className="text-sm font-medium">All tenant organization contacts will be synced.</p>
          <p className="text-xs text-text-muted">
            After creating the tunnel, you can manage which contacts to include or exclude from the tunnel detail page.
          </p>
        </div>
      )}

      {error && (
        <p className="text-destructive text-xs">{error}</p>
      )}
    </div>
  );
}
