'use client';

import { useState } from 'react';
import { toast } from 'sonner';
import { RefreshCw } from 'lucide-react';
import { useRefreshDdg } from '@/hooks/use-tunnels';
import { Button } from '@/components/ui/button';
import {
  Popover,
  PopoverTrigger,
  PopoverContent,
} from '@/components/ui/popover';
import {
  Tooltip,
  TooltipTrigger,
  TooltipContent,
} from '@/components/ui/tooltip';

interface DDGRefreshButtonProps {
  tunnelId: number;
  sourceId: number;
}

export function DDGRefreshButton({ tunnelId, sourceId }: DDGRefreshButtonProps) {
  const [popoverOpen, setPopoverOpen] = useState(false);
  const refreshDdg = useRefreshDdg();

  const handleRefresh = () => {
    setPopoverOpen(false);
    refreshDdg.mutate({ tunnelId, sourceId }, {
      onSuccess: () => {
        toast.success('Filter refreshed successfully.');
      },
      onError: () => {
        toast.error('Failed to refresh filter. Please try again.');
      },
    });
  };

  return (
    <Popover open={popoverOpen} onOpenChange={setPopoverOpen}>
      <Tooltip>
        <TooltipTrigger
          render={
            <PopoverTrigger
              render={
                <Button
                  size="sm"
                  variant="outline"
                  disabled={refreshDdg.isPending}
                />
              }
            />
          }
        >
          <RefreshCw
            className={`size-3.5 ${refreshDdg.isPending ? 'animate-spin' : ''}`}
          />
        </TooltipTrigger>
        <TooltipContent>Refresh filter from Exchange</TooltipContent>
      </Tooltip>
      <PopoverContent className="w-auto p-3">
        <p className="text-sm">Re-read filter from Exchange?</p>
        <div className="flex items-center gap-2 mt-2">
          <span
            className="text-sm text-text-muted cursor-pointer"
            onClick={() => setPopoverOpen(false)}
          >
            Cancel
          </span>
          <Button size="sm" variant="outline" onClick={handleRefresh}>
            Refresh DDG
          </Button>
        </div>
      </PopoverContent>
    </Popover>
  );
}
