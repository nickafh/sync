'use client';

import { Suspense, useEffect, useState } from 'react';
import { useRouter, useSearchParams } from 'next/navigation';
import { AlertCircle, Search, UserSearch } from 'lucide-react';
import { PageHeader } from '@/components/PageHeader';
import { EmptyState } from '@/components/EmptyState';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
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
import { useUserFolderState } from '@/hooks/use-user-lookup';

function formatRelative(iso: string | null): string {
  if (!iso) return 'never';
  const then = new Date(iso).getTime();
  const diff = Date.now() - then;
  if (diff < 0) return 'just now';
  const mins = Math.floor(diff / 60_000);
  if (mins < 1) return 'just now';
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  if (days < 30) return `${days}d ago`;
  return new Date(iso).toLocaleDateString();
}

function formatAbsolute(iso: string | null): string {
  if (!iso) return '—';
  return new Date(iso).toLocaleString();
}

function UsersPageInner() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const urlEmail = searchParams.get('email') ?? '';
  const [input, setInput] = useState(urlEmail);
  const [submittedEmail, setSubmittedEmail] = useState<string | null>(
    urlEmail || null,
  );

  useEffect(() => {
    setInput(urlEmail);
    setSubmittedEmail(urlEmail || null);
  }, [urlEmail]);

  const {
    data,
    isLoading,
    isFetching,
    error,
  } = useUserFolderState(submittedEmail);

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    const trimmed = input.trim();
    if (!trimmed) return;
    setSubmittedEmail(trimmed);
    const params = new URLSearchParams(searchParams);
    params.set('email', trimmed);
    router.replace(`/users?${params.toString()}`);
  };

  return (
    <div>
      <PageHeader
        title="User Lookup"
        description="Inspect a target user's Exchange contact folders to diagnose missing-contact reports."
      />

      <Card className="mb-6">
        <CardContent className="pt-4">
          <form onSubmit={handleSubmit} className="flex items-center gap-3">
            <div className="relative flex-1">
              <Search
                className="absolute left-3 top-1/2 -translate-y-1/2 size-4 text-text-muted"
                strokeWidth={1.5}
              />
              <Input
                type="email"
                placeholder="user@afhsir.com"
                value={input}
                onChange={(e) => setInput(e.target.value)}
                className="pl-9"
                aria-label="User email"
                autoFocus
              />
            </div>
            <Button type="submit" disabled={!input.trim() || isFetching}>
              {isFetching ? 'Loading…' : 'Load'}
            </Button>
          </form>
        </CardContent>
      </Card>

      {!submittedEmail && (
        <EmptyState
          icon={UserSearch}
          heading="Enter an email to start"
          body="Type the user's email and press Load to see their Exchange contact folders, contact counts, and last-sync times."
        />
      )}

      {submittedEmail && isLoading && (
        <div className="space-y-4">
          <Skeleton className="h-24 w-full" />
          <Skeleton className="h-64 w-full" />
        </div>
      )}

      {submittedEmail && error && (
        <Card className="border-red-200 bg-red-50">
          <CardContent className="pt-4 flex items-start gap-3">
            <AlertCircle
              className="size-5 text-red-700 shrink-0 mt-0.5"
              strokeWidth={1.5}
            />
            <div>
              <p className="font-medium text-red-900">Lookup failed</p>
              <p className="text-sm text-red-800 mt-1">
                {error instanceof Error ? error.message : 'Unknown error'}
              </p>
            </div>
          </CardContent>
        </Card>
      )}

      {data && (
        <>
          <Card className="mb-6">
            <CardHeader>
              <CardTitle>
                {data.displayName ?? data.email}
              </CardTitle>
            </CardHeader>
            <CardContent className="pt-0">
              <dl className="grid grid-cols-1 sm:grid-cols-3 gap-4 text-sm">
                <div>
                  <dt className="text-text-muted">Email</dt>
                  <dd className="font-mono text-xs mt-1 break-all">
                    {data.email}
                  </dd>
                </div>
                <div>
                  <dt className="text-text-muted">Entra ID</dt>
                  <dd className="font-mono text-xs mt-1 break-all">
                    {data.entraId ?? '—'}
                  </dd>
                </div>
                <div>
                  <dt className="text-text-muted">Tracked mailbox</dt>
                  <dd className="mt-1">
                    {data.isTrackedTargetMailbox ? (
                      <span className="text-emerald-700 font-medium">
                        Yes (#{data.targetMailboxId})
                      </span>
                    ) : (
                      <span className="text-text-muted">
                        Not in our TargetMailboxes table
                      </span>
                    )}
                  </dd>
                </div>
              </dl>
            </CardContent>
          </Card>

          <Card className="mb-6">
            <CardHeader>
              <CardTitle>
                Contact folders ({data.folders.length})
              </CardTitle>
            </CardHeader>
            <CardContent className="pt-0">
              {data.folders.length === 0 ? (
                <p className="text-sm text-text-muted py-4">
                  No contact folders found in the user&apos;s mailbox.
                </p>
              ) : (
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>Folder</TableHead>
                      <TableHead className="text-right">Graph count</TableHead>
                      <TableHead>Matched tunnel</TableHead>
                      <TableHead className="text-right">Expected</TableHead>
                      <TableHead className="text-right">Delta</TableHead>
                      <TableHead>Last synced</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {data.folders.map((f) => {
                      const expected = f.expectedContactCount;
                      const delta =
                        expected === null
                          ? null
                          : f.graphContactCount - expected;
                      const deltaClass =
                        delta === null || delta === 0
                          ? 'text-text-muted'
                          : delta > 0
                            ? 'text-amber-700'
                            : 'text-red-700';
                      return (
                        <TableRow key={f.folderId}>
                          <TableCell className="font-medium">
                            {f.folderName}
                          </TableCell>
                          <TableCell className="text-right tabular-nums">
                            {f.graphContactCount}
                          </TableCell>
                          <TableCell>
                            {f.matchedTunnelId ? (
                              <a
                                href={`/tunnels/${f.matchedTunnelId}`}
                                className="text-navy hover:underline"
                              >
                                {f.matchedTunnelName}
                              </a>
                            ) : (
                              <span className="text-text-muted">—</span>
                            )}
                          </TableCell>
                          <TableCell className="text-right tabular-nums">
                            {expected ?? (
                              <span className="text-text-muted">—</span>
                            )}
                          </TableCell>
                          <TableCell
                            className={`text-right tabular-nums ${deltaClass}`}
                          >
                            {delta === null
                              ? '—'
                              : delta > 0
                                ? `+${delta}`
                                : delta}
                          </TableCell>
                          <TableCell title={formatAbsolute(f.lastSyncedAt)}>
                            {formatRelative(f.lastSyncedAt)}
                          </TableCell>
                        </TableRow>
                      );
                    })}
                  </TableBody>
                </Table>
              )}
            </CardContent>
          </Card>

          {data.orphanTunnels.length > 0 && (
            <Card className="border-amber-200 bg-amber-50">
              <CardHeader>
                <CardTitle className="text-amber-900 flex items-center gap-2">
                  <AlertCircle size={18} strokeWidth={1.5} />
                  Tunnels with no matching folder ({data.orphanTunnels.length})
                </CardTitle>
              </CardHeader>
              <CardContent className="pt-0">
                <p className="text-sm text-amber-900 mb-3">
                  Sync state exists for these tunnels but no Graph folder with a
                  matching name was found — the folder may have been deleted
                  client-side, renamed, or never provisioned.
                </p>
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>Tunnel</TableHead>
                      <TableHead className="text-right">
                        Expected contacts
                      </TableHead>
                      <TableHead>Last synced</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {data.orphanTunnels.map((t) => (
                      <TableRow key={t.tunnelId}>
                        <TableCell className="font-medium">
                          <a
                            href={`/tunnels/${t.tunnelId}`}
                            className="text-navy hover:underline"
                          >
                            {t.tunnelName}
                          </a>
                        </TableCell>
                        <TableCell className="text-right tabular-nums">
                          {t.expectedContactCount}
                        </TableCell>
                        <TableCell title={formatAbsolute(t.lastSyncedAt)}>
                          {formatRelative(t.lastSyncedAt)}
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </CardContent>
            </Card>
          )}
        </>
      )}
    </div>
  );
}

export default function UsersPage() {
  return (
    <Suspense fallback={<Skeleton className="h-64 w-full" />}>
      <UsersPageInner />
    </Suspense>
  );
}
