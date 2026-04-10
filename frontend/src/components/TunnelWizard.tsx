'use client';

import { useState, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import { toast } from 'sonner';
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
} from '@/components/ui/dialog';
import { Button } from '@/components/ui/button';
import { Separator } from '@/components/ui/separator';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { WizardStepper } from '@/components/WizardStepper';
import { StepName } from '@/components/wizard/StepName';
import { StepSource } from '@/components/wizard/StepSource';
import type { SourceEntry } from '@/components/wizard/StepSource';
import { StepTargets } from '@/components/wizard/StepTargets';
import { StepReview } from '@/components/wizard/StepReview';
import { useCreateTunnel } from '@/hooks/use-tunnels';
import { usePhoneLists } from '@/hooks/use-phone-lists';
import type { CreateTunnelRequest } from '@/types/tunnel';

interface TunnelWizardProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

interface FormData {
  name: string;
  sources: SourceEntry[];
  targetListIds: number[];
  targetUserEmails: string | null;
}

const initialFormData: FormData = {
  name: '',
  sources: [],
  targetListIds: [],
  targetUserEmails: null,
};

export function TunnelWizard({ open, onOpenChange }: TunnelWizardProps) {
  const router = useRouter();
  const createTunnel = useCreateTunnel();
  const { data: phoneLists } = usePhoneLists();

  const [step, setStep] = useState(0);
  const [formData, setFormData] = useState<FormData>(initialFormData);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [discardDialogOpen, setDiscardDialogOpen] = useState(false);

  const hasData =
    formData.name.trim() !== '' ||
    formData.sources.length > 0 ||
    formData.targetListIds.length > 0;

  const resetForm = useCallback(() => {
    setStep(0);
    setFormData(initialFormData);
    setErrors({});
  }, []);

  const validateStep = useCallback(
    (stepIndex: number): boolean => {
      const newErrors: Record<string, string> = {};

      switch (stepIndex) {
        case 0:
          if (!formData.name.trim()) {
            newErrors.name = 'Tunnel name is required.';
          } else if (formData.name.trim().length < 3) {
            newErrors.name = 'Tunnel name must be at least 3 characters.';
          } else if (formData.name.trim().length > 100) {
            newErrors.name = 'Tunnel name must be at most 100 characters.';
          }
          break;
        case 1:
          if (formData.sources.length === 0) {
            newErrors.source = 'Add at least one source to continue.';
          }
          break;
        case 2:
          if (formData.targetListIds.length === 0) {
            newErrors.targets = 'Select at least one phone list above.';
          }
          if (formData.targetUserEmails !== null) {
            const emails: string[] = JSON.parse(formData.targetUserEmails || '[]');
            if (emails.length === 0) {
              newErrors.targets = 'Select at least one user, or switch scope to All Users.';
            }
          }
          break;
      }

      setErrors(newErrors);
      return Object.keys(newErrors).length === 0;
    },
    [formData],
  );

  const handleNext = useCallback(() => {
    if (validateStep(step)) {
      setStep((s) => s + 1);
    }
  }, [step, validateStep]);

  const handleBack = useCallback(() => {
    setStep((s) => Math.max(0, s - 1));
  }, []);

  const goToStep = useCallback(
    (target: number) => {
      if (target < step) {
        setStep(target);
      }
    },
    [step],
  );

  const handleAddSource = useCallback((source: SourceEntry) => {
    setFormData((prev) => ({ ...prev, sources: [...prev.sources, source] }));
    setErrors((prev) => ({ ...prev, source: '' }));
  }, []);

  const handleRemoveSource = useCallback((index: number) => {
    setFormData((prev) => ({
      ...prev,
      sources: prev.sources.filter((_, i) => i !== index),
    }));
  }, []);

  const handleSubmit = useCallback(() => {
    if (formData.sources.length === 0) return;

    const sources: CreateTunnelRequest['sources'] = formData.sources.map((s) => {
      if (s.type === 'ddg' && s.ddg) {
        return {
          sourceType: 'ddg',
          sourceIdentifier: s.ddg.graphFilter ?? s.ddg.recipientFilter,
          sourceDisplayName: s.ddg.displayName,
          sourceSmtpAddress: s.ddg.primarySmtpAddress,
          sourceFilterPlain: s.ddg.recipientFilterPlain,
        };
      } else if (s.type === 'mailbox_contacts' && s.mailboxEmail) {
        return {
          sourceType: 'mailbox_contacts',
          sourceIdentifier: s.mailboxEmail,
          sourceDisplayName: s.contactFolderName ? `${s.mailboxEmail} / ${s.contactFolderName}` : s.mailboxEmail,
          sourceSmtpAddress: s.mailboxEmail,
          sourceFilterPlain: null,
          contactFolderId: s.contactFolderId ?? null,
          contactFolderName: s.contactFolderName ?? null,
        };
      } else {
        return {
          sourceType: 'org_contacts',
          sourceIdentifier: 'all',
          sourceDisplayName: 'Organization Contacts',
          sourceSmtpAddress: null,
          sourceFilterPlain: null,
        };
      }
    });

    const request: CreateTunnelRequest = {
      name: formData.name.trim(),
      sources,
      targetListIds: formData.targetListIds,
      fieldProfileId: null,
      stalePolicy: 'auto_remove',
      staleDays: 14,
      targetGroupId: null,
      targetGroupName: null,
      targetUserEmails: formData.targetUserEmails,
    };

    createTunnel.mutate(request, {
      onSuccess: (data) => {
        toast.success('Tunnel created successfully.');
        resetForm();
        onOpenChange(false);
        router.push(`/tunnels/${data.id}`);
      },
      onError: () => {
        toast.error('Failed to create tunnel.');
      },
    });
  }, [formData, createTunnel, resetForm, onOpenChange, router]);

  const handleOpenChange = (isOpen: boolean) => {
    if (!isOpen && hasData) {
      setDiscardDialogOpen(true);
    } else {
      if (!isOpen) resetForm();
      onOpenChange(isOpen);
    }
  };

  const handleToggleTarget = useCallback((listId: number) => {
    setFormData((prev) => ({
      ...prev,
      targetListIds: prev.targetListIds.includes(listId)
        ? prev.targetListIds.filter((id) => id !== listId)
        : [...prev.targetListIds, listId],
    }));
  }, []);

  const handleSelectAll = useCallback(() => {
    if (phoneLists) {
      setFormData((prev) => ({
        ...prev,
        targetListIds: phoneLists.map((l) => l.id),
      }));
    }
  }, [phoneLists]);

  const handleDeselectAll = useCallback(() => {
    setFormData((prev) => ({ ...prev, targetListIds: [] }));
  }, []);

  const steps = ['Name', 'Source', 'Targets', 'Review'];

  return (
    <>
      <Dialog open={open} onOpenChange={handleOpenChange}>
        <DialogContent className="max-w-lg max-h-[85vh] overflow-y-auto">
          <DialogHeader>
            <DialogTitle className="sr-only">Create Tunnel</DialogTitle>
            <DialogDescription className="sr-only">
              Step {step + 1} of {steps.length}: {steps[step]}
            </DialogDescription>
          </DialogHeader>

          <WizardStepper currentStep={step} onStepClick={goToStep} />
          <Separator className="my-2" />

          <div className="min-h-[300px]">
            {step === 0 && (
              <StepName
                name={formData.name}
                onChange={(name) => {
                  setFormData((prev) => ({ ...prev, name }));
                  if (errors.name)
                    setErrors((prev) => ({ ...prev, name: '' }));
                }}
                error={errors.name || null}
              />
            )}
            {step === 1 && (
              <StepSource
                sources={formData.sources}
                onAddSource={handleAddSource}
                onRemoveSource={handleRemoveSource}
                error={errors.source || null}
              />
            )}
            {step === 2 && (
              <StepTargets
                selectedIds={formData.targetListIds}
                onToggle={handleToggleTarget}
                onSelectAll={handleSelectAll}
                onDeselectAll={handleDeselectAll}
                error={errors.targets || null}
                targetUserEmails={formData.targetUserEmails}
                onTargetUserEmailsChange={(val) =>
                  setFormData((prev) => ({ ...prev, targetUserEmails: val }))
                }
              />
            )}
            {step === 3 && (
              <StepReview
                name={formData.name}
                sources={formData.sources}
                targetListIds={formData.targetListIds}
                onEdit={goToStep}
              />
            )}
          </div>

          <div className="flex justify-between mt-4">
            <Button
              variant="outline"
              onClick={step === 0 ? () => handleOpenChange(false) : handleBack}
            >
              {step === 0 ? 'Cancel' : 'Back'}
            </Button>
            {step < steps.length - 1 ? (
              <Button
                className="bg-gold text-white hover:bg-gold/90"
                onClick={handleNext}
              >
                Next
              </Button>
            ) : (
              <Button
                className="bg-gold text-white hover:bg-gold/90"
                onClick={handleSubmit}
                disabled={createTunnel.isPending}
              >
                {createTunnel.isPending ? 'Creating...' : 'Create Tunnel'}
              </Button>
            )}
          </div>
        </DialogContent>
      </Dialog>

      <ConfirmDialog
        open={discardDialogOpen}
        onOpenChange={setDiscardDialogOpen}
        title="Discard changes?"
        description="You have unsaved progress. Closing will discard all entered data."
        confirmLabel="Discard"
        dismissLabel="Cancel"
        variant="destructive"
        onConfirm={() => {
          setDiscardDialogOpen(false);
          resetForm();
          onOpenChange(false);
        }}
      />
    </>
  );
}
