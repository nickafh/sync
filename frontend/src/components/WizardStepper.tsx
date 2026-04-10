'use client';

import { Fragment } from 'react';
import { Check } from 'lucide-react';
import { cn } from '@/lib/utils';

interface WizardStepperProps {
  currentStep: number;
  onStepClick: (step: number) => void;
}

const steps = ['Name', 'Source', 'Targets', 'Review'];

export function WizardStepper({ currentStep, onStepClick }: WizardStepperProps) {
  return (
    <div className="flex items-center w-full px-8 py-4">
      {steps.map((label, index) => (
        <Fragment key={label}>
          <div className="flex flex-col items-center gap-1.5">
            <button
              type="button"
              onClick={() => {
                if (index < currentStep) onStepClick(index);
              }}
              disabled={index > currentStep}
              className={cn(
                'w-8 h-8 rounded-full text-sm font-bold flex items-center justify-center shrink-0 transition-colors',
                index < currentStep &&
                  'bg-gold text-white cursor-pointer',
                index === currentStep &&
                  'bg-gold text-white',
                index > currentStep &&
                  'bg-muted text-text-muted cursor-default opacity-50',
              )}
            >
              {index < currentStep ? (
                <Check className="size-4" />
              ) : (
                index + 1
              )}
            </button>
            <span
              className={cn(
                'text-sm',
                index <= currentStep
                  ? 'font-bold text-navy'
                  : 'text-text-muted',
              )}
            >
              {label}
            </span>
          </div>
          {index < steps.length - 1 && (
            <div
              className={cn(
                'flex-1 h-0.5 mx-2 self-start mt-4',
                index < currentStep ? 'bg-gold' : 'bg-muted',
              )}
            />
          )}
        </Fragment>
      ))}
    </div>
  );
}
