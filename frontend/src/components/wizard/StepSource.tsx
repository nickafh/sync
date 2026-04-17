'use client';

import { useState, useEffect, useRef } from 'react';
import { DDGSearchList } from '@/components/DDGSearchList';
import { Input } from '@/components/ui/input';
import { Button } from '@/components/ui/button';
import { api } from '@/lib/api';
import type { DdgDto } from '@/types/ddg';
import type { UserSearchResult } from '@/types/tunnel';
import { X, Plus, Cable, Mail, Building2 } from 'lucide-react';

export interface SourceEntry {
  type: 'ddg' | 'mailbox_contacts' | 'org_contacts';
  ddg?: DdgDto;
  mailboxEmail?: string;
  contactFolderId?: string;
  contactFolderName?: string;
  label: string;
  sublabel?: string;
}

interface StepSourceProps {
  sources: SourceEntry[];
  onAddSource: (source: SourceEntry) => void;
  onRemoveSource: (index: number) => void;
  error: string | null;
}

type AddMode = null | 'ddg' | 'mailbox_contacts' | 'org_contacts' | 'specific_users';

export function StepSource({
  sources,
  onAddSource,
  onRemoveSource,
  error,
}: StepSourceProps) {
  const [addMode, setAddMode] = useState<AddMode>(null);
  const [mailboxInput, setMailboxInput] = useState('');

  const hasOrgContacts = sources.some((s) => s.type === 'org_contacts');

  const handleSelectDdg = (ddg: DdgDto) => {
    // Don't add duplicates
    if (sources.some((s) => s.type === 'ddg' && s.ddg?.id === ddg.id)) return;
    onAddSource({
      type: 'ddg',
      ddg,
      label: ddg.displayName,
      sublabel: `${ddg.memberCount} members`,
    });
  };

  const handleAddMailbox = () => {
    const email = mailboxInput.trim();
    if (!email) return;
    if (sources.some((s) => s.type === 'mailbox_contacts' && s.mailboxEmail === email)) return;
    onAddSource({
      type: 'mailbox_contacts',
      mailboxEmail: email,
      label: email,
      sublabel: 'Shared Mailbox',
    });
    setMailboxInput('');
    setAddMode(null);
  };

  const handleAddOrgContacts = () => {
    if (hasOrgContacts) return;
    onAddSource({
      type: 'org_contacts',
      label: 'Organization Contacts',
      sublabel: 'All tenant external contacts',
    });
    setAddMode(null);
  };

  const sourceIcon = (type: string) => {
    switch (type) {
      case 'ddg': return <Cable className="h-4 w-4 text-text-muted" />;
      case 'mailbox_contacts': return <Mail className="h-4 w-4 text-text-muted" />;
      case 'org_contacts': return <Building2 className="h-4 w-4 text-text-muted" />;
      default: return null;
    }
  };

  return (
    <div className="space-y-4">
      <p className="text-sm text-text-muted">
        Add one or more contact sources for this tunnel.
      </p>

      {/* Added sources list */}
      {sources.length > 0 && (
        <div className="space-y-2">
          {sources.map((source, i) => (
            <div
              key={`${source.type}-${source.ddg?.id ?? source.mailboxEmail ?? 'org'}-${i}`}
              className="flex items-center gap-3 p-3 bg-muted/30 rounded-lg border"
            >
              {sourceIcon(source.type)}
              <div className="flex-1 min-w-0">
                <p className="text-sm font-medium truncate">{source.label}</p>
                {source.sublabel && (
                  <p className="text-xs text-text-muted">{source.sublabel}</p>
                )}
              </div>
              <button
                type="button"
                onClick={() => onRemoveSource(i)}
                className="text-text-muted hover:text-destructive transition-colors cursor-pointer"
              >
                <X className="h-4 w-4" />
              </button>
            </div>
          ))}
        </div>
      )}

      {/* Add source picker */}
      {addMode === null && (
        <div className="space-y-2">
          <button
            type="button"
            onClick={() => setAddMode('ddg')}
            className="w-full flex items-center gap-3 p-3 rounded-lg border border-dashed hover:border-gold hover:bg-gold/5 transition-colors cursor-pointer text-left"
          >
            <Cable className="h-4 w-4 text-text-muted" />
            <div>
              <p className="text-sm font-medium">Add DDG</p>
              <p className="text-xs text-text-muted">Dynamic Distribution Group — internal employee contacts</p>
            </div>
          </button>
          <button
            type="button"
            onClick={() => setAddMode('mailbox_contacts')}
            className="w-full flex items-center gap-3 p-3 rounded-lg border border-dashed hover:border-gold hover:bg-gold/5 transition-colors cursor-pointer text-left"
          >
            <Mail className="h-4 w-4 text-text-muted" />
            <div>
              <p className="text-sm font-medium">Add Shared Mailbox</p>
              <p className="text-xs text-text-muted">Contacts from a mailbox&apos;s Contacts folder</p>
            </div>
          </button>
          <button
            type="button"
            onClick={() => setAddMode('specific_users')}
            className="w-full flex items-center gap-3 p-3 rounded-lg border border-dashed hover:border-gold hover:bg-gold/5 transition-colors cursor-pointer text-left"
          >
            <Plus className="h-4 w-4 text-text-muted" />
            <div>
              <p className="text-sm font-medium">Add Specific Users</p>
              <p className="text-xs text-text-muted">Pick individual users to sync as contacts</p>
            </div>
          </button>
          {!hasOrgContacts && (
            <button
              type="button"
              onClick={handleAddOrgContacts}
              className="w-full flex items-center gap-3 p-3 rounded-lg border border-dashed hover:border-gold hover:bg-gold/5 transition-colors cursor-pointer text-left"
            >
              <Building2 className="h-4 w-4 text-text-muted" />
              <div>
                <p className="text-sm font-medium">Add Organization Contacts</p>
                <p className="text-xs text-text-muted">Tenant external contacts from Exchange Admin Center</p>
              </div>
            </button>
          )}
        </div>
      )}

      {/* DDG picker */}
      {addMode === 'ddg' && (
        <div className="space-y-3">
          <div className="flex items-center justify-between">
            <p className="text-sm font-medium">Select DDGs</p>
            <Button
              variant="outline"
              size="sm"
              onClick={() => setAddMode(null)}
            >
              Done
            </Button>
          </div>
          <DDGSearchList
            onSelect={handleSelectDdg}
            selectedIds={sources.filter((s) => s.type === 'ddg').map((s) => s.ddg!.id)}
          />
        </div>
      )}

      {/* Mailbox email input with autocomplete */}
      {addMode === 'mailbox_contacts' && (
        <MailboxAutocomplete
          onSelectMany={(entries) => {
            for (const entry of entries) {
              if (sources.some((s) =>
                s.type === 'mailbox_contacts' &&
                s.mailboxEmail === entry.mailboxEmail &&
                s.contactFolderId === entry.contactFolderId
              )) continue;
              onAddSource(entry);
            }
            setAddMode(null);
            setMailboxInput('');
          }}
          onCancel={() => { setAddMode(null); setMailboxInput(''); }}
        />
      )}

      {/* Specific Users picker */}
      {addMode === 'specific_users' && (
        <SpecificUsersPicker
          onDone={(users) => {
            if (users.length === 0) { setAddMode(null); return; }
            const emails = users.map((u) => u.email);
            const filter = emails.map((e) => `mail eq '${e}'`).join(' or ');
            const names = users.map((u) => u.displayName || u.email).join(', ');
            onAddSource({
              type: 'ddg',
              label: names.length > 60 ? `${names.substring(0, 57)}...` : names,
              sublabel: `${users.length} specific user(s)`,
              ddg: {
                id: `specific-${Date.now()}`,
                displayName: names.length > 60 ? `${names.substring(0, 57)}...` : names,
                primarySmtpAddress: '',
                recipientFilter: filter,
                recipientFilterPlain: `Specific users: ${emails.join(', ')}`,
                graphFilter: filter,
                graphFilterSuccess: true,
                graphFilterWarning: null,
                memberCount: users.length,
                type: 'Specific',
              },
            });
            setAddMode(null);
          }}
          onCancel={() => setAddMode(null)}
        />
      )}

      {error && (
        <p className="text-destructive text-xs">{error}</p>
      )}
    </div>
  );
}

function SpecificUsersPicker({
  onDone,
  onCancel,
}: {
  onDone: (users: { email: string; displayName?: string }[]) => void;
  onCancel: () => void;
}) {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<UserSearchResult[]>([]);
  const [searching, setSearching] = useState(false);
  const [selected, setSelected] = useState<{ email: string; displayName?: string }[]>([]);
  const debounceRef = useRef<ReturnType<typeof setTimeout>>(undefined);

  useEffect(() => {
    if (query.length < 2) {
      setResults([]);
      return;
    }
    clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(async () => {
      setSearching(true);
      try {
        const data = await api.users.search(query);
        setResults(data);
      } catch {
        setResults([]);
      } finally {
        setSearching(false);
      }
    }, 300);
    return () => clearTimeout(debounceRef.current);
  }, [query]);

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between">
        <p className="text-sm font-medium">Add Specific Users</p>
        <div className="flex gap-2">
          <Button variant="outline" size="sm" onClick={onCancel}>Cancel</Button>
          <Button
            size="sm"
            className="bg-gold text-white hover:bg-gold/90"
            onClick={() => onDone(selected)}
            disabled={selected.length === 0}
          >
            Add {selected.length} user(s)
          </Button>
        </div>
      </div>

      {selected.length > 0 && (
        <div className="flex flex-wrap gap-1">
          {selected.map((u) => (
            <span
              key={u.email}
              className="inline-flex items-center gap-1 rounded-full bg-gold/10 px-2.5 py-0.5 text-xs text-gold"
            >
              {u.displayName || u.email}
              <button
                type="button"
                onClick={() => setSelected((prev) => prev.filter((s) => s.email !== u.email))}
                className="hover:text-gold/70 cursor-pointer"
              >
                <X className="h-3 w-3" />
              </button>
            </span>
          ))}
        </div>
      )}

      <Input
        placeholder="Search by name or email..."
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        autoFocus
      />
      {searching && <p className="text-xs text-text-muted">Searching...</p>}
      {results.length > 0 && (
        <div className="border rounded-lg divide-y max-h-[200px] overflow-y-auto">
          {results
            .filter((u) => !selected.some((s) => s.email.toLowerCase() === u.email.toLowerCase()))
            .map((user) => (
              <button
                key={user.id}
                type="button"
                className="flex items-center gap-3 px-3 py-2 w-full text-left hover:bg-muted/50 transition-colors cursor-pointer"
                onClick={() => {
                  setSelected((prev) => [...prev, { email: user.email, displayName: user.displayName }]);
                  setQuery('');
                  setResults([]);
                }}
              >
                <Plus className="h-4 w-4 text-text-muted shrink-0" />
                <div className="min-w-0">
                  <p className="text-sm font-medium truncate">{user.displayName}</p>
                  <p className="text-xs text-text-muted truncate">{user.email}</p>
                </div>
              </button>
            ))}
        </div>
      )}
    </div>
  );
}

const ROOT_FOLDER_KEY = '__root__';

function MailboxAutocomplete({
  onSelectMany,
  onCancel,
}: {
  onSelectMany: (entries: SourceEntry[]) => void;
  onCancel: () => void;
}) {
  const [step, setStep] = useState<'email' | 'folder'>('email');
  const [selectedEmail, setSelectedEmail] = useState('');
  const [selectedDisplayName, setSelectedDisplayName] = useState<string | undefined>();
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<UserSearchResult[]>([]);
  const [searching, setSearching] = useState(false);
  const [folders, setFolders] = useState<{ id: string; name: string }[]>([]);
  const [loadingFolders, setLoadingFolders] = useState(false);
  const [checked, setChecked] = useState<Set<string>>(new Set());
  const [managedNames, setManagedNames] = useState<Set<string>>(new Set());
  const debounceRef = useRef<ReturnType<typeof setTimeout>>(undefined);

  useEffect(() => {
    if (step !== 'email' || query.length < 2) {
      setResults([]);
      return;
    }
    clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(async () => {
      setSearching(true);
      try {
        const data = await api.users.search(query);
        setResults(data);
      } catch {
        setResults([]);
      } finally {
        setSearching(false);
      }
    }, 300);
    return () => clearTimeout(debounceRef.current);
  }, [query, step]);

  const handlePickEmail = async (email: string, displayName?: string) => {
    setSelectedEmail(email);
    setSelectedDisplayName(displayName);
    setLoadingFolders(true);
    setStep('folder');
    setChecked(new Set());
    // Fetch folders + AFH-Sync-managed phone-list names in parallel.
    try {
      const [folderData, phoneLists] = await Promise.all([
        api.graph.contactFolders(email),
        api.phoneLists.list().catch(() => []),
      ]);
      setFolders(folderData);
      setManagedNames(new Set(phoneLists.map((p) => p.name.trim().toLowerCase())));
    } catch {
      setFolders([]);
    } finally {
      setLoadingFolders(false);
    }
  };

  const toggleChecked = (key: string) => {
    setChecked((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  };

  const handleSubmit = () => {
    const entries: SourceEntry[] = [];
    for (const key of checked) {
      if (key === ROOT_FOLDER_KEY) {
        entries.push({
          type: 'mailbox_contacts',
          mailboxEmail: selectedEmail,
          label: selectedDisplayName || selectedEmail,
          sublabel: selectedDisplayName ? `${selectedEmail} / All Contacts` : 'All Contacts',
        });
      } else {
        const folder = folders.find((f) => f.id === key);
        if (!folder) continue;
        entries.push({
          type: 'mailbox_contacts',
          mailboxEmail: selectedEmail,
          contactFolderId: folder.id,
          contactFolderName: folder.name,
          label: selectedDisplayName || selectedEmail,
          sublabel: `${selectedEmail} / ${folder.name}`,
        });
      }
    }
    onSelectMany(entries);
  };

  if (step === 'folder') {
    return (
      <div className="space-y-3">
        <div className="flex items-center justify-between">
          <p className="text-sm font-medium">Select Contact Folders</p>
          <Button variant="outline" size="sm" onClick={() => { setStep('email'); setQuery(''); setChecked(new Set()); }}>
            Back
          </Button>
        </div>
        <p className="text-xs text-text-muted">{selectedEmail}</p>
        {loadingFolders ? (
          <p className="text-xs text-text-muted">Loading folders...</p>
        ) : (
          <>
            <div className="border rounded-lg divide-y max-h-[250px] overflow-y-auto">
              <label
                className="flex items-center gap-3 px-3 py-2.5 w-full text-left hover:bg-muted/50 transition-colors cursor-pointer font-medium"
              >
                <input
                  type="checkbox"
                  checked={checked.has(ROOT_FOLDER_KEY)}
                  onChange={() => toggleChecked(ROOT_FOLDER_KEY)}
                  className="rounded"
                />
                <Mail className="h-4 w-4 text-text-muted shrink-0" />
                <span className="text-sm">All Contacts (root folder)</span>
              </label>
              {folders.map((folder) => {
                const isManaged = managedNames.has(folder.name.trim().toLowerCase());
                return (
                  <label
                    key={folder.id}
                    className="flex items-center gap-3 px-3 py-2.5 w-full text-left hover:bg-muted/50 transition-colors cursor-pointer"
                    title={isManaged ? 'This folder is managed by an AFH Sync phone list. Selecting it may cause feedback loops.' : undefined}
                  >
                    <input
                      type="checkbox"
                      checked={checked.has(folder.id)}
                      onChange={() => toggleChecked(folder.id)}
                      className="rounded"
                    />
                    <Cable className="h-4 w-4 text-text-muted shrink-0" />
                    <span className="text-sm">
                      {folder.name}
                      {isManaged && (
                        <span className="ml-2 text-xs text-text-muted">(synced by AFH Sync)</span>
                      )}
                    </span>
                  </label>
                );
              })}
            </div>
            <Button
              size="sm"
              className="w-full bg-gold text-white hover:bg-gold/90"
              onClick={handleSubmit}
              disabled={checked.size === 0}
            >
              Add selected ({checked.size})
            </Button>
          </>
        )}
      </div>
    );
  }

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between">
        <p className="text-sm font-medium">Shared Mailbox Email</p>
        <Button variant="outline" size="sm" onClick={onCancel}>
          Cancel
        </Button>
      </div>
      <Input
        placeholder="Search by name or email..."
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        autoFocus
      />
      {searching && (
        <p className="text-xs text-text-muted">Searching...</p>
      )}
      {results.length > 0 && (
        <div className="border rounded-lg divide-y max-h-[250px] overflow-y-auto">
          {results.map((user) => (
            <button
              key={user.id}
              type="button"
              className="flex items-center gap-3 px-3 py-2 w-full text-left hover:bg-muted/50 transition-colors cursor-pointer"
              onClick={() => handlePickEmail(user.email, user.displayName)}
            >
              <Mail className="h-4 w-4 text-text-muted shrink-0" />
              <div className="min-w-0">
                <p className="text-sm font-medium truncate">{user.displayName}</p>
                <p className="text-xs text-text-muted truncate">{user.email}</p>
              </div>
            </button>
          ))}
        </div>
      )}
      {query.length >= 2 && !searching && results.length === 0 && (
        <p className="text-xs text-text-muted">No users found. You can also type a full email and press Enter.</p>
      )}
      {query.includes('@') && (
        <Button
          size="sm"
          variant="outline"
          onClick={() => handlePickEmail(query.trim())}
          className="w-full"
        >
          Use &quot;{query.trim()}&quot;
        </Button>
      )}
    </div>
  );
}
