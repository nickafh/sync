'use client';

import { useState, useEffect, useCallback, useRef } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import {
  useFieldProfiles,
  useFieldProfile,
  useUpdateFieldProfile,
} from '@/hooks/use-field-profiles';
import { toast } from 'sonner';
import { PageHeader } from '@/components/PageHeader';
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from '@/components/ui/card';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { Check } from 'lucide-react';
import { ContactCardPreview } from '@/components/ContactCardPreview';
import type { FieldProfileDetailDto } from '@/types/field-profile';

const behaviorOptions = [
  { value: 'always', label: 'Always' },
  { value: 'add_missing', label: 'Add if Missing' },
  { value: 'nosync', label: 'Do Not Sync' },
  { value: 'remove_blank', label: 'Remove if Blank' },
];

export default function FieldProfilesPage() {
  const queryClient = useQueryClient();
  const { data: profiles, isLoading: profilesLoading } = useFieldProfiles();
  const [selectedProfileId, setSelectedProfileId] = useState<number | null>(
    null,
  );
  const { data: profile, isLoading: profileLoading } = useFieldProfile(
    selectedProfileId ?? 0,
  );
  const updateProfile = useUpdateFieldProfile();
  const [savedField, setSavedField] = useState<string | null>(null);
  const savedTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Auto-select default profile when data loads
  useEffect(() => {
    if (profiles && profiles.length > 0 && selectedProfileId === null) {
      const defaultProfile = profiles.find((p) => p.isDefault);
      setSelectedProfileId(defaultProfile?.id ?? profiles[0].id);
    }
  }, [profiles, selectedProfileId]);

  const handleBehaviorChange = useCallback(
    (fieldName: string, newValue: string) => {
      if (!selectedProfileId || !profile) return;

      // Optimistic update
      const previousProfile = queryClient.getQueryData<FieldProfileDetailDto>([
        'field-profile',
        selectedProfileId,
      ]);

      queryClient.setQueryData<FieldProfileDetailDto>(
        ['field-profile', selectedProfileId],
        (old) => {
          if (!old) return old;
          return {
            ...old,
            sections: old.sections.map((s) => ({
              ...s,
              fields: s.fields.map((f) =>
                f.fieldName === fieldName ? { ...f, behavior: newValue } : f,
              ),
            })),
          };
        },
      );

      updateProfile.mutate(
        {
          id: selectedProfileId,
          data: { fields: [{ fieldName, behavior: newValue }] },
        },
        {
          onSuccess: () => {
            toast.success('Field profile updated.');
            setSavedField(fieldName);
            if (savedTimerRef.current) clearTimeout(savedTimerRef.current);
            savedTimerRef.current = setTimeout(
              () => setSavedField(null),
              2000,
            );
          },
          onError: () => {
            // Revert optimistic update
            queryClient.setQueryData(
              ['field-profile', selectedProfileId],
              previousProfile,
            );
            toast.error('Something went wrong. Please try again.');
          },
        },
      );
    },
    [selectedProfileId, profile, queryClient, updateProfile],
  );

  const isLoading = profilesLoading || profileLoading;

  return (
    <div>
      <PageHeader
        title="Field Profiles"
        description="Configure which contact fields sync and how."
      />

      {/* Profile selector */}
      {profiles && profiles.length > 1 && (
        <div className="mb-6">
          <Select
            value={selectedProfileId}
            onValueChange={(val) => setSelectedProfileId(val as number)}
          >
            <SelectTrigger className="w-[250px]">
              <SelectValue placeholder="Select a profile" />
            </SelectTrigger>
            <SelectContent>
              {profiles.map((p) => (
                <SelectItem key={p.id} value={p.id}>
                  {p.name}
                  {p.isDefault && (
                    <span className="ml-2 text-xs text-text-muted">
                      (Default)
                    </span>
                  )}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
      )}

      {/* Loading state */}
      {isLoading && (
        <div className="flex flex-col lg:flex-row gap-8">
          <div className="flex-1 lg:w-3/5 space-y-6">
            {Array.from({ length: 4 }).map((_, i) => (
              <Skeleton key={i} className="h-40 w-full rounded-xl" />
            ))}
          </div>
          <div className="w-full lg:w-2/5">
            <Skeleton className="w-full h-[500px] rounded-xl" />
          </div>
        </div>
      )}

      {/* Field sections + live preview split layout */}
      {!isLoading && profile && profile.sections.length > 0 && (
        <div className="flex flex-col lg:flex-row gap-8">
          {/* Left panel: field sections (~60%) */}
          <div className="flex-1 lg:w-3/5 space-y-6">
            {profile.sections.map((section) => (
              <Card key={section.name}>
                <CardHeader>
                  <CardTitle className="text-lg font-bold font-heading text-navy">
                    {section.name}
                  </CardTitle>
                </CardHeader>
                <CardContent>
                  <div className="divide-y divide-border">
                    {section.fields.map((field) => (
                      <div
                        key={field.fieldName}
                        className="flex items-center justify-between py-3"
                      >
                        <span className="text-sm font-body">
                          {field.displayName}
                        </span>
                        <div className="flex items-center gap-2">
                          <Select
                            value={field.behavior}
                            onValueChange={(val) =>
                              handleBehaviorChange(
                                field.fieldName,
                                val as string,
                              )
                            }
                          >
                            <SelectTrigger size="sm" className="w-[160px]">
                              <SelectValue />
                            </SelectTrigger>
                            <SelectContent>
                              {behaviorOptions.map((opt) => (
                                <SelectItem key={opt.value} value={opt.value}>
                                  {opt.label}
                                </SelectItem>
                              ))}
                            </SelectContent>
                          </Select>
                          {savedField === field.fieldName && (
                            <div className="flex items-center gap-1">
                              <Check className="w-4 h-4 text-emerald-500" />
                              <span className="text-xs text-emerald-600">
                                Saved
                              </span>
                            </div>
                          )}
                        </div>
                      </div>
                    ))}
                  </div>
                </CardContent>
              </Card>
            ))}
          </div>

          {/* Right panel: live preview (~40%) */}
          <div className="w-full lg:w-2/5">
            <ContactCardPreview profile={profile ?? null} />
          </div>
        </div>
      )}

      {/* Empty state when profile has no sections */}
      {!isLoading && profile && profile.sections.length === 0 && (
        <p className="text-sm text-text-muted text-center py-8">
          No field settings configured for this profile.
        </p>
      )}
    </div>
  );
}
