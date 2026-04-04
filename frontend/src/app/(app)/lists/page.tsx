'use client';

import { useState, useEffect } from 'react';
import { usePhoneLists, usePhoneListContacts } from '@/hooks/use-phone-lists';
import { PageHeader } from '@/components/PageHeader';
import { DataTable } from '@/components/DataTable';
import { StatusBadge } from '@/components/StatusBadge';
import { EmptyState } from '@/components/EmptyState';
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { Phone, ChevronDown, ChevronRight } from 'lucide-react';
import type { ColumnDef } from '@tanstack/react-table';
import type { PhoneListDto, ContactDto } from '@/types/phone-list';

const contactPageSize = 25;

const contactColumns: ColumnDef<ContactDto, unknown>[] = [
  {
    accessorKey: 'displayName',
    header: 'Name',
  },
  {
    accessorKey: 'email',
    header: 'Email',
  },
  {
    accessorKey: 'phone',
    header: 'Phone',
  },
  {
    accessorKey: 'jobTitle',
    header: 'Title',
  },
  {
    accessorKey: 'department',
    header: 'Department',
  },
];

export default function PhoneListsPage() {
  const [expandedListId, setExpandedListId] = useState<number | null>(null);
  const [contactPage, setContactPage] = useState(0);

  const { data: phoneLists, isLoading } = usePhoneLists();

  // API is 1-indexed; request pageSize + 1 for N+1 detection (hook already does +1)
  const { data: rawContacts, isLoading: contactsLoading } =
    usePhoneListContacts(expandedListId ?? 0, contactPage + 1, contactPageSize);

  // N+1 pagination: if we got more than pageSize, there's a next page
  const hasNextContactPage = (rawContacts?.length ?? 0) > contactPageSize;
  const contacts = rawContacts?.slice(0, contactPageSize) ?? [];

  // Reset contact page when switching lists
  useEffect(() => {
    setContactPage(0);
  }, [expandedListId]);

  const expandedList = phoneLists?.find((l) => l.id === expandedListId);

  const phoneListColumns: ColumnDef<PhoneListDto, unknown>[] = [
    {
      id: 'expand',
      header: '',
      cell: ({ row }) => {
        const isExpanded = row.original.id === expandedListId;
        return isExpanded ? (
          <ChevronDown className="size-4 text-text-muted" />
        ) : (
          <ChevronRight className="size-4 text-text-muted" />
        );
      },
      size: 40,
    },
    {
      accessorKey: 'name',
      header: 'Name',
      cell: ({ getValue }) => (
        <span className="font-medium text-navy">{getValue<string>()}</span>
      ),
    },
    {
      accessorKey: 'contactCount',
      header: () => <span className="text-right w-full block">Contacts</span>,
      cell: ({ getValue }) => (
        <span className="text-right w-full block">{getValue<number>()}</span>
      ),
    },
    {
      accessorKey: 'userCount',
      header: () => <span className="text-right w-full block">Users</span>,
      cell: ({ getValue }) => (
        <span className="text-right w-full block">{getValue<number>()}</span>
      ),
    },
    {
      id: 'sourceTunnels',
      header: 'Source Tunnels',
      accessorFn: (row) =>
        row.sourceTunnels.map((t) => t.name).join(', '),
      cell: ({ getValue }) => (
        <span className="truncate max-w-[200px] block">
          {getValue<string>() || '--'}
        </span>
      ),
    },
    {
      accessorKey: 'lastSyncStatus',
      header: 'Last Sync',
      cell: ({ getValue }) => {
        const status = getValue<string | null>();
        return status ? <StatusBadge status={status} /> : <span className="text-text-muted">--</span>;
      },
    },
  ];

  return (
    <div>
      <PageHeader
        title="Phone Lists"
        description="Browse contact lists delivered to user mailboxes."
      />

      {isLoading ? (
        <div className="space-y-3">
          {Array.from({ length: 4 }).map((_, i) => (
            <Skeleton key={i} className="h-12 w-full rounded-lg" />
          ))}
        </div>
      ) : !phoneLists || phoneLists.length === 0 ? (
        <EmptyState
          icon={Phone}
          heading="No phone lists"
          body="Phone lists will appear here after tunnels are configured and a sync run completes."
        />
      ) : (
        <>
          <DataTable<PhoneListDto>
            columns={phoneListColumns}
            data={phoneLists}
            isLoading={false}
            pageIndex={0}
            pageSize={phoneLists.length}
            hasNextPage={false}
            onPageChange={() => {}}
            onRowClick={(row) => {
              setExpandedListId((prev) =>
                prev === row.id ? null : row.id,
              );
            }}
          />

          {expandedListId !== null && expandedList && (
            <Card className="mt-6">
              <CardHeader>
                <CardTitle className="text-lg font-bold font-heading text-navy">
                  {expandedList.name}
                </CardTitle>
                <p className="text-sm text-text-muted">Contacts</p>
              </CardHeader>
              <CardContent>
                {contacts.length === 0 && !contactsLoading ? (
                  <EmptyState
                    icon={Phone}
                    heading="No contacts synced"
                    body="Contacts will appear here after the next sync run."
                  />
                ) : (
                  <DataTable<ContactDto>
                    columns={contactColumns}
                    data={contacts}
                    isLoading={contactsLoading}
                    pageIndex={contactPage}
                    pageSize={contactPageSize}
                    hasNextPage={hasNextContactPage}
                    onPageChange={setContactPage}
                  />
                )}
              </CardContent>
            </Card>
          )}
        </>
      )}
    </div>
  );
}
