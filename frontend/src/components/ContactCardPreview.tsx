'use client';

import { useMemo } from 'react';
import { ContactCard } from '@/components/ContactCard';
import type { ContactCardData } from '@/components/ContactCard';
import type { FieldProfileDetailDto } from '@/types/field-profile';

const SAMPLE_CONTACT: ContactCardData = {
  displayName: 'Sarah Mitchell',
  email: 'sarah.mitchell@atlantafinehomes.com',
  jobTitle: 'Senior Sales Associate',
  department: 'Residential Sales',
  office: 'Buckhead Office',
  phone: '(404) 555-0142',
  mobilePhone: '(404) 555-0198',
  companyName: 'Atlanta Fine Homes SIR',
  streetAddress: '3500 Lenox Road NE',
  city: 'Atlanta',
  state: 'GA',
  postalCode: '30326',
  country: null,
};

const FIELD_NAME_TO_CARD_KEY: Record<string, string> = {
  'DisplayName': 'displayName',
  'Email': 'email',
  'BusinessPhone': 'phone',
  'MobilePhone': 'mobilePhone',
  'JobTitle': 'jobTitle',
  'Department': 'department',
  'OfficeLocation': 'office',
  'CompanyName': 'companyName',
  'StreetAddress': 'streetAddress',
  'City': 'city',
  'State': 'state',
  'PostalCode': 'postalCode',
  'Country': 'country',
};

interface ContactCardPreviewProps {
  profile: FieldProfileDetailDto | null;
}

export function ContactCardPreview({ profile }: ContactCardPreviewProps) {
  const hiddenFields = useMemo(() => {
    if (!profile) return [];
    const hidden: string[] = [];
    for (const section of profile.sections) {
      for (const field of section.fields) {
        if (field.behavior === 'nosync') {
          const cardKey = FIELD_NAME_TO_CARD_KEY[field.fieldName];
          if (cardKey) hidden.push(cardKey);
        }
      }
    }
    return hidden;
  }, [profile]);

  const removeBlankFields = useMemo(() => {
    if (!profile) return [];
    const fields: string[] = [];
    for (const section of profile.sections) {
      for (const field of section.fields) {
        if (field.behavior === 'remove_blank') {
          const cardKey = FIELD_NAME_TO_CARD_KEY[field.fieldName];
          if (cardKey) fields.push(cardKey);
        }
      }
    }
    return fields;
  }, [profile]);

  return (
    <div className="sticky top-8">
      <div className="mb-2">
        <span className="text-sm font-medium">Preview</span>
        <p className="text-xs text-text-muted">How a synced contact will appear on phones.</p>
      </div>
      <div className="bg-white border border-border-default rounded-xl p-4 shadow-sm">
        <ContactCard
          contact={SAMPLE_CONTACT}
          hiddenFields={hiddenFields}
          removeBlankFields={removeBlankFields}
        />
      </div>
    </div>
  );
}
