'use client';

import React, { useState } from 'react';
import { toast } from 'sonner';
import { PageHeader } from '@/components/PageHeader';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { api } from '@/lib/api';
import { Trash2, Search, X, Plus } from 'lucide-react';
import type { UserSearchResult } from '@/types/tunnel';
import { CleanupProgressModal } from '@/components/CleanupProgressModal';

interface FolderDto {
  id: string;
  name: string;
}

interface UserFoldersDto {
  email: string;
  displayName: string | null;
  entraId: string;
  folders: FolderDto[];
}

interface SelectedFolder {
  entraId: string;
  email: string;
  folderId: string;
  folderName: string;
}

const ALLOWED_FOLDER_NAMES_STORAGE_KEY = 'afhsync.cleanup.allowedFolderNames';
const DEFAULT_ALLOWED_FOLDER_NAMES: string[] = [
  'Avalon Gate Code',
  'Blue Ridge',
  'Buckhead',
  'Clayton',
  'Cobb',
  'Intown',
  'North Atlanta',
  'Services',
  'Holiday Cards 2012',
  'Sync Contacts',
  'Export2',
];

export default function CleanupPage() {
  const [scope, setScope] = useState<'all' | 'specific'>('specific');
  const [selectedEmails, setSelectedEmails] = useState<string[]>([]);
  const [scanning, setScanning] = useState(false);
  const [scanResults, setScanResults] = useState<UserFoldersDto[] | null>(null);
  const [selectedFolders, setSelectedFolders] = useState<SelectedFolder[]>([]);
  const [deleting, setDeleting] = useState(false);
  // quick-260417-48z: tracks the in-flight Hangfire job so the modal can poll
  // /api/cleanup/jobs/{jobId}. Set on POST 202; cleared in modal onClose.
  const [activeJob, setActiveJob] = useState<{
    jobId: string;
    total: number;
    folders: SelectedFolder[];
  } | null>(null);

  // Allowed folder name allowlist — only folders matching these names are returned by scan.
  // Persisted to localStorage so the admin doesn't re-type each session.
  const [allowedFolderNames, setAllowedFolderNames] = useState<string[]>([]);
  const [folderNameInput, setFolderNameInput] = useState('');
  const [bulkPasteOpen, setBulkPasteOpen] = useState(false);
  const [bulkPasteText, setBulkPasteText] = useState('');
  const allowedFolderNamesLoadedRef = React.useRef(false);

  // User search state
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<UserSearchResult[]>([]);
  const [searching, setSearching] = useState(false);
  const debounceRef = React.useRef<ReturnType<typeof setTimeout>>(undefined);

  // Load allowed folder names from localStorage on mount; pre-seed with defaults if empty.
  React.useEffect(() => {
    if (typeof window === 'undefined') return;
    try {
      const raw = window.localStorage.getItem(ALLOWED_FOLDER_NAMES_STORAGE_KEY);
      if (raw) {
        const parsed = JSON.parse(raw);
        if (Array.isArray(parsed)) {
          setAllowedFolderNames(parsed.filter((n) => typeof n === 'string'));
          allowedFolderNamesLoadedRef.current = true;
          return;
        }
      }
    } catch {
      // fall through to default seed
    }
    setAllowedFolderNames(DEFAULT_ALLOWED_FOLDER_NAMES);
    allowedFolderNamesLoadedRef.current = true;
  }, []);

  // Persist allowed folder names whenever they change (after initial load).
  React.useEffect(() => {
    if (!allowedFolderNamesLoadedRef.current) return;
    if (typeof window === 'undefined') return;
    try {
      window.localStorage.setItem(
        ALLOWED_FOLDER_NAMES_STORAGE_KEY,
        JSON.stringify(allowedFolderNames),
      );
    } catch {
      // ignore quota/serialization errors — non-critical
    }
  }, [allowedFolderNames]);

  const addAllowedFolderName = (rawName: string) => {
    const name = rawName.trim();
    if (!name) return;
    setAllowedFolderNames((prev) => {
      if (prev.some((n) => n.toLowerCase() === name.toLowerCase())) return prev;
      return [...prev, name];
    });
  };

  const removeAllowedFolderName = (name: string) => {
    setAllowedFolderNames((prev) => prev.filter((n) => n !== name));
  };

  const handleFolderNameKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      addAllowedFolderName(folderNameInput);
      setFolderNameInput('');
    }
  };

  const handleBulkPasteApply = () => {
    const lines = bulkPasteText
      .split(/\r?\n/)
      .map((l) => l.trim())
      .filter((l) => l.length > 0);
    if (lines.length === 0) {
      setBulkPasteOpen(false);
      setBulkPasteText('');
      return;
    }
    setAllowedFolderNames((prev) => {
      const seen = new Set(prev.map((n) => n.toLowerCase()));
      const additions: string[] = [];
      for (const line of lines) {
        const key = line.toLowerCase();
        if (!seen.has(key)) {
          additions.push(line);
          seen.add(key);
        }
      }
      return [...prev, ...additions];
    });
    setBulkPasteText('');
    setBulkPasteOpen(false);
  };

  React.useEffect(() => {
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

  const handleScan = async () => {
    setScanning(true);
    setScanResults(null);
    setSelectedFolders([]);
    try {
      const data = await api.cleanup.scan(
        scope === 'all' ? null : selectedEmails,
        allowedFolderNames.length > 0 ? allowedFolderNames : null,
      );
      setScanResults(data);
      if (data.length === 0) toast.info('No contact folders found for selected users.');
    } catch {
      toast.error('Failed to scan folders.');
    } finally {
      setScanning(false);
    }
  };

  const handleDelete = async () => {
    if (selectedFolders.length === 0) return;
    if (!confirm(`Delete ${selectedFolders.length} folder(s)? This removes all contacts in those folders from users' mailboxes. This cannot be undone.`)) return;

    // POST returns 202 in milliseconds — the actual Graph deletion runs in the
    // worker via Hangfire. The modal polls progress; scanResults is pruned only
    // when the user dismisses the modal after a terminal status.
    setDeleting(true);
    try {
      const data = await api.cleanup.delete(selectedFolders);
      setActiveJob({
        jobId: data.jobId,
        total: data.total,
        folders: [...selectedFolders],
      });
    } catch {
      toast.error('Failed to enqueue cleanup job.');
      setDeleting(false);
    }
  };

  const toggleFolder = (user: UserFoldersDto, folder: FolderDto) => {
    setSelectedFolders((prev) => {
      const exists = prev.some((f) => f.entraId === user.entraId && f.folderId === folder.id);
      if (exists) {
        return prev.filter((f) => !(f.entraId === user.entraId && f.folderId === folder.id));
      }
      return [...prev, { entraId: user.entraId, email: user.email, folderId: folder.id, folderName: folder.name }];
    });
  };

  const selectAllForUser = (user: UserFoldersDto) => {
    setSelectedFolders((prev) => {
      const withoutUser = prev.filter((f) => f.entraId !== user.entraId);
      const allUserFolders = user.folders.map((f) => ({
        entraId: user.entraId,
        email: user.email,
        folderId: f.id,
        folderName: f.name,
      }));
      return [...withoutUser, ...allUserFolders];
    });
  };

  const deselectAllForUser = (user: UserFoldersDto) => {
    setSelectedFolders((prev) => prev.filter((f) => f.entraId !== user.entraId));
  };

  const selectAllMatching = (folderName: string) => {
    if (!scanResults) return;
    const toAdd: SelectedFolder[] = [];
    for (const user of scanResults) {
      for (const folder of user.folders) {
        if (folder.name === folderName) {
          if (!selectedFolders.some((f) => f.entraId === user.entraId && f.folderId === folder.id)) {
            toAdd.push({ entraId: user.entraId, email: user.email, folderId: folder.id, folderName: folder.name });
          }
        }
      }
    }
    setSelectedFolders((prev) => [...prev, ...toAdd]);
  };

  // Get unique folder names across all users for bulk selection
  const allFolderNames = scanResults
    ? [...new Set(scanResults.flatMap((u) => u.folders.map((f) => f.name)))].sort()
    : [];

  return (
    <div className="p-6 max-w-5xl mx-auto">
      <PageHeader title="Cleanup" description="Remove old CiraSync contact folders from user mailboxes." />

      {/* Step 1: Select Users */}
      <Card className="mt-6">
        <CardHeader>
          <CardTitle>1. Select Users</CardTitle>
        </CardHeader>
        <CardContent>
          <div className="space-y-3">
            <div className="flex items-center gap-4">
              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="radio"
                  checked={scope === 'all'}
                  onChange={() => setScope('all')}
                  className="accent-gold"
                />
                <span className="text-sm">All Users</span>
              </label>
              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="radio"
                  checked={scope === 'specific'}
                  onChange={() => setScope('specific')}
                  className="accent-gold"
                />
                <span className="text-sm">Specific Users</span>
              </label>
            </div>

            {scope === 'specific' && (
              <div className="space-y-2">
                {selectedEmails.length > 0 && (
                  <div className="flex flex-wrap gap-1">
                    {selectedEmails.map((email) => (
                      <span key={email} className="inline-flex items-center gap-1 rounded-full bg-gold/10 px-2.5 py-0.5 text-xs text-gold">
                        {email}
                        <button type="button" onClick={() => setSelectedEmails((prev) => prev.filter((e) => e !== email))} className="hover:text-gold/70 cursor-pointer">
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
                />
                {searching && <p className="text-xs text-text-muted">Searching...</p>}
                {results.length > 0 && (
                  <div className="border rounded-lg divide-y max-h-[200px] overflow-y-auto">
                    {results
                      .filter((u) => !selectedEmails.some((e) => e.toLowerCase() === u.email.toLowerCase()))
                      .map((user) => (
                        <button
                          key={user.id}
                          type="button"
                          className="flex items-center gap-3 px-3 py-2 w-full text-left hover:bg-muted/50 transition-colors cursor-pointer"
                          onClick={() => {
                            setSelectedEmails((prev) => [...prev, user.email]);
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
            )}

          </div>
        </CardContent>
      </Card>

      {/* Step 2: Folder names allowlist */}
      <Card className="mt-4">
        <CardHeader>
          <CardTitle>2. Folder names to wipe</CardTitle>
        </CardHeader>
        <CardContent>
          <div className="space-y-3">
            {allowedFolderNames.length > 0 && (
              <div className="flex flex-wrap gap-1">
                {allowedFolderNames.map((name) => (
                  <span
                    key={name}
                    className="inline-flex items-center gap-1 rounded-full bg-gold/10 px-2.5 py-0.5 text-xs text-gold"
                  >
                    {name}
                    <button
                      type="button"
                      onClick={() => removeAllowedFolderName(name)}
                      className="hover:text-gold/70 cursor-pointer"
                      aria-label={`Remove ${name}`}
                    >
                      <X className="h-3 w-3" />
                    </button>
                  </span>
                ))}
              </div>
            )}
            <div className="flex items-center gap-2">
              <Input
                placeholder="Type a folder name and press Enter..."
                value={folderNameInput}
                onChange={(e) => setFolderNameInput(e.target.value)}
                onKeyDown={handleFolderNameKeyDown}
              />
              <Button
                type="button"
                variant="outline"
                size="sm"
                onClick={() => setBulkPasteOpen((v) => !v)}
              >
                {bulkPasteOpen ? 'Close paste' : 'Bulk paste'}
              </Button>
            </div>
            {bulkPasteOpen && (
              <div className="space-y-2">
                <textarea
                  className="w-full min-h-[100px] rounded-md border border-input bg-transparent px-3 py-2 text-sm shadow-sm placeholder:text-text-muted focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                  placeholder="Paste one folder name per line..."
                  value={bulkPasteText}
                  onChange={(e) => setBulkPasteText(e.target.value)}
                />
                <div className="flex items-center gap-2">
                  <Button
                    type="button"
                    size="sm"
                    className="bg-gold text-white hover:bg-gold/90"
                    onClick={handleBulkPasteApply}
                  >
                    Add names
                  </Button>
                  <Button
                    type="button"
                    size="sm"
                    variant="ghost"
                    onClick={() => {
                      setBulkPasteText('');
                      setBulkPasteOpen(false);
                    }}
                  >
                    Cancel
                  </Button>
                </div>
              </div>
            )}
            <p className="text-xs text-text-muted">
              Only folders matching these names will be returned. Personal folders are never shown.
            </p>

            <Button
              className="bg-gold text-white hover:bg-gold/90"
              onClick={handleScan}
              disabled={scanning || (scope === 'specific' && selectedEmails.length === 0)}
            >
              <Search className="mr-2 h-4 w-4" />
              {scanning ? 'Scanning...' : 'Scan Folders'}
            </Button>
          </div>
        </CardContent>
      </Card>

      {/* Step 3: Results */}
      {scanning && (
        <Card className="mt-4">
          <CardContent className="py-6">
            <div className="space-y-3">
              {[1, 2, 3].map((i) => <Skeleton key={i} className="h-12 w-full" />)}
            </div>
          </CardContent>
        </Card>
      )}

      {scanResults && scanResults.length > 0 && (
        <Card className="mt-4">
          <CardHeader>
            <div className="flex items-center justify-between">
              <CardTitle>3. Select Folders to Delete</CardTitle>
              <div className="flex items-center gap-2">
                <span className="text-sm text-text-muted">{selectedFolders.length} selected</span>
                <Button
                  variant="destructive"
                  size="sm"
                  onClick={handleDelete}
                  disabled={deleting || selectedFolders.length === 0}
                >
                  <Trash2 className="mr-2 h-4 w-4" />
                  {deleting ? 'Deleting...' : `Delete ${selectedFolders.length} folder(s)`}
                </Button>
              </div>
            </div>
          </CardHeader>
          <CardContent>
            {/* Bulk select by folder name */}
            <div className="mb-4">
              <p className="text-xs font-medium text-text-muted uppercase tracking-wide mb-2">Quick select by folder name (across all users)</p>
              <div className="flex flex-wrap gap-1">
                {allFolderNames.map((name) => (
                  <button
                    key={name}
                    type="button"
                    onClick={() => selectAllMatching(name)}
                    className="text-xs px-2 py-1 rounded border hover:bg-gold/10 hover:border-gold cursor-pointer transition-colors"
                  >
                    {name}
                  </button>
                ))}
              </div>
            </div>

            <div className="space-y-4">
              {scanResults.map((user) => {
                const userSelectedCount = selectedFolders.filter((f) => f.entraId === user.entraId).length;
                const allSelected = userSelectedCount === user.folders.length;
                return (
                  <div key={user.entraId} className="border rounded-lg p-3">
                    <div className="flex items-center justify-between mb-2">
                      <div>
                        <p className="text-sm font-medium">{user.displayName || user.email}</p>
                        <p className="text-xs text-text-muted">{user.email} — {user.folders.length} folder(s)</p>
                      </div>
                      <button
                        type="button"
                        onClick={() => allSelected ? deselectAllForUser(user) : selectAllForUser(user)}
                        className="text-xs text-navy underline cursor-pointer"
                      >
                        {allSelected ? 'Deselect all' : 'Select all'}
                      </button>
                    </div>
                    <div className="grid grid-cols-2 md:grid-cols-3 gap-1">
                      {user.folders.map((folder) => {
                        const isSelected = selectedFolders.some(
                          (f) => f.entraId === user.entraId && f.folderId === folder.id
                        );
                        return (
                          <label
                            key={folder.id}
                            className={`flex items-center gap-2 px-2 py-1.5 rounded cursor-pointer text-sm transition-colors ${
                              isSelected ? 'bg-red-50 text-red-700' : 'hover:bg-muted/50'
                            }`}
                          >
                            <input
                              type="checkbox"
                              checked={isSelected}
                              onChange={() => toggleFolder(user, folder)}
                              className="rounded"
                            />
                            {folder.name}
                          </label>
                        );
                      })}
                    </div>
                  </div>
                );
              })}
            </div>
          </CardContent>
        </Card>
      )}

      {scanResults && scanResults.length === 0 && (
        <Card className="mt-4">
          <CardContent className="py-8 text-center">
            <p className="text-text-muted">No contact folders found for the selected users.</p>
          </CardContent>
        </Card>
      )}

      {activeJob && (
        <CleanupProgressModal
          jobId={activeJob.jobId}
          total={activeJob.total}
          selectedFolders={activeJob.folders}
          onClose={(deletedKeys) => {
            // Terminal close: the modal hands back the set of attempted folder
            // keys so we can prune them from the visible scan results — matches
            // the legacy optimistic-removal UX. Empty set = "Hide" while running,
            // leaves results untouched.
            if (deletedKeys.size > 0) {
              setScanResults((prev) =>
                prev?.map((u) => ({
                  ...u,
                  folders: u.folders.filter(
                    (f) => !deletedKeys.has(`${u.entraId}|${f.id}`)
                  ),
                })).filter((u) => u.folders.length > 0) ?? null
              );
              setSelectedFolders([]);
            }
            setActiveJob(null);
            setDeleting(false);
          }}
        />
      )}
    </div>
  );
}
