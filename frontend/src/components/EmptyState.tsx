import type { LucideIcon } from 'lucide-react';
import Link from 'next/link';
import { Button } from '@/components/ui/button';

interface EmptyStateProps {
  icon: LucideIcon;
  heading: string;
  body: string;
  ctaLabel?: string;
  ctaHref?: string;
  onCtaClick?: () => void;
}

export function EmptyState({
  icon: Icon,
  heading,
  body,
  ctaLabel,
  ctaHref,
  onCtaClick,
}: EmptyStateProps) {
  return (
    <div className="flex flex-col items-center justify-center py-16">
      <Icon className="size-12 text-text-muted" strokeWidth={1.5} />
      <h3 className="text-lg font-bold font-heading text-navy mt-4">{heading}</h3>
      <p className="text-sm text-text-muted mt-2">{body}</p>
      {ctaLabel && ctaHref && (
        <Link href={ctaHref} className="mt-4">
          <Button>{ctaLabel}</Button>
        </Link>
      )}
      {ctaLabel && onCtaClick && !ctaHref && (
        <Button className="mt-4" onClick={onCtaClick}>
          {ctaLabel}
        </Button>
      )}
    </div>
  );
}
