import { cn } from '@/lib/utils';
import { Card, CardContent } from '@/components/ui/card';

export function KPICard({
  label,
  value,
  className,
}: {
  label: string;
  value: string | number;
  className?: string;
}) {
  return (
    <Card className={cn('p-5 bg-white border-border-default shadow-sm', className)}>
      <CardContent className="p-0">
        <span className="text-sm font-normal uppercase tracking-wide text-text-muted">
          {label}
        </span>
        <div className="text-2xl font-bold text-navy mt-1">{value}</div>
      </CardContent>
    </Card>
  );
}
