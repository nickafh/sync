'use client';

import { ChevronLeft } from 'lucide-react';
import { cn } from '@/lib/utils';

export interface ContactCardData {
  displayName: string | null;
  email: string | null;
  jobTitle: string | null;
  department: string | null;
  office: string | null;
  phone: string | null;
  mobilePhone: string | null;
  companyName: string | null;
  streetAddress: string | null;
  city: string | null;
  state: string | null;
  postalCode: string | null;
  country: string | null;
}

interface ContactCardProps {
  contact: ContactCardData;
  hiddenFields?: string[];
  removeBlankFields?: string[];
  onBack?: () => void;
}

const fieldSections = [
  { name: 'Phone', fields: [
    { key: 'phone', label: 'Business Phone' },
    { key: 'mobilePhone', label: 'Mobile Phone' },
  ]},
  { name: 'Email', fields: [
    { key: 'email', label: 'Email' },
  ]},
  { name: 'Organization', fields: [
    { key: 'companyName', label: 'Company' },
    { key: 'department', label: 'Department' },
    { key: 'jobTitle', label: 'Job Title' },
    { key: 'office', label: 'Office' },
  ]},
  { name: 'Address', fields: [
    { key: 'streetAddress', label: 'Street' },
    { key: 'city', label: 'City' },
    { key: 'state', label: 'State' },
    { key: 'postalCode', label: 'Postal Code' },
    { key: 'country', label: 'Country' },
  ]},
];

function getInitial(name: string | null): string {
  if (!name || name.trim().length === 0) return '?';
  return name.trim()[0].toUpperCase();
}

export function ContactCard({ contact, hiddenFields = [], removeBlankFields = [], onBack }: ContactCardProps) {
  return (
    <div className="bg-white">
      {onBack && (
        <button
          onClick={onBack}
          className="flex items-center gap-1 px-4 py-2.5 text-sm text-gold hover:opacity-80"
        >
          <ChevronLeft className="h-3.5 w-3.5" />
          Contacts
        </button>
      )}

      {/* Hero section */}
      <div className="pt-4 pb-6 flex flex-col items-center">
        <div className="w-16 h-16 rounded-full bg-navy text-white text-lg font-bold flex items-center justify-center">
          {getInitial(contact.displayName)}
        </div>
        <h2 className="text-lg font-bold font-heading text-navy mt-3">
          {contact.displayName || '--'}
        </h2>
        <p className="text-xs text-text-muted mt-1">
          {contact.jobTitle || '--'}
        </p>
      </div>

      {/* Field sections */}
      <div className="px-4 space-y-4">
        {fieldSections.map((section) => (
          <div key={section.name}>
            <h3 className="text-xs font-normal uppercase tracking-wide text-text-muted pb-1">
              {section.name}
            </h3>
            {section.fields.map((field) => {
              const value = contact[field.key as keyof ContactCardData];
              const isHidden = hiddenFields.includes(field.key);
              const isRemoveBlank = removeBlankFields.includes(field.key);

              return (
                <div key={field.key} className="py-2 border-b border-border-default/50">
                  <div className="text-xs text-text-muted">{field.label}</div>
                  <div className="flex items-baseline gap-2">
                    <span
                      className={cn(
                        'text-sm text-navy',
                        isHidden && 'line-through text-text-muted'
                      )}
                    >
                      {value || '--'}
                    </span>
                    {isHidden && (
                      <span className="ml-2 text-xs text-text-muted italic">Not synced</span>
                    )}
                    {isRemoveBlank && (
                      <span className="ml-2 text-xs text-text-muted italic">(cleared if empty)</span>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        ))}
      </div>
    </div>
  );
}
