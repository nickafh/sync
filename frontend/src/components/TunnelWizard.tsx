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
import { StepTargets } from '@/components/wizard/StepTargets';
import { StepReview } from '@/components/wizard/StepReview';
import { useCreateTunnel } from '@/hooks/use-tunnels';
import { usePhoneLists } from '@/hooks/use-phone-lists';
import type { DdgDto } from '@/types/ddg';
import type { CreateTunnelRequest } from '@/types/tunnel';

interface TunnelWizardProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

interface FormData {
  name: string;
  sourceType: 'ddg' | 'mailbox_contacts' | 'org_contacts';
  sourceDdgs: DdgDto[];
  sourceMailboxEmail: string;
  targetListIds: number[];
}

const initialFormData: FormData = {
  name: '',
  sourceType: 'ddg',
  sourceDdgs: [],
  sourceMailboxEmail: '',
  targetListIds: [],
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
    formData.sourceDdgs.length > 0 ||
    formData.sourceMailboxEmail.trim() !== '' ||
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
          if (formData.sourceType === 'ddg' && formData.sourceDdgs.length === 0) {
            newErrors.source = 'Select at least one DDG to continue.';
          } else if (formData.sourceType === 'mailbox_contacts' && !formData.sourceMailboxEmail.trim()) {
            newErrors.source = 'Enter a mailbox email address to continue.';
          }
          break;
        case 2:
          if (formData.targetListIds.length === 0) {
            newErrors.targets = 'Select at least one target list.';
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

  const handleToggleDdg = useCallback((ddg: DdgDto) => {
    setFormData((prev) => {
      const exists = prev.sourceDdgs.find((d) => d.id === ddg.id);
      return {
        ...prev,
        sourceDdgs: exists
          ? prev.sourceDdgs.filter((d) => d.id !== ddg.id)
          : [...prev.sourceDdgs, ddg],
      };
    });
    setErrors((prev) => ({ ...prev, source: '' }));
  }, []);

  const handleSubmit = useCallback(() => {
    const sources: CreateTunnelRequest['sources'] = [];

    if (formData.sourceType === 'ddg') {
      if (formData.sourceDdgs.length === 0) return;
      for (const ddg of formData.sourceDdgs) {
        sources.push({
          sourceType: 'ddg',
          sourceIdentifier: ddg.graphFilter ?? ddg.recipientFilter,
          sourceDisplayName: ddg.displayName,
          sourceSmtpAddress: ddg.primarySmtpAddress,
          sourceFilterPlain: ddg.recipientFilterPlain,
        });
      }
    } else if (formData.sourceType === 'mailbox_contacts') {
      const email = formData.sourceMailboxEmail.trim();
      if (!email) return;
      sources.push({
        sourceType: 'mailbox_contacts',
        sourceIdentifier: email,
        sourceDisplayName: email,
        sourceSmtpAddress: email,
        sourceFilterPlain: null,
      });
    } else if (formData.sourceType === 'org_contacts') {
      sources.push({
        sourceType: 'org_contacts',
        sourceIdentifier: 'all',
        sourceDisplayName: 'Organization Contacts',
        sourceSmtpAddress: null,
        sourceFilterPlain: null,
      });
    }

    const request: CreateTunnelRequest = {
      name: formData.name.trim(),
      sources,
      targetListIds: formData.targetListIds,
      fieldProfileId: null,
      stalePolicy: 'auto_remove',
      staleDays: 14,
      targetGroupId: null,
      targetGroupName: null,
      targetUserEmails: null,
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
                sourceType={formData.sourceType}
                onSourceTypeChange={(type) =>
                  setFormData((prev) => ({ ...prev, sourceType: type }))
                }
                selectedDdgs={formData.sourceDdgs}
                onToggleDdg={handleToggleDdg}
                mailboxEmail={formData.sourceMailboxEmail}
                onMailboxEmailChange={(email) => {
                  setFormData((prev) => ({
                    ...prev,
                    sourceMailboxEmail: email,
                  }));
                  if (errors.source)
                    setErrors((prev) => ({ ...prev, source: '' }));
                }}
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
              />
            )}
            {step === 3 && (
              <StepReview
                name={formData.name}
                sourceType={formData.sourceType}
                ddgs={formData.sourceDdgs}
                mailboxEmail={formData.sourceMailboxEmail}
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
