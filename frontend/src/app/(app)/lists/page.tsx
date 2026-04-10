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
import type { ContactDto, PhoneListDto } from '@/types/phone-list';

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

  // Edit form state
  const [editingId, setEditingId] = useState<number | null>(null);
  const [editName, setEditName] = useState('');
  const [editDescription, setEditDescription] = useState('');
  const [editScope, setEditScope] = useState('all_users');
  const [editEmails, setEditEmails] = useState<string[]>([]);

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

  const handleCreate = () => {
    if (!newName.trim()) return;
    createPhoneList.mutate(
      {
        name: newName.trim(),
        description: newDescription.trim() || null,
        targetScope: newScope,
        targetUserFilter: newScope === 'specific_users'
          ? JSON.stringify({ emails: newEmails })
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
    // Load existing emails from targetUserFilter JSON
    try {
      const parsed = list.targetUserFilter ? JSON.parse(list.targetUserFilter) : null;
      setEditEmails(parsed?.emails ?? []);
    } catch {
      setEditEmails([]);
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
            ? JSON.stringify({ emails: editEmails })
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
              <EmailSearchPicker
                selected={newEmails}
                onChange={setNewEmails}
              />
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
              onClick={() => { setShowCreate(false); setNewName(''); setNewDescription(''); setNewScope('all_users'); setNewEmails([]); }}
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
                        <EmailSearchPicker
                          selected={editEmails}
                          onChange={setEditEmails}
                        />
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
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<UserSearchResult[]>([]);
  const [searching, setSearching] = useState(false);
  const debounceRef = React.useRef<ReturnType<typeof setTimeout>>(undefined);

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
      <Input
        placeholder="Search by name or email..."
        value={query}
        onChange={(e) => setQuery(e.target.value)}
      />
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
    </div>
  );
}
