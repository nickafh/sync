'use client';

import { useState, useEffect } from 'react';
import { useSettings, useUpdateSettings } from '@/hooks/use-settings';
import { toast } from 'sonner';
import { PageHeader } from '@/components/PageHeader';
import { SettingsCard } from '@/components/SettingsCard';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
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

const cronPresets = [
  { label: 'Every 2 hours', value: '0 */2 * * *' },
  { label: 'Every 4 hours', value: '0 */4 * * *' },
  { label: 'Every 6 hours', value: '0 */6 * * *' },
  { label: 'Daily at midnight', value: '0 0 * * *' },
  { label: 'Daily at 6 AM', value: '0 6 * * *' },
];

function describeCron(cron: string): string {
  const map: Record<string, string> = {
    '0 */2 * * *': 'Runs every 2 hours',
    '0 */4 * * *': 'Runs every 4 hours',
    '0 */6 * * *': 'Runs every 6 hours',
    '0 0 * * *': 'Runs daily at midnight',
    '0 6 * * *': 'Runs daily at 6 AM',
    '0 */1 * * *': 'Runs every hour',
    '0 */8 * * *': 'Runs every 8 hours',
    '0 */12 * * *': 'Runs every 12 hours',
  };
  return map[cron] ?? `Custom schedule: ${cron}`;
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
            <Label htmlFor="cron-expression">Cron Expression</Label>
            <Input
              id="cron-expression"
              value={cronExpression}
              onChange={(e) => setCronExpression(e.target.value)}
              placeholder="0 */4 * * *"
            />
            <p className="text-sm text-text-muted">
              {describeCron(cronExpression)}
            </p>
            <div className="flex flex-wrap gap-2 pt-1">
              {cronPresets.map((preset) => (
                <button
                  key={preset.value}
                  type="button"
                  className="text-xs text-gold hover:underline"
                  onClick={() => setCronExpression(preset.value)}
                >
                  {preset.label}
                </button>
              ))}
            </div>
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
            <Select
              value={photoSyncMode}
              onValueChange={(val) => setPhotoSyncMode(val as string)}
            >
              <SelectTrigger className="w-full">
                <SelectValue placeholder="Select mode" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="included">
                  Included with Contact Sync
                </SelectItem>
                <SelectItem value="separate_pass">Separate Pass</SelectItem>
                <SelectItem value="disabled">Disabled</SelectItem>
              </SelectContent>
            </Select>
            {photoSyncMode === 'separate_pass' && (
              <div className="space-y-4 mt-4">
                <Separator />
                <div className="space-y-2">
                  <Label>Photo Sync Schedule (Cron)</Label>
                  <Input
                    value={photoCron}
                    onChange={(e) => setPhotoCron(e.target.value)}
                    placeholder="0 */6 * * *"
                  />
                  <p className="text-xs text-text-muted">
                    Cron expression for photo sync schedule. Default: every 6 hours.
                  </p>
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
              </div>
            </div>
            <p className="text-sm text-text-muted">
              Batch size controls items per API batch. Parallelism controls
              concurrent mailbox processing.
            </p>
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
      </div>
    </div>
  );
}
