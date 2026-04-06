'use client';

import { useState, useEffect } from 'react';
import { usePhoneLists, usePhoneListContacts } from '@/hooks/use-phone-lists';
import { PageHeader } from '@/components/PageHeader';
import { EmptyState } from '@/components/EmptyState';
import { IPhoneFrame } from '@/components/IPhoneFrame';
import { ContactList } from '@/components/ContactList';
import { ContactCard } from '@/components/ContactCard';
import type { ContactCardData } from '@/components/ContactCard';
import { Skeleton } from '@/components/ui/skeleton';
import { Phone } from 'lucide-react';
import type { ContactDto, PhoneListDto } from '@/types/phone-list';

function mapContactDtoToCardData(dto: ContactDto): ContactCardData {
  return {
    displayName: dto.displayName,
    email: dto.email,
    jobTitle: dto.jobTitle,
    department: dto.department,
    office: dto.office,
    phone: dto.phone,
    mobilePhone: dto.mobilePhone,
    companyName: dto.companyName,
    streetAddress: dto.streetAddress,
    city: dto.city,
    state: dto.state,
    postalCode: dto.postalCode,
    country: dto.country,
  };
}

export default function PhoneListsPage() {
  const [selectedListId, setSelectedListId] = useState<number | null>(null);
  const [selectedContact, setSelectedContact] = useState<ContactDto | null>(null);
  const [contactPage, setContactPage] = useState(0);

  const { data: phoneLists, isLoading } = usePhoneLists();

  // Load contacts for the selected list (large page size for phone frame scrolling)
  const { data: contacts, isLoading: contactsLoading } =
    usePhoneListContacts(selectedListId ?? 0, contactPage + 1, 200);

  // Auto-select first list when data loads
  useEffect(() => {
    if (phoneLists && phoneLists.length > 0 && selectedListId === null) {
      setSelectedListId(phoneLists[0].id);
    }
  }, [phoneLists, selectedListId]);

  // Reset contact selection and page when list changes
  useEffect(() => {
    setSelectedContact(null);
    setContactPage(0);
  }, [selectedListId]);

  const selectedList = phoneLists?.find((l) => l.id === selectedListId);

  return (
    <div>
      <PageHeader
        title="Lists on Phones"
        description="Preview how contacts appear on employee phones."
      />

      {isLoading ? (
        <div className="space-y-3">
          {Array.from({ length: 4 }).map((_, i) => (
            <Skeleton key={i} className="h-16 w-full rounded-lg" />
          ))}
        </div>
      ) : !phoneLists || phoneLists.length === 0 ? (
        <EmptyState
          icon={Phone}
          heading="No targets"
          body="Targets will appear after tunnels are configured."
        />
      ) : (
        <div className="flex flex-col lg:flex-row gap-8">
          {/* Left panel: Target selector */}
          <div className="min-w-[320px] lg:w-[45%] space-y-2">
            {phoneLists.map((list: PhoneListDto) => (
              <div
                key={list.id}
                className={
                  list.id === selectedListId
                    ? 'border-l-2 border-gold bg-gold/5 rounded-lg p-4 cursor-pointer transition-colors'
                    : 'border border-border-default hover:border-gray-400 rounded-lg p-4 cursor-pointer transition-colors'
                }
                onClick={() => setSelectedListId(list.id)}
              >
                <div className="flex items-center gap-2">
                  <span className="text-sm font-bold text-navy">{list.name}</span>
                  {list.targetScope === 'specific_users' && (
                    <span className="text-xs bg-gold/10 text-gold px-2 py-0.5 rounded-full">
                      Specific users
                    </span>
                  )}
                </div>
                <div className="text-xs text-text-muted mt-1">
                  {list.contactCount} contacts &middot; {list.userCount} users
                </div>
              </div>
            ))}
          </div>

          {/* Right panel: iPhone frame preview */}
          <div className="lg:w-[55%] flex justify-center lg:justify-start">
            <IPhoneFrame title={selectedList?.name ?? ''}>
              <div className="relative h-full">
                {/* Contact list layer */}
                <div
                  className={
                    selectedContact !== null
                      ? 'absolute inset-0 transition-transform duration-200 ease-out -translate-x-full'
                      : 'absolute inset-0 transition-transform duration-200 ease-out translate-x-0'
                  }
                >
                  <ContactList
                    contacts={contacts ?? []}
                    onSelectContact={(c) => setSelectedContact(c)}
                    isLoading={contactsLoading}
                  />
                </div>

                {/* Contact card layer (slide-over) */}
                <div
                  className={
                    selectedContact !== null
                      ? 'absolute inset-0 transition-transform duration-200 ease-out translate-x-0 bg-white'
                      : 'absolute inset-0 transition-transform duration-200 ease-out translate-x-full bg-white'
                  }
                >
                  {selectedContact && (
                    <ContactCard
                      contact={mapContactDtoToCardData(selectedContact)}
                      onBack={() => setSelectedContact(null)}
                    />
                  )}
                </div>
              </div>
            </IPhoneFrame>
          </div>
        </div>
      )}
    </div>
  );
}
