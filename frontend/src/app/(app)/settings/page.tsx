'use client';

import { useState, useEffect } from 'react';
import { useSettings, useUpdateSettings } from '@/hooks/use-settings';
import { toast } from 'sonner';
import { api } from '@/lib/api';
import { PageHeader } from '@/components/PageHeader';
import { SettingsCard } from '@/components/SettingsCard';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Button } from '@/components/ui/button';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { Switch } from '@/components/ui/switch';
import { Separator } from '@/components/ui/separator';
import { Skeleton } from '@/components/ui/skeleton';

const scheduleOptions = [
  { label: 'Every 2 hours', value: '0 */2 * * *' },
  { label: 'Every 4 hours', value: '0 */4 * * *' },
  { label: 'Every 6 hours', value: '0 */6 * * *' },
  { label: 'Every 8 hours', value: '0 */8 * * *' },
  { label: 'Every 12 hours', value: '0 */12 * * *' },
  { label: 'Daily at midnight', value: '0 0 * * *' },
  { label: 'Daily at 6 AM', value: '0 6 * * *' },
];

const photoSyncOptions = [
  {
    value: 'included',
    label: 'Sync with contacts',
    description: 'Photos are synced as part of the regular contact sync. Each run processes both contact data and photos together.',
  },
  {
    value: 'separate_pass',
    label: 'Separate photo pass',
    description: 'Photos are synced in a separate pass after contact data. Reduces sync time when only contact fields changed.',
  },
  {
    value: 'disabled',
    label: 'Photos off',
    description: 'Contact photos are not synced. Contacts will show initials only on phones.',
  },
];

const photoScheduleOptions = [
  { label: 'Every 6 hours', value: '0 */6 * * *' },
  { label: 'Every 12 hours', value: '0 */12 * * *' },
  { label: 'Daily at midnight', value: '0 0 * * *' },
  { label: 'Daily at 3 AM', value: '0 3 * * *' },
  { label: 'Daily at 6 AM', value: '0 6 * * *' },
];

function isKnownSchedule(cron: string): boolean {
  return scheduleOptions.some((opt) => opt.value === cron);
}

function isKnownPhotoSchedule(cron: string): boolean {
  return photoScheduleOptions.some((opt) => opt.value === cron);
}

export default function SettingsPage() {
  const { data: settings, isLoading } = useSettings();
  const updateSettings = useUpdateSettings();

  // Sync Schedule (SETT-01)
  const [cronExpression, setCronExpression] = useState('');
  const [savingSchedule, setSavingSchedule] = useState(false);

  // Photo Sync (SETT-02)
  const [photoSyncMode, setPhotoSyncMode] = useState('');
  const [photoCron, setPhotoCron] = useState('0 */6 * * *');
  const [photoAutoTrigger, setPhotoAutoTrigger] = useState(false);
  const [savingPhoto, setSavingPhoto] = useState(false);

  // Performance (SETT-03)
  const [batchSize, setBatchSize] = useState('');
  const [parallelism, setParallelism] = useState('');
  const [savingPerf, setSavingPerf] = useState(false);

  // Stale Policy (SETT-04)
  const [stalePolicy, setStalePolicy] = useState('');
  const [staleHoldDays, setStaleHoldDays] = useState('');
  const [savingStale, setSavingStale] = useState(false);

  // Maintenance
  const [resetDialogOpen, setResetDialogOpen] = useState(false);
  const [resetting, setResetting] = useState(false);

  // Initialize from settings
  useEffect(() => {
    if (settings) {
      const get = (key: string) => settings.find((s) => s.key === key)?.value ?? '';
      setCronExpression(get('sync_schedule_cron'));
      setPhotoSyncMode(get('photo_sync_mode'));
      const photoCronSetting = settings.find(s => s.key === 'photo_sync_cron');
      if (photoCronSetting) setPhotoCron(photoCronSetting.value);
      const autoTriggerSetting = settings.find(s => s.key === 'photo_sync_auto_trigger');
      if (autoTriggerSetting) setPhotoAutoTrigger(autoTriggerSetting.value === 'true');
      setBatchSize(get('batch_size'));
      setParallelism(get('parallelism'));
      setStalePolicy(get('stale_policy_default'));
      setStaleHoldDays(get('stale_hold_days_default'));
    }
  }, [settings]);

  async function saveSchedule() {
    setSavingSchedule(true);
    try {
      await updateSettings.mutateAsync({
        settings: [{ key: 'sync_schedule_cron', value: cronExpression }],
      });
      toast.success('Settings saved.');
    } catch {
      toast.error('Something went wrong. Please try again.');
    } finally {
      setSavingSchedule(false);
    }
  }

  async function savePhotoSync() {
    setSavingPhoto(true);
    try {
      const settingsToSave = [
        { key: 'photo_sync_mode', value: photoSyncMode },
      ];
      if (photoSyncMode === 'separate_pass') {
        settingsToSave.push(
          { key: 'photo_sync_cron', value: photoCron },
          { key: 'photo_sync_auto_trigger', value: String(photoAutoTrigger) },
        );
      }
      await updateSettings.mutateAsync({ settings: settingsToSave });
      toast.success('Photo sync settings saved.');
    } catch {
      toast.error('Failed to save photo sync settings.');
    } finally {
      setSavingPhoto(false);
    }
  }

  async function savePerformance() {
    setSavingPerf(true);
    try {
      await updateSettings.mutateAsync({
        settings: [
          { key: 'batch_size', value: batchSize },
          { key: 'parallelism', value: parallelism },
        ],
      });
      toast.success('Settings saved.');
    } catch {
      toast.error('Something went wrong. Please try again.');
    } finally {
      setSavingPerf(false);
    }
  }

  async function handleResetAllHashes() {
    setResetting(true);
    try {
      const result = await api.sync.resetAllHashes();
      toast.success(`Reset ${result.count} contact states. Run a sync to apply changes.`);
      setResetDialogOpen(false);
    } catch {
      toast.error('Failed to reset hashes. Please try again.');
    } finally {
      setResetting(false);
    }
  }

  async function saveStalePolicy() {
    setSavingStale(true);
    try {
      const settingsToUpdate = [
        { key: 'stale_policy_default', value: stalePolicy },
      ];
      if (stalePolicy === 'flag_hold') {
        settingsToUpdate.push({
          key: 'stale_hold_days_default',
          value: staleHoldDays,
        });
      }
      await updateSettings.mutateAsync({ settings: settingsToUpdate });
      toast.success('Settings saved.');
    } catch {
      toast.error('Something went wrong. Please try again.');
    } finally {
      setSavingStale(false);
    }
  }

  if (isLoading) {
    return (
      <div>
        <PageHeader
          title="Settings"
          description="Configure sync behavior and system defaults."
        />
        <div className="flex flex-col gap-6 mt-6">
          {Array.from({ length: 4 }).map((_, i) => (
            <Skeleton key={i} className="h-48 w-full rounded-xl" />
          ))}
        </div>
      </div>
    );
  }

  return (
    <div>
      <PageHeader
        title="Settings"
        description="Configure sync behavior and system defaults."
      />

      <div className="flex flex-col gap-6 mt-6">
        {/* Card 1: Sync Schedule (SETT-01) */}
        <SettingsCard
          title="Sync Schedule"
          description="Configure how often the sync engine runs automatically."
          onSave={saveSchedule}
          isSaving={savingSchedule}
        >
          <div className="space-y-3">
            <Label>Schedule</Label>
            <div className="grid grid-cols-2 gap-2">
              {scheduleOptions.map((opt) => (
                <button
                  key={opt.value}
                  type="button"
                  onClick={() => setCronExpression(opt.value)}
                  className={`rounded-lg border px-3 py-2 text-left text-sm transition-colors ${
                    cronExpression === opt.value
                      ? 'border-gold bg-gold/10 text-navy font-medium'
                      : 'border-input hover:border-gold/50'
                  }`}
                >
                  {opt.label}
                </button>
              ))}
              <button
                type="button"
                onClick={() => {
                  if (isKnownSchedule(cronExpression)) setCronExpression('');
                }}
                className={`rounded-lg border px-3 py-2 text-left text-sm transition-colors ${
                  !isKnownSchedule(cronExpression)
                    ? 'border-gold bg-gold/10 text-navy font-medium'
                    : 'border-input hover:border-gold/50'
                }`}
              >
                Custom
              </button>
            </div>
            {!isKnownSchedule(cronExpression) && (
              <div className="space-y-1">
                <Label htmlFor="cron-expression">Cron Expression</Label>
                <Input
                  id="cron-expression"
                  value={cronExpression}
                  onChange={(e) => setCronExpression(e.target.value)}
                  placeholder="0 */4 * * *"
                />
                <p className="text-xs text-text-muted">
                  Standard cron format: minute hour day month weekday
                </p>
              </div>
            )}
          </div>
        </SettingsCard>

        {/* Card 2: Photo Sync (SETT-02) */}
        <SettingsCard
          title="Photo Sync"
          description="Configure how contact photos are synchronized."
          onSave={savePhotoSync}
          isSaving={savingPhoto}
        >
          <div className="space-y-3">
            <Label>Photo Sync Mode</Label>
            <div className="space-y-2">
              {photoSyncOptions.map((opt) => (
                <button
                  key={opt.value}
                  type="button"
                  onClick={() => setPhotoSyncMode(opt.value)}
                  className={`w-full rounded-lg border px-4 py-3 text-left transition-colors ${
                    photoSyncMode === opt.value
                      ? 'border-gold bg-gold/10'
                      : 'border-input hover:border-gold/50'
                  }`}
                >
                  <div className={`text-sm font-medium ${photoSyncMode === opt.value ? 'text-navy' : ''}`}>
                    {opt.label}
                  </div>
                  <div className="text-xs text-text-muted mt-0.5">
                    {opt.description}
                  </div>
                </button>
              ))}
            </div>
            {photoSyncMode === 'separate_pass' && (
              <div className="space-y-4 mt-4">
                <Separator />
                <div className="space-y-2">
                  <Label>Photo Sync Schedule</Label>
                  <div className="grid grid-cols-2 gap-2">
                    {photoScheduleOptions.map((opt) => (
                      <button
                        key={opt.value}
                        type="button"
                        onClick={() => setPhotoCron(opt.value)}
                        className={`rounded-lg border px-3 py-2 text-left text-sm transition-colors ${
                          photoCron === opt.value
                            ? 'border-gold bg-gold/10 text-navy font-medium'
                            : 'border-input hover:border-gold/50'
                        }`}
                      >
                        {opt.label}
                      </button>
                    ))}
                    <button
                      type="button"
                      onClick={() => {
                        if (isKnownPhotoSchedule(photoCron)) setPhotoCron('');
                      }}
                      className={`rounded-lg border px-3 py-2 text-left text-sm transition-colors ${
                        !isKnownPhotoSchedule(photoCron)
                          ? 'border-gold bg-gold/10 text-navy font-medium'
                          : 'border-input hover:border-gold/50'
                      }`}
                    >
                      Custom
                    </button>
                  </div>
                  {!isKnownPhotoSchedule(photoCron) && (
                    <div className="space-y-1">
                      <Label htmlFor="photo-cron-expression">Cron Expression</Label>
                      <Input
                        id="photo-cron-expression"
                        value={photoCron}
                        onChange={(e) => setPhotoCron(e.target.value)}
                        placeholder="0 */6 * * *"
                      />
                      <p className="text-xs text-text-muted">
                        Standard cron format: minute hour day month weekday
                      </p>
                    </div>
                  )}
                </div>
                <div className="flex items-center justify-between">
                  <div>
                    <Label htmlFor="photo-auto-trigger">Auto-trigger after contact sync</Label>
                    <p className="text-xs text-text-muted">
                      Automatically run photo sync after each contact sync completes.
                    </p>
                  </div>
                  <Switch
                    id="photo-auto-trigger"
                    checked={photoAutoTrigger}
                    onCheckedChange={setPhotoAutoTrigger}
                  />
                </div>
              </div>
            )}
          </div>
        </SettingsCard>

        {/* Card 3: Performance (SETT-03) */}
        <SettingsCard
          title="Performance"
          description="Tune batch processing and concurrent operations."
          onSave={savePerformance}
          isSaving={savingPerf}
        >
          <div className="space-y-4">
            <div className="grid grid-cols-2 gap-4">
              <div className="space-y-2">
                <Label htmlFor="batch-size">Batch Size</Label>
                <Input
                  id="batch-size"
                  type="number"
                  min={1}
                  max={200}
                  value={batchSize}
                  onChange={(e) => setBatchSize(e.target.value)}
                />
                <p className="text-xs text-text-muted">
                  Number of contacts processed per Graph API call. Higher values are faster but increase memory usage. Recommended: 50.
                </p>
              </div>
              <div className="space-y-2">
                <Label htmlFor="parallelism">Parallelism</Label>
                <Input
                  id="parallelism"
                  type="number"
                  min={1}
                  max={10}
                  value={parallelism}
                  onChange={(e) => setParallelism(e.target.value)}
                />
                <p className="text-xs text-text-muted">
                  Number of user mailboxes processed simultaneously. Higher values are faster but increase Graph API load. Recommended: 4–8.
                </p>
              </div>
            </div>
          </div>
        </SettingsCard>

        {/* Card 4: Stale Contact Policy (SETT-04) */}
        <SettingsCard
          title="Stale Contact Policy"
          description="Configure how contacts are handled when removed from the source DDG."
          onSave={saveStalePolicy}
          isSaving={savingStale}
        >
          <div className="space-y-4">
            <div className="space-y-2">
              <Label>Default Policy</Label>
              <Select
                value={stalePolicy}
                onValueChange={(val) => setStalePolicy(val as string)}
              >
                <SelectTrigger className="w-full">
                  <SelectValue placeholder="Select policy" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="auto_remove">
                    Auto Remove
                  </SelectItem>
                  <SelectItem value="flag_hold">
                    Flag &amp; Hold
                  </SelectItem>
                  <SelectItem value="leave">
                    Leave in Place
                  </SelectItem>
                </SelectContent>
              </Select>
              {stalePolicy === 'auto_remove' && (
                <p className="text-xs text-text-muted">
                  Automatically delete stale contacts from target mailboxes.
                </p>
              )}
              {stalePolicy === 'flag_hold' && (
                <p className="text-xs text-text-muted">
                  Flag stale contacts and hold for a configurable number of days
                  before removing.
                </p>
              )}
              {stalePolicy === 'leave' && (
                <p className="text-xs text-text-muted">
                  Leave stale contacts untouched in target mailboxes.
                </p>
              )}
            </div>

            {stalePolicy === 'flag_hold' && (
              <div className="space-y-2">
                <Label htmlFor="hold-days">Hold Days</Label>
                <Input
                  id="hold-days"
                  type="number"
                  min={1}
                  max={365}
                  value={staleHoldDays}
                  onChange={(e) => setStaleHoldDays(e.target.value)}
                />
                <p className="text-sm text-text-muted">
                  Number of days to hold flagged contacts before automatic
                  removal.
                </p>
              </div>
            )}
          </div>
        </SettingsCard>

        {/* Card 5: Maintenance */}
        <SettingsCard
          title="Maintenance"
          description="System maintenance and troubleshooting tools."
        >
          <div className="space-y-2">
            <Label>Force Full Re-Sync</Label>
            <p className="text-sm text-text-muted">
              Clears all cached data so the next sync re-writes every contact. Use after changing field behaviors or fixing data issues.
            </p>
            <Button
              variant="destructive"
              onClick={() => setResetDialogOpen(true)}
              className="mt-2"
            >
              Force Full Re-Sync
            </Button>
          </div>
        </SettingsCard>
      </div>

      <ConfirmDialog
        open={resetDialogOpen}
        onOpenChange={setResetDialogOpen}
        title="Force full re-sync"
        description="This will cause the next sync run to re-write all contacts across all tunnels. This may take longer than usual. Continue?"
        confirmLabel="Reset All Hashes"
        dismissLabel="Cancel"
        variant="destructive"
        onConfirm={handleResetAllHashes}
        isLoading={resetting}
      />
    </div>
  );
}
