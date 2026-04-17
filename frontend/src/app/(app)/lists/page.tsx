'use client';

import React, { useState, useEffect, useRef } from 'react';
import { usePhoneLists, usePhoneListContacts, useCreatePhoneList, useUpdatePhoneList, useDeletePhoneList } from '@/hooks/use-phone-lists';
import { PageHeader } from '@/components/PageHeader';
import { EmptyState } from '@/components/EmptyState';
import { IPhoneFrame } from '@/components/IPhoneFrame';
import { ContactList } from '@/components/ContactList';
import { ContactCard } from '@/components/ContactCard';
import type { ContactCardData } from '@/components/ContactCard';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { toast } from 'sonner';
import { Phone, Plus, Pencil, Trash2, X } from 'lucide-react';
import { api } from '@/lib/api';
import type { UserSearchResult } from '@/types/tunnel';
import type { ContactDto, PhoneListDto, TargetUserFilterDdg, TargetUserFilterShape } from '@/types/phone-list';
import { DDGSearchList } from '@/components/DDGSearchList';

function mapContactDtoToCardData(dto: ContactDto): ContactCardData {
  return {
    displayName: dto.displayName,
    email: dto.email,
    jobTitle: dto.jobTitle,
    department: dto.department,
    office: dto.office,
    phone: dto.phone,
    mobilePhone: dto.mobilePhone,
    companyName: dto.companyName,
    streetAddress: dto.streetAddress,
    city: dto.city,
    state: dto.state,
    postalCode: dto.postalCode,
    country: dto.country,
  };
}

export default function PhoneListsPage() {
  const [selectedListId, setSelectedListId] = useState<number | null>(null);
  const [selectedContact, setSelectedContact] = useState<ContactDto | null>(null);
  const [contactPage, setContactPage] = useState(0);

  // Create form state
  const [showCreate, setShowCreate] = useState(false);
  const [newName, setNewName] = useState('');
  const [newDescription, setNewDescription] = useState('');
  const [newScope, setNewScope] = useState('all_users');
  const [newEmails, setNewEmails] = useState<string[]>([]);
  // quick-260417-2lb: DDGs picked here are persisted as {id, displayName} and resolved
  // live every sync run by the worker — distinct from the EmailSearchPicker "Groups" tab,
  // which expands a DDG into its current member emails at pick time (snapshot, not live).
  const [newDdgs, setNewDdgs] = useState<TargetUserFilterDdg[]>([]);

  // Edit form state
  const [editingId, setEditingId] = useState<number | null>(null);
  const [editName, setEditName] = useState('');
  const [editDescription, setEditDescription] = useState('');
  const [editScope, setEditScope] = useState('all_users');
  const [editEmails, setEditEmails] = useState<string[]>([]);
  const [editDdgs, setEditDdgs] = useState<TargetUserFilterDdg[]>([]);

  // Delete state
  const [deleteTarget, setDeleteTarget] = useState<{ id: number; name: string } | null>(null);

  const { data: phoneLists, isLoading } = usePhoneLists();
  const createPhoneList = useCreatePhoneList();
  const updatePhoneList = useUpdatePhoneList();
  const deletePhoneList = useDeletePhoneList();

  const { data: contacts, isLoading: contactsLoading } =
    usePhoneListContacts(selectedListId ?? 0, contactPage + 1, 200);

  useEffect(() => {
    if (phoneLists && phoneLists.length > 0 && selectedListId === null) {
      setSelectedListId(phoneLists[0].id);
    }
  }, [phoneLists, selectedListId]);

  useEffect(() => {
    setSelectedContact(null);
    setContactPage(0);
  }, [selectedListId]);

  const selectedList = phoneLists?.find((l) => l.id === selectedListId);

  // quick-260417-2lb: build the targetUserFilter JSON from emails + ddgs, omitting
  // empty arrays so old/back-compat consumers see {emails:[...]} when ddgs is unset.
  const buildTargetUserFilter = (emails: string[], ddgs: TargetUserFilterDdg[]): string | null => {
    const shape: TargetUserFilterShape = {
      ...(emails.length > 0 ? { emails } : {}),
      ...(ddgs.length > 0 ? { ddgs } : {}),
    };
    return JSON.stringify(shape);
  };

  const handleCreate = () => {
    if (!newName.trim()) return;
    createPhoneList.mutate(
      {
        name: newName.trim(),
        description: newDescription.trim() || null,
        targetScope: newScope,
        targetUserFilter: newScope === 'specific_users'
          ? buildTargetUserFilter(newEmails, newDdgs)
          : null,
      },
      {
        onSuccess: () => {
          toast.success('Target created.');
          setShowCreate(false);
          setNewName('');
          setNewDescription('');
          setNewScope('all_users');
          setNewEmails([]);
          setNewDdgs([]);
        },
        onError: () => toast.error('Failed to create target.'),
      },
    );
  };

  const startEdit = (list: PhoneListDto) => {
    setEditingId(list.id);
    setEditName(list.name);
    setEditDescription('');
    setEditScope(list.targetScope);
    // Load existing emails + ddgs from targetUserFilter JSON.
    // Back-compat: rows containing only {emails:[...]} (no ddgs key) load with editDdgs=[].
    try {
      const parsed = (list.targetUserFilter
        ? JSON.parse(list.targetUserFilter)
        : null) as TargetUserFilterShape | null;
      setEditEmails(parsed?.emails ?? []);
      setEditDdgs(parsed?.ddgs ?? []);
    } catch {
      setEditEmails([]);
      setEditDdgs([]);
    }
  };

  const handleUpdate = () => {
    if (!editingId || !editName.trim()) return;
    updatePhoneList.mutate(
      {
        id: editingId,
        data: {
          name: editName.trim(),
          description: editDescription.trim() || null,
          targetScope: editScope,
          targetUserFilter: editScope === 'specific_users'
            ? buildTargetUserFilter(editEmails, editDdgs)
            : null,
        },
      },
      {
        onSuccess: () => {
          toast.success('Target updated.');
          setEditingId(null);
        },
        onError: () => toast.error('Failed to update target.'),
      },
    );
  };

  const handleDelete = () => {
    if (!deleteTarget) return;
    deletePhoneList.mutate(deleteTarget.id, {
      onSuccess: () => {
        toast.success('Target deleted.');
        setDeleteTarget(null);
        if (selectedListId === deleteTarget.id) setSelectedListId(null);
      },
      onError: () => toast.error('Cannot delete a target used by tunnels.'),
    });
  };

  return (
    <div>
      <PageHeader
        title="Targets"
        description="Manage target lists and preview how contacts appear on phones."
      >
        <Button
          className="bg-gold text-white hover:bg-gold/90"
          onClick={() => setShowCreate(true)}
        >
          <Plus className="size-4 mr-2" />
          New Target
        </Button>
      </PageHeader>

      {/* Create form */}
      {showCreate && (
        <div className="border rounded-lg p-4 mb-6 space-y-3 bg-muted/30">
          <div className="flex items-center gap-3">
            <Input
              placeholder="Target name"
              value={newName}
              onChange={(e) => setNewName(e.target.value)}
              className="flex-1"
              autoFocus
            />
            <Input
              placeholder="Description (optional)"
              value={newDescription}
              onChange={(e) => setNewDescription(e.target.value)}
              className="flex-1"
            />
          </div>
          <div className="space-y-2">
            <p className="text-sm font-medium">Delivery Scope</p>
            <div className="flex items-center gap-4">
              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="radio"
                  name="newScope"
                  value="all_users"
                  checked={newScope === 'all_users'}
                  onChange={() => setNewScope('all_users')}
                  className="accent-gold"
                />
                <span className="text-sm">All Users</span>
              </label>
              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="radio"
                  name="newScope"
                  value="specific_users"
                  checked={newScope === 'specific_users'}
                  onChange={() => setNewScope('specific_users')}
                  className="accent-gold"
                />
                <span className="text-sm">Specific Users</span>
              </label>
            </div>
            {newScope === 'specific_users' && (
              <>
                <EmailSearchPicker
                  selected={newEmails}
                  onChange={setNewEmails}
                />
                <DDGTargetPicker
                  selected={newDdgs}
                  onChange={setNewDdgs}
                />
              </>
            )}
          </div>
          <div className="flex items-center gap-2">
            <Button
              size="sm"
              className="bg-gold text-white hover:bg-gold/90"
              onClick={handleCreate}
              disabled={createPhoneList.isPending || !newName.trim()}
            >
              {createPhoneList.isPending ? 'Creating...' : 'Create'}
            </Button>
            <Button
              size="sm"
              variant="outline"
              onClick={() => { setShowCreate(false); setNewName(''); setNewDescription(''); setNewScope('all_users'); setNewEmails([]); setNewDdgs([]); }}
            >
              Cancel
            </Button>
          </div>
        </div>
      )}

      {isLoading ? (
        <div className="space-y-3">
          {Array.from({ length: 4 }).map((_, i) => (
            <Skeleton key={i} className="h-16 w-full rounded-lg" />
          ))}
        </div>
      ) : !phoneLists || phoneLists.length === 0 ? (
        <EmptyState
          icon={Phone}
          heading="No targets"
          body="Create a target to get started."
        />
      ) : (
        <div className="flex flex-col lg:flex-row gap-8">
          {/* Left panel: Target list */}
          <div className="min-w-[320px] lg:w-[45%] space-y-2">
            {phoneLists.map((list: PhoneListDto) => (
              <div key={list.id}>
                {editingId === list.id ? (
                  /* Edit form inline */
                  <div className="border-l-2 border-gold bg-gold/5 rounded-lg p-4 space-y-3">
                    <Input
                      value={editName}
                      onChange={(e) => setEditName(e.target.value)}
                      autoFocus
                    />
                    <div className="space-y-2">
                      <p className="text-xs font-medium text-text-muted uppercase tracking-wide">Delivery Scope</p>
                      <div className="flex items-center gap-4">
                        <label className="flex items-center gap-2 cursor-pointer">
                          <input
                            type="radio"
                            name={`editScope-${list.id}`}
                            value="all_users"
                            checked={editScope === 'all_users'}
                            onChange={() => setEditScope('all_users')}
                            className="accent-gold"
                          />
                          <span className="text-sm">All Users</span>
                        </label>
                        <label className="flex items-center gap-2 cursor-pointer">
                          <input
                            type="radio"
                            name={`editScope-${list.id}`}
                            value="specific_users"
                            checked={editScope === 'specific_users'}
                            onChange={() => setEditScope('specific_users')}
                            className="accent-gold"
                          />
                          <span className="text-sm">Specific Users</span>
                        </label>
                      </div>
                      {editScope === 'specific_users' && (
                        <>
                          <EmailSearchPicker
                            selected={editEmails}
                            onChange={setEditEmails}
                          />
                          <DDGTargetPicker
                            selected={editDdgs}
                            onChange={setEditDdgs}
                          />
                        </>
                      )}
                    </div>
                    <div className="flex items-center gap-2">
                      <Button
                        size="sm"
                        className="bg-gold text-white hover:bg-gold/90"
                        onClick={handleUpdate}
                        disabled={updatePhoneList.isPending || !editName.trim()}
                      >
                        {updatePhoneList.isPending ? 'Saving...' : 'Save'}
                      </Button>
                      <Button size="sm" variant="outline" onClick={() => setEditingId(null)}>
                        Cancel
                      </Button>
                    </div>
                  </div>
                ) : (
                  /* Normal display */
                  <div
                    className={
                      list.id === selectedListId
                        ? 'border-l-2 border-gold bg-gold/5 rounded-lg p-4 cursor-pointer transition-colors group'
                        : 'border border-border-default hover:border-gray-400 rounded-lg p-4 cursor-pointer transition-colors group'
                    }
                    onClick={() => setSelectedListId(list.id)}
                  >
                    <div className="flex items-center justify-between">
                      <div className="flex items-center gap-2">
                        <span className="text-sm font-bold text-navy">{list.name}</span>
                        {list.targetScope === 'specific_users' && (
                          <span className="text-xs bg-gold/10 text-gold px-2 py-0.5 rounded-full">
                            Specific users
                          </span>
                        )}
                      </div>
                      <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                        <button
                          type="button"
                          onClick={(e) => { e.stopPropagation(); startEdit(list); }}
                          className="p-1 rounded hover:bg-gray-200"
                        >
                          <Pencil className="size-3.5 text-text-muted" />
                        </button>
                        <button
                          type="button"
                          onClick={(e) => { e.stopPropagation(); setDeleteTarget({ id: list.id, name: list.name }); }}
                          className="p-1 rounded hover:bg-red-100"
                        >
                          <Trash2 className="size-3.5 text-destructive" />
                        </button>
                      </div>
                    </div>
                    <div className="text-xs text-text-muted mt-1">
                      {list.contactCount} contacts &middot; {list.userCount} users
                    </div>
                  </div>
                )}
              </div>
            ))}
          </div>

          {/* Right panel: iPhone frame preview */}
          <div className="lg:w-[55%] flex justify-center lg:justify-start">
            <IPhoneFrame title={selectedList?.name ?? ''}>
              <div className="relative h-full">
                <div
                  className={
                    selectedContact !== null
                      ? 'absolute inset-0 transition-transform duration-200 ease-out -translate-x-full'
                      : 'absolute inset-0 transition-transform duration-200 ease-out translate-x-0'
                  }
                >
                  <ContactList
                    contacts={contacts ?? []}
                    onSelectContact={(c) => setSelectedContact(c)}
                    isLoading={contactsLoading}
                  />
                </div>

                <div
                  className={
                    selectedContact !== null
                      ? 'absolute inset-0 transition-transform duration-200 ease-out translate-x-0 bg-white'
                      : 'absolute inset-0 transition-transform duration-200 ease-out translate-x-full bg-white'
                  }
                >
                  {selectedContact && (
                    <ContactCard
                      contact={mapContactDtoToCardData(selectedContact)}
                      onBack={() => setSelectedContact(null)}
                    />
                  )}
                </div>
              </div>
            </IPhoneFrame>
          </div>
        </div>
      )}

      <ConfirmDialog
        open={deleteTarget !== null}
        onOpenChange={(open) => { if (!open) setDeleteTarget(null); }}
        title="Delete target"
        description={
          <>
            Are you sure you want to delete <strong>{deleteTarget?.name}</strong>? This cannot be undone.
          </>
        }
        confirmLabel="Delete Target"
        dismissLabel="Keep Target"
        variant="destructive"
        onConfirm={handleDelete}
        isLoading={deletePhoneList.isPending}
      />
    </div>
  );
}

function EmailSearchPicker({
  selected,
  onChange,
}: {
  selected: string[];
  onChange: (emails: string[]) => void;
}) {
  const [tab, setTab] = useState<'users' | 'groups'>('users');
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<UserSearchResult[]>([]);
  const [searching, setSearching] = useState(false);
  const [ddgs, setDdgs] = useState<{ id: string; displayName: string; memberCount: number }[]>([]);
  const [ddgsLoading, setDdgsLoading] = useState(false);
  const [addingGroup, setAddingGroup] = useState<string | null>(null);
  const debounceRef = React.useRef<ReturnType<typeof setTimeout>>(undefined);

  React.useEffect(() => {
    if (tab !== 'users' || query.length < 2) {
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
  }, [query, tab]);

  React.useEffect(() => {
    if (tab === 'groups' && ddgs.length === 0) {
      setDdgsLoading(true);
      api.ddgs.list()
        .then((data) => setDdgs(data.map((d) => ({ id: d.primarySmtpAddress, displayName: d.displayName, memberCount: d.memberCount }))))
        .catch(() => setDdgs([]))
        .finally(() => setDdgsLoading(false));
    }
  }, [tab, ddgs.length]);

  const handleAddGroup = async (groupEmail: string) => {
    setAddingGroup(groupEmail);
    try {
      const members = await api.ddgs.getMembers(groupEmail, 1, 999);
      const newEmails = members
        .map((m) => m.email)
        .filter((e): e is string => !!e)
        .filter((e) => !selected.some((s) => s.toLowerCase() === e.toLowerCase()));
      if (newEmails.length > 0) {
        onChange([...selected, ...newEmails]);
        toast.success(`Added ${newEmails.length} users from group.`);
      } else {
        toast.info('All members already selected.');
      }
    } catch {
      toast.error('Failed to load group members.');
    } finally {
      setAddingGroup(null);
    }
  };

  const filteredDdgs = query.trim()
    ? ddgs.filter((d) => d.displayName.toLowerCase().includes(query.toLowerCase()))
    : ddgs;

  return (
    <div className="space-y-2">
      {selected.length > 0 && (
        <div className="flex flex-wrap gap-1">
          {selected.map((email) => (
            <span
              key={email}
              className="inline-flex items-center gap-1 rounded-full bg-gold/10 px-2.5 py-0.5 text-xs text-gold"
            >
              {email}
              <button
                type="button"
                onClick={() => onChange(selected.filter((e) => e !== email))}
                className="hover:text-gold/70 cursor-pointer"
              >
                <X className="h-3 w-3" />
              </button>
            </span>
          ))}
        </div>
      )}
      <p className="text-xs text-text-muted">{selected.length} user(s) selected</p>

      <div className="flex gap-2 border-b">
        <button
          type="button"
          onClick={() => setTab('users')}
          className={`px-3 py-1.5 text-sm font-medium cursor-pointer ${tab === 'users' ? 'border-b-2 border-gold text-gold' : 'text-text-muted'}`}
        >
          Users
        </button>
        <button
          type="button"
          onClick={() => setTab('groups')}
          className={`px-3 py-1.5 text-sm font-medium cursor-pointer ${tab === 'groups' ? 'border-b-2 border-gold text-gold' : 'text-text-muted'}`}
        >
          Groups
        </button>
      </div>

      <Input
        placeholder={tab === 'users' ? 'Search by name or email...' : 'Filter groups...'}
        value={query}
        onChange={(e) => setQuery(e.target.value)}
      />

      {tab === 'users' && (
        <>
          {searching && <p className="text-xs text-text-muted">Searching...</p>}
          {results.length > 0 && (
            <div className="border rounded-lg divide-y max-h-[200px] overflow-y-auto">
              {results
                .filter((u) => !selected.some((e) => e.toLowerCase() === u.email.toLowerCase()))
                .map((user) => (
                  <button
                    key={user.id}
                    type="button"
                    className="flex items-center gap-3 px-3 py-2 w-full text-left hover:bg-muted/50 transition-colors cursor-pointer"
                    onClick={() => {
                      onChange([...selected, user.email]);
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
        </>
      )}

      {tab === 'groups' && (
        <>
          {ddgsLoading && <p className="text-xs text-text-muted">Loading groups...</p>}
          {filteredDdgs.length > 0 && (
            <div className="border rounded-lg divide-y max-h-[200px] overflow-y-auto">
              {filteredDdgs.map((ddg) => (
                <button
                  key={ddg.id}
                  type="button"
                  className="flex items-center justify-between px-3 py-2 w-full text-left hover:bg-muted/50 transition-colors cursor-pointer"
                  onClick={() => handleAddGroup(ddg.id)}
                  disabled={addingGroup === ddg.id}
                >
                  <div className="min-w-0">
                    <p className="text-sm font-medium truncate">{ddg.displayName}</p>
                    <p className="text-xs text-text-muted">{ddg.memberCount} members</p>
                  </div>
                  <span className="text-xs text-gold shrink-0">
                    {addingGroup === ddg.id ? 'Adding...' : '+ Add all'}
                  </span>
                </button>
              ))}
            </div>
          )}
        </>
      )}
    </div>
  );
}

/**
 * DDGTargetPicker — quick-260417-2lb
 *
 * Pick one or more Exchange Dynamic Distribution Groups to deliver this phone list to.
 * Persists ONLY {id, displayName} into targetUserFilter.ddgs (no member snapshot — the
 * worker resolves the membership live every sync run, so adding a user to the DDG in
 * Exchange auto-delivers the phone list on the next run with zero admin touch).
 *
 * Distinct from the EmailSearchPicker "Groups" tab: that flow expands a DDG into its
 * current member emails (snapshot, frozen until the admin re-runs it). This picker is
 * for the live-resolve path.
 */
function DDGTargetPicker({
  selected,
  onChange,
}: {
  selected: TargetUserFilterDdg[];
  onChange: (next: TargetUserFilterDdg[]) => void;
}) {
  const [showPicker, setShowPicker] = useState(false);

  const handleAdd = (ddg: { id: string; displayName: string }) => {
    if (selected.some((d) => d.id === ddg.id)) {
      toast.info('Already added.');
      return;
    }
    onChange([...selected, { id: ddg.id, displayName: ddg.displayName }]);
    setShowPicker(false);
  };

  const handleRemove = (id: string) => {
    onChange(selected.filter((d) => d.id !== id));
  };

  return (
    <div className="space-y-2 pt-3 border-t mt-2">
      <p className="text-xs font-medium text-text-muted uppercase tracking-wide">
        Distribution Groups (live)
      </p>
      <p className="text-xs text-text-muted">
        Members are resolved on every sync — add or remove people in Exchange and the change
        flows through automatically. {selected.length} group(s) selected.
      </p>
      {selected.length > 0 && (
        <div className="flex flex-wrap gap-1">
          {selected.map((ddg) => (
            <span
              key={ddg.id}
              className="inline-flex items-center gap-1 rounded-full bg-navy/10 px-2.5 py-0.5 text-xs text-navy"
            >
              {ddg.displayName}
              <button
                type="button"
                onClick={() => handleRemove(ddg.id)}
                className="hover:text-navy/70 cursor-pointer"
                aria-label={`Remove ${ddg.displayName}`}
              >
                <X className="h-3 w-3" />
              </button>
            </span>
          ))}
        </div>
      )}
      {showPicker ? (
        <div className="border rounded-lg p-2">
          <DDGSearchList
            onSelect={(ddg) => handleAdd({ id: ddg.id, displayName: ddg.displayName })}
            selectedIds={selected.map((d) => d.id)}
          />
          <div className="flex justify-end pt-2">
            <Button size="sm" variant="outline" onClick={() => setShowPicker(false)}>
              Done
            </Button>
          </div>
        </div>
      ) : (
        <Button
          type="button"
          size="sm"
          variant="outline"
          onClick={() => setShowPicker(true)}
        >
          <Plus className="size-3.5 mr-1" />
          Add Distribution Group
        </Button>
      )}
    </div>
  );
}
