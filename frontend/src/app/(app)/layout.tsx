'use client';

import type { ReactNode } from 'react';
import { QueryClientProvider } from '@tanstack/react-query';
import { Toaster } from '@/components/ui/sonner';
import { Sidebar } from '@/components/layout/Sidebar';
import { AuthProvider } from '@/lib/auth';
import { getQueryClient } from '@/lib/query-client';

export default function AppLayout({ children }: { children: ReactNode }) {
  const queryClient = getQueryClient();

  return (
    <QueryClientProvider client={queryClient}>
      <AuthProvider>
        <div className="flex min-h-screen">
          <Sidebar />
          <main className="flex-1 ml-64 p-8 bg-warm-white min-h-screen">
            {children}
          </main>
        </div>
      </AuthProvider>
      <Toaster position="bottom-right" />
    </QueryClientProvider>
  );
}
