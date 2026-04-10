'use client';

import { useState, useEffect, useMemo, useCallback } from 'react';
import { toast } from 'sonner';
import { Input } from '@/components/ui/input';
import { Button } from '@/components/ui/button';
import { api } from '@/lib/api';
import type { OrgContactDto, OrgContactFilterInput } from '@/types/tunnel';

interface OrgContactManagerProps {
  tunnelId: number;
}

export function OrgContactManager({ tunnelId }: OrgContactManagerProps) {
  const [orgContacts, setOrgContacts] = useState<OrgContactDto[]>([]);
  const [filters, setFilters] = useState<Map<string, boolean>>(new Map());
  const [search, setSearch] = useState('');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [dirty, setDirty] = useState(false);

  // Load org contacts from Graph and existing filters from DB
  useEffect(() => {
    async function load() {
      try {
        const [contacts, existingFilters] = await Promise.all([
          api.orgContacts.list(),
          api.orgContacts.getFilters(tunnelId),
        ]);

        // Build exclusion map from existing filters
        const filterMap = new Map<string, boolean>();
        for (const f of existingFilters) {
          filterMap.set(f.orgContactId, f.isExcluded);
        }

        setOrgContacts(contacts);
        setFilters(filterMap);
      } catch {
        toast.error('Failed to load organization contacts.');
      } finally {
        setLoading(false);
      }
    }
    load();
  }, [tunnelId]);

  // Merge contacts with filter state
  const contactsWithState = useMemo(() => {
    return orgContacts.map((c) => ({
      ...c,
      isExcluded: filters.get(c.id) ?? false,
    }));
  }, [orgContacts, filters]);

  // Filtered by search
  const filtered = useMemo(() => {
    if (!search.trim()) return contactsWithState;
    const q = search.toLowerCase();
    return contactsWithState.filter(
      (c) =>
        c.displayName?.toLowerCase().includes(q) ||
        c.email?.toLowerCase().includes(q) ||
        c.companyName?.toLowerCase().includes(q)
    );
  }, [contactsWithState, search]);

  // Get unique companies for bulk actions
  const companies = useMemo(() => {
    const companyMap = new Map<string, { total: number; excluded: number }>();
    for (const c of contactsWithState) {
      const name = c.companyName || '(No company)';
      const existing = companyMap.get(name) || { total: 0, excluded: 0 };
      existing.total++;
      if (c.isExcluded) existing.excluded++;
      companyMap.set(name, existing);
    }
    return Array.from(companyMap.entries())
      .sort(([a], [b]) => a.localeCompare(b));
  }, [contactsWithState]);

  const toggleContact = useCallback((contactId: string) => {
    setFilters((prev) => {
      const next = new Map(prev);
      next.set(contactId, !(next.get(contactId) ?? false));
      return next;
    });
    setDirty(true);
  }, []);

  const toggleCompany = useCallback((companyName: string, exclude: boolean) => {
    setFilters((prev) => {
      const next = new Map(prev);
      for (const c of orgContacts) {
        const cName = c.companyName || '(No company)';
        if (cName === companyName) {
          next.set(c.id, exclude);
        }
      }
      return next;
    });
    setDirty(true);
  }, [orgContacts]);

  const selectAll = useCallback(() => {
    setFilters((prev) => {
      const next = new Map(prev);
      for (const c of orgContacts) {
        next.set(c.id, false); // not excluded = included
      }
      return next;
    });
    setDirty(true);
  }, [orgContacts]);

  const deselectAll = useCallback(() => {
    setFilters((prev) => {
      const next = new Map(prev);
      for (const c of orgContacts) {
        next.set(c.id, true); // excluded
      }
      return next;
    });
    setDirty(true);
  }, [orgContacts]);

  const handleSave = useCallback(async () => {
    setSaving(true);
    try {
      const filterInputs: OrgContactFilterInput[] = orgContacts.map((c) => ({
        orgContactId: c.id,
        displayName: c.displayName,
        email: c.email,
        companyName: c.companyName,
        isExcluded: filters.get(c.id) ?? false,
      }));

      await api.orgContacts.updateFilters(tunnelId, filterInputs);
      setDirty(false);
      toast.success('Contact filters saved.');
    } catch {
      toast.error('Failed to save filters.');
    } finally {
      setSaving(false);
    }
  }, [orgContacts, filters, tunnelId]);

  const includedCount = contactsWithState.filter((c) => !c.isExcluded).length;
  const excludedCount = contactsWithState.filter((c) => c.isExcluded).length;

  if (loading) {
    return (
      <div className="text-sm text-text-muted py-8 text-center">
        Loading organization contacts...
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div>
          <h3 className="text-sm font-semibold">Organization Contacts</h3>
          <p className="text-xs text-text-muted">
            {includedCount} included, {excludedCount} excluded of {orgContacts.length} total
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Button variant="outline" size="sm" onClick={selectAll} disabled={saving}>
            Include All
          </Button>
          <Button variant="outline" size="sm" onClick={deselectAll} disabled={saving}>
            Exclude All
          </Button>
          {dirty && (
            <Button
              size="sm"
              className="bg-gold text-white hover:bg-gold/90"
              onClick={handleSave}
              disabled={saving}
            >
              {saving ? 'Saving...' : 'Save Filters'}
            </Button>
          )}
        </div>
      </div>

      <Input
        placeholder="Search by name, email, or company..."
        value={search}
        onChange={(e) => setSearch(e.target.value)}
      />

      {/* Company bulk toggles */}
      {companies.length > 1 && (
        <div className="flex flex-wrap gap-2">
          {companies.map(([name, stats]) => (
            <button
              key={name}
              type="button"
              onClick={() => toggleCompany(name, stats.excluded < stats.total)}
              className={`inline-flex items-center gap-1.5 rounded-full px-3 py-1 text-xs border transition-colors cursor-pointer ${
                stats.excluded === stats.total
                  ? 'bg-red-50 border-red-200 text-red-700'
                  : stats.excluded > 0
                    ? 'bg-yellow-50 border-yellow-200 text-yellow-700'
                    : 'bg-green-50 border-green-200 text-green-700'
              }`}
            >
              {name}
              <span className="font-medium">
                {stats.total - stats.excluded}/{stats.total}
              </span>
            </button>
          ))}
        </div>
      )}

      {/* Contact list */}
      <div className="border rounded-lg divide-y max-h-[400px] overflow-y-auto">
        {filtered.length === 0 ? (
          <div className="text-sm text-text-muted py-6 text-center">
            {search ? 'No contacts match your search.' : 'No organization contacts found.'}
          </div>
        ) : (
          filtered.map((contact) => (
            <label
              key={contact.id}
              className={`flex items-center gap-3 px-4 py-2.5 cursor-pointer hover:bg-muted/50 transition-colors ${
                contact.isExcluded ? 'opacity-50' : ''
              }`}
            >
              <input
                type="checkbox"
                checked={!contact.isExcluded}
                onChange={() => toggleContact(contact.id)}
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
              {contact.businessPhone && (
                <span className="text-xs text-text-muted hidden sm:block">
                  {contact.businessPhone}
                </span>
              )}
            </label>
          ))
        )}
      </div>
    </div>
  );
}
