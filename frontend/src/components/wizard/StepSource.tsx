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

type AddMode = null | 'ddg' | 'mailbox_contacts' | 'org_contacts';

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
          onSelect={(email, displayName, folderId, folderName) => {
            if (sources.some((s) => s.type === 'mailbox_contacts' && s.mailboxEmail === email && s.contactFolderId === folderId)) return;
            onAddSource({
              type: 'mailbox_contacts',
              mailboxEmail: email,
              contactFolderId: folderId,
              contactFolderName: folderName,
              label: displayName || email,
              sublabel: folderName ? `${email} / ${folderName}` : (displayName ? email : undefined),
            });
            setAddMode(null);
            setMailboxInput('');
          }}
          onCancel={() => { setAddMode(null); setMailboxInput(''); }}
        />
      )}

      {error && (
        <p className="text-destructive text-xs">{error}</p>
      )}
    </div>
  );
}

function MailboxAutocomplete({
  onSelect,
  onCancel,
}: {
  onSelect: (email: string, displayName?: string, folderId?: string, folderName?: string) => void;
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
    try {
      const data = await api.graph.contactFolders(email);
      setFolders(data);
    } catch {
      setFolders([]);
    } finally {
      setLoadingFolders(false);
    }
  };

  if (step === 'folder') {
    return (
      <div className="space-y-3">
        <div className="flex items-center justify-between">
          <p className="text-sm font-medium">Select Contact Folder</p>
          <Button variant="outline" size="sm" onClick={() => { setStep('email'); setQuery(''); }}>
            Back
          </Button>
        </div>
        <p className="text-xs text-text-muted">{selectedEmail}</p>
        {loadingFolders ? (
          <p className="text-xs text-text-muted">Loading folders...</p>
        ) : (
          <div className="border rounded-lg divide-y max-h-[250px] overflow-y-auto">
            <button
              type="button"
              className="flex items-center gap-3 px-3 py-2.5 w-full text-left hover:bg-muted/50 transition-colors cursor-pointer font-medium"
              onClick={() => onSelect(selectedEmail, selectedDisplayName)}
            >
              <Mail className="h-4 w-4 text-text-muted shrink-0" />
              <span className="text-sm">All Contacts (root folder)</span>
            </button>
            {folders.map((folder) => (
              <button
                key={folder.id}
                type="button"
                className="flex items-center gap-3 px-3 py-2.5 w-full text-left hover:bg-muted/50 transition-colors cursor-pointer"
                onClick={() => onSelect(selectedEmail, selectedDisplayName, folder.id, folder.name)}
              >
                <Cable className="h-4 w-4 text-text-muted shrink-0" />
                <span className="text-sm">{folder.name}</span>
              </button>
            ))}
          </div>
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
