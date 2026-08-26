'use client';

import { AlertCircle } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { useUnavailableMailboxes } from '@/hooks/use-targets';

function formatDate(iso: string | null): string {
  if (!iso) return '—';
  return new Date(iso).toLocaleString();
}

/**
 * Phase 2 (§2.1): mailboxes the worker skips because Graph reports no REST-enabled mailbox
 * (soft-deleted, on-prem, unlicensed). Each is re-probed weekly; the row disappears on the
 * first successful folder lookup. "N of M" reconciles with the dashboard's Target Users:
 * M − N is the number of mailboxes a run can deliver to.
 */
export function UnavailableMailboxes() {
  const { data, isLoading, error } = useUnavailableMailboxes();

  if (isLoading) {
    return <Skeleton className="h-24 w-full mt-8" />;
  }

  if (error || !data) {
    return (
      <p className="text-sm text-text-muted mt-8">
        Unavailable mailboxes could not be loaded.
      </p>
    );
  }

  return (
    <Card className="mt-8">
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <AlertCircle className="size-4 text-amber-600" strokeWidth={1.5} />
          Unavailable mailboxes ({data.unavailable} of {data.totalActive})
        </CardTitle>
        <p className="text-sm text-text-muted">
          Active accounts whose mailbox is inactive, soft-deleted, or hosted on-premise. Contacts
          are not delivered to them; each is re-checked weekly and drops off this list when it
          accepts contacts again.
        </p>
      </CardHeader>
      <CardContent className="pt-0">
        {data.items.length === 0 ? (
          <p className="text-sm text-text-muted py-4">
            Every active target mailbox currently accepts contacts.
          </p>
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Email</TableHead>
                <TableHead>Since</TableHead>
                <TableHead>Last checked</TableHead>
                <TableHead>Reason</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.items.map((m) => (
                <TableRow key={m.id}>
                  <TableCell className="font-medium">{m.displayName ?? '—'}</TableCell>
                  <TableCell className="font-mono text-xs break-all">{m.email}</TableCell>
                  <TableCell title={m.unavailableSince}>{formatDate(m.unavailableSince)}</TableCell>
                  <TableCell title={m.lastCheckedAt ?? undefined}>{formatDate(m.lastCheckedAt)}</TableCell>
                  <TableCell className="text-xs text-text-muted max-w-md truncate" title={m.reason ?? undefined}>
                    {m.reason ?? '—'}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </Card>
  );
}
