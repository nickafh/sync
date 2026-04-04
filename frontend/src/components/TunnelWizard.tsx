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
  sourceDdg: DdgDto | null;
  targetListIds: number[];
}

const initialFormData: FormData = {
  name: '',
  sourceDdg: null,
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
    formData.sourceDdg !== null ||
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
          if (!formData.sourceDdg) {
            newErrors.source = 'Select a source DDG to continue.';
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

  const handleSubmit = useCallback(() => {
    if (!formData.sourceDdg) return;

    const request: CreateTunnelRequest = {
      name: formData.name.trim(),
      sourceType: 'ddg',
      sourceIdentifier:
        formData.sourceDdg.graphFilter ?? formData.sourceDdg.recipientFilter,
      sourceDisplayName: formData.sourceDdg.displayName,
      sourceSmtpAddress: formData.sourceDdg.primarySmtpAddress,
      sourceFilterPlain: formData.sourceDdg.recipientFilterPlain,
      targetScope: 'all_users',
      targetListIds: formData.targetListIds,
      fieldProfileId: null,
      stalePolicy: 'auto_remove',
      staleDays: 14,
    };

    createTunnel.mutate(request, {
      onSuccess: (data) => {
        toast.success('Tunnel created successfully.');
        resetForm();
        onOpenChange(false);
        router.push(`/tunnels/${data.id}`);
      },
      onError: () => {
        toast.error('Failed to create tunnel. Please try again.');
      },
    });
  }, [formData, createTunnel, resetForm, onOpenChange, router]);

  const handleOpenChange = useCallback(
    (isOpen: boolean) => {
      if (!isOpen && hasData) {
        setDiscardDialogOpen(true);
      } else {
        if (!isOpen) resetForm();
        onOpenChange(isOpen);
      }
    },
    [hasData, resetForm, onOpenChange],
  );

  const handleDiscard = useCallback(() => {
    setDiscardDialogOpen(false);
    resetForm();
    onOpenChange(false);
  }, [resetForm, onOpenChange]);

  const handleToggleTarget = useCallback((id: number) => {
    setFormData((prev) => ({
      ...prev,
      targetListIds: prev.targetListIds.includes(id)
        ? prev.targetListIds.filter((tid) => tid !== id)
        : [...prev.targetListIds, id],
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
    setFormData((prev) => ({
      ...prev,
      targetListIds: [],
    }));
  }, []);

  return (
    <>
      <Dialog open={open} onOpenChange={handleOpenChange}>
        <DialogContent className="max-w-3xl min-h-[600px] flex flex-col p-0 gap-0">
          <DialogHeader className="sr-only">
            <DialogTitle>Create Tunnel</DialogTitle>
            <DialogDescription>
              Create a new sync tunnel in 4 steps.
            </DialogDescription>
          </DialogHeader>

          <WizardStepper currentStep={step} onStepClick={goToStep} />

          <Separator />

          <div className="flex-1 overflow-y-auto p-6">
            {step === 0 && (
              <StepName
                name={formData.name}
                onChange={(name) => {
                  setFormData((prev) => ({ ...prev, name }));
                  if (errors.name) setErrors((prev) => ({ ...prev, name: '' }));
                }}
                error={errors.name || null}
              />
            )}
            {step === 1 && (
              <StepSource
                selectedDdg={formData.sourceDdg}
                onSelect={(ddg) => {
                  setFormData((prev) => ({ ...prev, sourceDdg: ddg }));
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
                ddg={formData.sourceDdg}
                targetListIds={formData.targetListIds}
                onEdit={goToStep}
              />
            )}
          </div>

          <Separator />

          <div className="flex items-center justify-between px-6 py-4">
            <div>
              {step > 0 && (
                <Button variant="outline" onClick={handleBack}>
                  Back
                </Button>
              )}
            </div>
            <div>
              {step < 3 ? (
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
          </div>
        </DialogContent>
      </Dialog>

      <ConfirmDialog
        open={discardDialogOpen}
        onOpenChange={setDiscardDialogOpen}
        title="Discard tunnel?"
        description="You have unsaved progress. Closing will discard all entered data."
        confirmLabel="Discard Tunnel"
        dismissLabel="Keep Editing"
        variant="destructive"
        onConfirm={handleDiscard}
      />
    </>
  );
}
