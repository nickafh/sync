import { describe, it, expect } from 'vitest';
import { buildManualTriggerRequest } from './sync-trigger';

describe('buildManualTriggerRequest', () => {
  it('is a plain manual run of every tunnel by default', () => {
    expect(buildManualTriggerRequest(false)).toEqual({
      runType: 'manual',
      isDryRun: false,
      tunnelIds: null,
      auditFolders: false,
    });
  });

  it('carries the audit flag when asked', () => {
    expect(buildManualTriggerRequest(true)).toEqual({
      runType: 'manual',
      isDryRun: false,
      tunnelIds: null,
      auditFolders: true,
    });
  });
});
