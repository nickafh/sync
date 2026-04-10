'use client';

import { useState, useEffect, useMemo, useCallback } from 'react';
import { toast } from 'sonner';
import { Input } from '@/components/ui/input';
import { Button } from '@/components/ui/button';
import { api } from '@/lib/api';
import type { SourceContactDto, ContactExclusionInput } from '@/types/tunnel';
import { RefreshCw } from 'lucide-react';

interface ContactExclusionManagerProps {
  tunnelId: number;
}

export function ContactExclusionManager({ tunnelId }: ContactExclusionManagerProps) {
  const [contacts, setContacts] = useState<SourceContactDto[]>([]);
  const [search, setSearch] = useState('');
  const [loading, setLoading] = useState(true);
  const [resolving, setResolving] = useState(false);
  const [saving, setSaving] = useState(false);
  const [dirty, setDirty] = useState(false);

  useEffect(() => {
    async function load() {
      try {
        // Always resolve from Graph to get the complete contact list from all sources.
        // The source-contacts endpoint only returns previously-synced contacts, which
        // misses contacts from sources that haven't synced yet or failed.
        const data = await api.contactExclusions.resolve(tunnelId);
        setContacts(data);
      } catch {
        setContacts([]);
      } finally {
        setLoading(false);
      }
    }
    load();
  }, [tunnelId]);

  const handleResolve = useCallback(async () => {
    setResolving(true);
    try {
      const data = await api.contactExclusions.resolve(tunnelId);
      setContacts(data);
      toast.success(`Loaded ${data.length} contacts from source.`);
    } catch {
      toast.error('Failed to load contacts from source.');
    } finally {
      setResolving(false);
    }
  }, [tunnelId]);

  const filtered = useMemo(() => {
    if (!search.trim()) return contacts;
    const q = search.toLowerCase();
    return contacts.filter(
      (c) =>
        c.displayName?.toLowerCase().includes(q) ||
        c.email?.toLowerCase().includes(q) ||
        c.companyName?.toLowerCase().includes(q)
    );
  }, [contacts, search]);

  const toggleContact = useCallback((entraId: string) => {
    setContacts((prev) =>
      prev.map((c) =>
        c.entraId === entraId ? { ...c, isExcluded: !c.isExcluded } : c
      )
    );
    setDirty(true);
  }, []);

  const includeAll = useCallback(() => {
    setContacts((prev) => prev.map((c) => ({ ...c, isExcluded: false })));
    setDirty(true);
  }, []);

  const excludeAll = useCallback(() => {
    setContacts((prev) => prev.map((c) => ({ ...c, isExcluded: true })));
    setDirty(true);
  }, []);

  const handleSave = useCallback(async () => {
    setSaving(true);
    try {
      const exclusions: ContactExclusionInput[] = contacts
        .filter((c) => c.isExcluded)
        .map((c) => ({
          entraId: c.entraId,
          displayName: c.displayName,
          email: c.email,
        }));

      await api.contactExclusions.updateExclusions(tunnelId, exclusions);
      setDirty(false);
      toast.success(`Saved ${exclusions.length} exclusion(s).`);
    } catch {
      toast.error('Failed to save exclusions.');
    } finally {
      setSaving(false);
    }
  }, [contacts, tunnelId]);

  const includedCount = contacts.filter((c) => !c.isExcluded).length;
  const excludedCount = contacts.filter((c) => c.isExcluded).length;

  if (loading) {
    return (
      <div className="text-sm text-text-muted py-4 text-center">
        Loading contacts...
      </div>
    );
  }

  if (contacts.length === 0) {
    return (
      <div className="text-center py-6 space-y-3">
        <p className="text-sm text-text-muted">
          No contacts loaded yet.
        </p>
        <Button
          variant="outline"
          onClick={handleResolve}
          disabled={resolving}
          className="cursor-pointer"
        >
          <RefreshCw className={`h-4 w-4 mr-2 ${resolving ? 'animate-spin' : ''}`} />
          {resolving ? 'Loading from Graph...' : 'Load Contacts'}
        </Button>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div>
          <p className="text-xs text-text-muted">
            {includedCount} included, {excludedCount} excluded of {contacts.length} total
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Button
            variant="ghost"
            size="sm"
            onClick={handleResolve}
            disabled={resolving || saving}
            className="cursor-pointer"
            title="Refresh contacts from source"
          >
            <RefreshCw className={`h-3.5 w-3.5 ${resolving ? 'animate-spin' : ''}`} />
          </Button>
          <Button variant="outline" size="sm" onClick={includeAll} disabled={saving}>
            Include All
          </Button>
          <Button variant="outline" size="sm" onClick={excludeAll} disabled={saving}>
            Exclude All
          </Button>
          {dirty && (
            <Button
              size="sm"
              className="bg-gold text-white hover:bg-gold/90"
              onClick={handleSave}
              disabled={saving}
            >
              {saving ? 'Saving...' : 'Save'}
            </Button>
          )}
        </div>
      </div>

      <Input
        placeholder="Search by name, email, or company..."
        value={search}
        onChange={(e) => setSearch(e.target.value)}
      />

      <div className="border rounded-lg divide-y max-h-[400px] overflow-y-auto">
        {filtered.length === 0 ? (
          <div className="text-sm text-text-muted py-6 text-center">
            No contacts match your search.
          </div>
        ) : (
          filtered.map((contact) => (
            <label
              key={contact.entraId}
              className={`flex items-center gap-3 px-4 py-2.5 cursor-pointer hover:bg-muted/50 transition-colors ${
                contact.isExcluded ? 'opacity-50' : ''
              }`}
            >
              <input
                type="checkbox"
                checked={!contact.isExcluded}
                onChange={() => toggleContact(contact.entraId)}
                className="rounded"
              />
              <div className="flex-1 min-w-0">
                <p className="text-sm font-medium truncate">
                  {contact.displayName || '(No name)'}
                </p>
                <p className="text-xs text-text-muted truncate">
                  {[contact.email, contact.companyName, contact.jobTitle]
                    .filter(Boolean)
                    .join(' \u00B7 ')}
                </p>
              </div>
            </label>
          ))
        )}
      </div>
    </div>
  );
}
