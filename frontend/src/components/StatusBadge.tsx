import { cn } from '@/lib/utils';

const statusConfig: Record<string, { bg: string; text: string; dot: string; pulse?: boolean }> = {
  active: { bg: 'bg-emerald-50', text: 'text-emerald-700', dot: 'bg-emerald-500' },
  completed: { bg: 'bg-emerald-50', text: 'text-emerald-700', dot: 'bg-emerald-500' },
  success: { bg: 'bg-emerald-50', text: 'text-emerald-700', dot: 'bg-emerald-500' },
  running: { bg: 'bg-blue-50', text: 'text-blue-700', dot: 'bg-blue-500', pulse: true },
  pending: { bg: 'bg-blue-50', text: 'text-blue-700', dot: 'bg-blue-500', pulse: true },
  inactive: { bg: 'bg-gray-100', text: 'text-gray-600', dot: 'bg-gray-400' },
  disabled: { bg: 'bg-gray-100', text: 'text-gray-600', dot: 'bg-gray-400' },
  failed: { bg: 'bg-red-50', text: 'text-red-700', dot: 'bg-red-500' },
  warning: { bg: 'bg-amber-50', text: 'text-amber-700', dot: 'bg-amber-500' },
  partial: { bg: 'bg-amber-50', text: 'text-amber-700', dot: 'bg-amber-500' },
  dry_run: { bg: 'bg-violet-50', text: 'text-violet-700', dot: 'bg-violet-500' },
};

const defaultConfig = { bg: 'bg-gray-100', text: 'text-gray-600', dot: 'bg-gray-400' };

function formatLabel(status: string): string {
  return status
    .replace(/_/g, ' ')
    .replace(/\b\w/g, (c) => c.toUpperCase());
}

export function StatusBadge({ status, className }: { status: string; className?: string }) {
  const config = statusConfig[status.toLowerCase()] ?? defaultConfig;

  return (
    <span
      className={cn(
        'inline-flex items-center gap-1.5 rounded-full px-2.5 py-0.5 text-xs font-normal uppercase tracking-wide',
        config.bg,
        config.text,
        className,
      )}
    >
      <span
        className={cn(
          'w-1.5 h-1.5 rounded-full',
          config.dot,
          config.pulse && 'animate-pulse',
        )}
      />
      {formatLabel(status)}
    </span>
  );
}
