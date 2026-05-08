'use client';

import { useState } from 'react';
import { toast } from 'sonner';
import { Download, Loader2 } from 'lucide-react';
import { PageHeader } from '@/components/PageHeader';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';

const FALLBACK_FILENAME = 'afh-sync-contacts.xlsx';

function filenameFromContentDisposition(header: string | null): string {
  if (!header) return FALLBACK_FILENAME;
  const match = /filename\*?=(?:UTF-8'')?["']?([^"';]+)/i.exec(header);
  return match?.[1] ?? FALLBACK_FILENAME;
}

export default function ExportsPage() {
  const [downloading, setDownloading] = useState(false);

  async function handleDownload() {
    setDownloading(true);
    try {
      const res = await fetch('/api/exports/contacts.xlsx', {
        credentials: 'include',
      });

      if (res.status === 401) {
        window.location.href = '/login';
        return;
      }

      if (!res.ok) {
        const body = await res.json().catch(() => ({}));
        throw new Error(body.message ?? `Export failed: ${res.status}`);
      }

      const blob = await res.blob();
      const filename = filenameFromContentDisposition(
        res.headers.get('content-disposition'),
      );
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = filename;
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);

      toast.success('Download started');
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Export failed';
      toast.error(message);
    } finally {
      setDownloading(false);
    }
  }

  return (
    <div className="space-y-6">
      <PageHeader
        title="Export"
        description="Download data from the sync platform."
      />

      <Card>
        <CardHeader>
          <CardTitle>Synced contacts by tunnel</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <p className="text-sm text-muted-foreground">
            Downloads an Excel workbook with one sheet per tunnel. Each sheet
            lists the contacts currently being synced through that tunnel
            (live contacts only — stale contacts are excluded).
          </p>
          <Button onClick={handleDownload} disabled={downloading}>
            {downloading ? (
              <Loader2 className="mr-2 h-4 w-4 animate-spin" />
            ) : (
              <Download className="mr-2 h-4 w-4" />
            )}
            {downloading ? 'Building workbook…' : 'Download .xlsx'}
          </Button>
        </CardContent>
      </Card>
    </div>
  );
}
