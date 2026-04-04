'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import {
  LayoutDashboard,
  Cable,
  Phone,
  SlidersHorizontal,
  ClipboardList,
  Settings,
} from 'lucide-react';

const navItems = [
  { label: 'Dashboard', href: '/', icon: LayoutDashboard },
  { label: 'Tunnels', href: '/tunnels', icon: Cable },
  { label: 'Phone Lists', href: '/lists', icon: Phone },
  { label: 'Field Profiles', href: '/fields', icon: SlidersHorizontal },
  { label: 'Runs & Logs', href: '/runs', icon: ClipboardList },
  { label: 'Settings', href: '/settings', icon: Settings },
];

export function Sidebar() {
  const pathname = usePathname();

  return (
    <aside className="fixed left-0 top-0 h-screen w-64 bg-navy flex flex-col">
      <div className="h-16 flex items-center px-4 border-b border-white/10">
        <span className="font-heading text-xl font-bold text-white">
          AFH Sync
        </span>
      </div>

      <nav className="flex-1 py-4">
        {navItems.map((item) => {
          const isActive = pathname === item.href;
          const Icon = item.icon;

          return (
            <Link
              key={item.href}
              href={item.href}
              className={`
                flex items-center gap-3 px-4 h-10 text-[0.8125rem] text-white/90
                transition-colors
                ${isActive
                  ? 'border-l-[3px] border-gold bg-white/5 text-white'
                  : 'border-l-[3px] border-transparent hover:bg-sidebar-hover'
                }
              `}
            >
              <Icon size={18} strokeWidth={1.5} />
              <span>{item.label}</span>
            </Link>
          );
        })}
      </nav>
    </aside>
  );
}
