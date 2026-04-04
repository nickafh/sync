'use client';

import { Users } from 'lucide-react';
import { Skeleton } from '@/components/ui/skeleton';
import type { ContactDto } from '@/types/phone-list';

interface ContactListProps {
  contacts: ContactDto[];
  onSelectContact: (contact: ContactDto) => void;
  isLoading?: boolean;
}

function groupContactsByInitial(contacts: ContactDto[]): Map<string, ContactDto[]> {
  const sorted = [...contacts].sort((a, b) => {
    const nameA = (a.displayName ?? '').toLowerCase();
    const nameB = (b.displayName ?? '').toLowerCase();
    return nameA.localeCompare(nameB);
  });

  const groups = new Map<string, ContactDto[]>();
  for (const contact of sorted) {
    const name = contact.displayName?.trim();
    const initial = name && name.length > 0 ? name[0].toUpperCase() : '#';
    const group = groups.get(initial);
    if (group) {
      group.push(contact);
    } else {
      groups.set(initial, [contact]);
    }
  }

  return groups;
}

function getInitial(name: string | null): string {
  if (!name || name.trim().length === 0) return '?';
  return name.trim()[0].toUpperCase();
}

export function ContactList({ contacts, onSelectContact, isLoading }: ContactListProps) {
  if (isLoading) {
    return (
      <div className="px-4 space-y-3 py-3">
        {Array.from({ length: 8 }).map((_, i) => (
          <div key={i} className="flex items-center gap-3">
            <Skeleton className="w-8 h-8 rounded-full" />
            <div className="flex-1 space-y-1.5">
              <Skeleton className="h-3.5 w-3/4" />
              <Skeleton className="h-3 w-1/2" />
            </div>
          </div>
        ))}
      </div>
    );
  }

  if (contacts.length === 0) {
    return (
      <div className="flex flex-col items-center justify-center py-12">
        <Users className="size-8 text-text-muted" strokeWidth={1.5} />
        <p className="text-xs font-bold text-navy mt-2">No contacts</p>
        <p className="text-xs text-text-muted mt-1">Contacts will appear after the next sync run.</p>
      </div>
    );
  }

  const groups = groupContactsByInitial(contacts);

  return (
    <div>
      {Array.from(groups.entries()).map(([letter, groupContacts]) => (
        <div key={letter}>
          {/* Sticky letter header */}
          <div className="sticky top-0 z-10 bg-stone-100 px-4 py-1 text-xs font-normal uppercase tracking-wide text-text-muted">
            {letter}
          </div>

          {/* Contact rows */}
          {groupContacts.map((contact) => (
            <div
              key={contact.sourceUserId}
              className="flex items-center gap-3 px-4 py-2.5 border-b border-border-default/50 hover:bg-stone-50 cursor-pointer"
              onClick={() => onSelectContact(contact)}
            >
              {/* Avatar */}
              <div className="w-8 h-8 rounded-full bg-navy text-white text-xs font-bold flex items-center justify-center shrink-0">
                {getInitial(contact.displayName)}
              </div>

              {/* Text column */}
              <div className="min-w-0 flex-1">
                <div className="text-sm font-bold text-navy truncate">
                  {contact.displayName || '--'}
                </div>
                {contact.jobTitle && (
                  <div className="text-xs text-text-muted truncate">
                    {contact.jobTitle}
                  </div>
                )}
              </div>
            </div>
          ))}
        </div>
      ))}
    </div>
  );
}
