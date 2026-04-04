'use client';

import type { ReactNode } from 'react';
import { Signal, Wifi, BatteryFull } from 'lucide-react';
import { ScrollArea } from '@/components/ui/scroll-area';

interface IPhoneFrameProps {
  children: ReactNode;
  title?: string;
}

export function IPhoneFrame({ children, title }: IPhoneFrameProps) {
  return (
    <div className="w-80 aspect-[9/19.5] rounded-[40px] border-[3px] border-navy bg-white overflow-hidden shadow-xl relative flex flex-col">
      {/* Dynamic Island */}
      <div className="absolute top-2 left-1/2 -translate-x-1/2 w-[120px] h-[28px] bg-black rounded-full z-20" />

      {/* Status bar */}
      <div className="h-12 bg-stone-100 flex items-end justify-between px-6 pb-1 shrink-0">
        <span className="text-xs font-bold">9:41</span>
        <div className="flex items-center gap-1">
          <Signal size={12} className="text-gray-600" />
          <Wifi size={12} className="text-gray-600" />
          <BatteryFull size={12} className="text-gray-600" />
        </div>
      </div>

      {/* Header (optional) */}
      {title && (
        <div className="h-11 flex items-center justify-center border-b border-border-default shrink-0">
          <span className="text-sm font-bold">{title}</span>
        </div>
      )}

      {/* Content area */}
      <div className="flex-1 overflow-hidden relative">
        <ScrollArea className="h-full">
          {children}
        </ScrollArea>
      </div>

      {/* Home indicator */}
      <div className="absolute bottom-2 left-1/2 -translate-x-1/2 w-[134px] h-[5px] bg-gray-300 rounded-full z-20" />
    </div>
  );
}
