'use client';

import { Button } from '@/components/ui/button';

export default function ErrorBoundary({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  return (
    <div className="flex flex-col items-center justify-center min-h-[50vh] gap-4">
      <h2 className="font-heading text-2xl text-navy">Something went wrong</h2>
      <p className="text-text-muted text-sm max-w-md text-center">
        {error.message || 'An unexpected error occurred.'}
      </p>
      <Button onClick={reset} variant="outline">
        Try again
      </Button>
    </div>
  );
}
