'use client';

import { useAuth } from '@/lib/auth';

export default function DashboardPage() {
  const { isLoading } = useAuth();

  if (isLoading) {
    return (
      <div className="animate-pulse">
        <div className="h-10 bg-gray-200 rounded w-40 mb-2" />
        <div className="h-4 bg-gray-200 rounded w-80" />
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4 mt-6">
          {[1, 2, 3, 4].map((i) => (
            <div key={i} className="bg-white rounded-lg border border-border-default p-5 shadow-sm">
              <div className="h-3 bg-gray-200 rounded w-24 mb-2" />
              <div className="h-8 bg-gray-200 rounded w-12 mt-1" />
            </div>
          ))}
        </div>
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-6 mt-6">
          <div className="h-32 bg-gray-200 rounded-lg" />
          <div className="h-32 bg-gray-200 rounded-lg" />
        </div>
      </div>
    );
  }

  return (
    <div>
      {/* Header */}
      <h1 className="font-heading text-[2rem] font-bold text-navy">Dashboard</h1>
      <p className="text-sm text-text-muted mt-1">
        Monitor tunnels, phone-visible lists, and sync activity from one place.
      </p>

      {/* KPI cards — Phase 4 (DASH-01) will replace placeholders with live data */}
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4 mt-6">
        <div className="bg-white rounded-lg border border-border-default p-5 shadow-sm">
          <span className="text-xs font-medium text-text-muted uppercase tracking-wide">
            Active Tunnels
          </span>
          <div className="text-2xl font-semibold text-navy mt-1">--</div>
        </div>

        <div className="bg-white rounded-lg border border-border-default p-5 shadow-sm">
          <span className="text-xs font-medium text-text-muted uppercase tracking-wide">
            Phone Lists
          </span>
          <div className="text-2xl font-semibold text-navy mt-1">--</div>
        </div>

        <div className="bg-white rounded-lg border border-border-default p-5 shadow-sm">
          <span className="text-xs font-medium text-text-muted uppercase tracking-wide">
            Target Users
          </span>
          <div className="text-2xl font-semibold text-navy mt-1">--</div>
        </div>

        <div className="bg-white rounded-lg border border-border-default p-5 shadow-sm">
          <span className="text-xs font-medium text-text-muted uppercase tracking-wide">
            Last Sync
          </span>
          <div className="text-2xl font-semibold text-navy mt-1">--</div>
        </div>
      </div>

      {/* Two-column section — Phase 4 (DASH-02, DASH-03) will populate with live data */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6 mt-6">
        <div className="bg-white rounded-lg border border-border-default p-5 shadow-sm">
          <h2 className="text-lg font-semibold text-navy">Active Tunnels</h2>
          <p className="text-sm text-text-muted mt-2">No tunnels configured yet.</p>
        </div>

        <div className="bg-white rounded-lg border border-border-default p-5 shadow-sm">
          <h2 className="text-lg font-semibold text-navy">Recent Runs</h2>
          <p className="text-sm text-text-muted mt-2">No sync runs yet.</p>
        </div>
      </div>
    </div>
  );
}
